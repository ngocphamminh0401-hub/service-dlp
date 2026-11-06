// [Thay đổi] File mới (mục 6.2 + mục 8 "Model bị thay"): manifest.json đi cùng gói model khi phân phối offline —
// liệt kê mọi file model kèm SHA-256. LLM host đọc file này và băm lại từng file THẬT trên đĩa để phát hiện model
// bị thay/hỏng trước khi cho chạy (Load/Verify). Generate/Save chỉ dùng 1 lần khi đóng gói/thiết lập model, KHÔNG
// chạy tự động lúc "serve".
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DlpLlmHost;

public sealed record ModelManifestEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record ModelManifest(
    [property: JsonPropertyName("model_ver")] string ModelVer,
    [property: JsonPropertyName("files")] IReadOnlyList<ModelManifestEntry> Files);

public static class ModelManifestChecker
{
    private const string ManifestFileName = "manifest.json";

    public static string ManifestPath(string modelDir) => Path.Combine(modelDir, ManifestFileName);

    public static ModelManifest Load(string modelDir)
    {
        string path = ManifestPath(modelDir);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Không tìm thấy manifest.json trong thư mục model: {path}");
        }
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ModelManifest>(json)
            ?? throw new InvalidDataException($"manifest.json rỗng hoặc sai định dạng: {path}");
    }

    /// <summary>So SHA-256 từng file trong manifest với file thật trên đĩa. Trả về danh sách lỗi (rỗng = khớp hết).
    /// [Thay đổi] Nhận <paramref name="cancellationToken"/>: kiểm tra sau mỗi khối đọc để service bị dừng giữa lúc
    /// đang băm (hash chạy nền sau khi báo RUNNING) không bị treo chờ băm xong 4,6 GB. Lỗi ĐỌC file (IOException,
    /// UnauthorizedAccessException) KHÔNG nằm trong danh sách lỗi mà ném thẳng ra ngoài — bên gọi phân biệt được "model
    /// bị thay/hỏng" (có trong danh sách) với "đọc tạm thời không được" (exception) để quyết định thử lại.</summary>
    public static IReadOnlyList<string> Verify(string modelDir, ModelManifest manifest, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        foreach (var entry in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.Combine(modelDir, entry.Path);
            if (!File.Exists(fullPath))
            {
                errors.Add($"Thiếu file: {entry.Path}");
                continue;
            }
            string actual = ComputeSha256(fullPath, cancellationToken);
            if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Sai SHA-256: {entry.Path} (manifest={entry.Sha256}, thực tế={actual})");
            }
        }
        return errors;
    }

    /// <summary>Quét thư mục model, băm mọi file hiện có (trừ chính manifest.json và thư mục cache của lệnh tải
    /// về), dựng manifest mới. Chưa ghi ra đĩa — gọi Save riêng.</summary>
    public static ModelManifest Generate(string modelDir, string modelVer)
    {
        var files = new List<ModelManifestEntry>();
        foreach (string file in Directory.EnumerateFiles(modelDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(modelDir, file).Replace('\\', '/');
            if (relative == ManifestFileName || relative.StartsWith(".cache/", StringComparison.Ordinal))
            {
                continue;
            }
            files.Add(new ModelManifestEntry(relative, ComputeSha256(file)));
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ModelManifest(modelVer, files);
    }

    public static void Save(string modelDir, ModelManifest manifest)
    {
        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ManifestPath(modelDir), json);
    }

    // [Thay đổi] Băm theo từng khối 1 MB (thay vì ComputeHash(stream) một mạch) để kiểm tra huỷ giữa chừng.
    // Kết quả SHA-256 giống hệt cách cũ nên manifest.json đã sinh trước đó vẫn khớp.
    private static string ComputeSha256(string filePath, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha256.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
    }
}
