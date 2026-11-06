// [Thay đổi] File mới: đọc nhãn "đáp án" từ các file _metadata.jsonl trong rawdata.
using System.Text.Json;
using DlpService.Classification;

namespace DlpBaseline;

/// <summary>1 mẫu trong bộ dữ liệu: nhãn thật + đường dẫn bản docx và pdf (cùng nội dung).</summary>
internal sealed record Sample(
    string Id,
    string Domain,
    string Label,
    SensitivityLevel Truth,
    bool ContainsPii,
    bool IsBorderline,
    string? DocxPath,
    string? PdfPath);

/// <summary>Kết quả đọc metadata: danh sách mẫu hợp lệ + số dòng bị bỏ qua (hỏng JSON / nhãn lạ).</summary>
internal sealed record LoadResult(List<Sample> Samples, int SkippedLines);

internal static class SampleLoader
{
    /// <summary>Đọc mọi "thư mục con/_metadata.jsonl" trong rawdata, sắp xếp theo tên thư mục cho kết quả ổn định.</summary>
    public static LoadResult Load(string rawdataFolder)
    {
        var samples = new List<Sample>();
        int skipped = 0;

        foreach (var folder in Directory.EnumerateDirectories(rawdataFolder).Order(StringComparer.OrdinalIgnoreCase))
        {
            string metadataPath = Path.Combine(folder, "_metadata.jsonl");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            foreach (var line in File.ReadLines(metadataPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var sample = TryParse(line, folder);
                if (sample == null)
                {
                    skipped++;
                }
                else
                {
                    samples.Add(sample);
                }
            }
        }

        return new LoadResult(samples, skipped);
    }

    /// <summary>Lấy tối đa limit mẫu, chia đều cho các thư mục con (domain__label) để giữ tỷ lệ các nhãn.</summary>
    public static List<Sample> TakeStratified(List<Sample> samples, int limit)
    {
        var groups = samples.GroupBy(s => (s.Domain, s.Label)).ToList();
        int perGroup = (int)Math.Ceiling(limit / (double)groups.Count);
        return groups.SelectMany(g => g.Take(perGroup)).Take(limit).ToList();
    }

    /// <summary>Ghép nhãn trong dữ liệu sang mức của engine. Nhãn lạ trả null (mẫu bị bỏ qua).</summary>
    public static SensitivityLevel? MapLabel(string label) => label.Trim().ToLowerInvariant() switch
    {
        "public" => SensitivityLevel.Public,
        "internal" => SensitivityLevel.Internal,
        "confidential" => SensitivityLevel.Confidential,
        "top_secret" or "secret" or "restricted" => SensitivityLevel.Restricted,
        _ => null
    };

    private static Sample? TryParse(string line, string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            string? id = GetString(root, "id");
            string? label = GetString(root, "label");
            if (id == null || label == null || MapLabel(label) is not SensitivityLevel truth)
            {
                return null;
            }

            return new Sample(
                id,
                GetString(root, "domain") ?? "?",
                label,
                truth,
                GetBool(root, "contains_pii"),
                GetBool(root, "is_borderline"),
                ResolvePath(folder, GetString(root, "docx_path")),
                ResolvePath(folder, GetString(root, "pdf_path")));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Đường dẫn trong jsonl là đường dẫn LÚC SINH dữ liệu ("data\raw\<thư mục>\<tên file>") -> chỉ giữ tên file,
    // ghép vào thư mục chứa chính file jsonl
    private static string? ResolvePath(string folder, string? rawPath) =>
        string.IsNullOrWhiteSpace(rawPath) ? null : Path.Combine(folder, Path.GetFileName(rawPath));

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Chấp nhận cả true/false lẫn chuỗi "True"/"false"
    private static bool GetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) && parsed,
            _ => false
        };
    }
}
