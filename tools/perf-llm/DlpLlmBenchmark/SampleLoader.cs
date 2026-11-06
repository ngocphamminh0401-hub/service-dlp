// [Thay đổi] File mới: bản RÚT GỌN của tools/eval/DlpBaseline/SampleLoader.cs — chỉ lấy đủ trường cần cho benchmark
// LLM (id, nhãn thật, đường dẫn docx). Không sửa/tham chiếu trực tiếp DlpBaseline (class ở đó là internal, tránh
// phải public hoá xuyên project chỉ để dùng lại vài dòng).
using System.Text.Json;
using DlpService.Classification;

namespace DlpLlmBenchmark;

internal sealed record BenchSample(string Id, string Domain, string Label, SensitivityLevel Truth, string DocPath);

internal static class SampleLoader
{
    public static List<BenchSample> Load(string rawdataFolder)
    {
        var samples = new List<BenchSample>();
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
                if (sample != null)
                {
                    samples.Add(sample);
                }
            }
        }
        return samples;
    }

    /// <summary>Lấy tối đa limit mẫu, chia đều cho các thư mục con (domain__label) — giữ tỷ lệ các nhãn thay vì lấy
    /// dồn hết vào 1 domain (mục 13.1: "chia tập... tránh overfit/lệch phân phối").</summary>
    public static List<BenchSample> TakeStratified(List<BenchSample> samples, int limit)
    {
        var groups = samples.GroupBy(s => (s.Domain, s.Label)).ToList();
        int perGroup = Math.Max(1, (int)Math.Ceiling(limit / (double)groups.Count));
        return groups.SelectMany(g => g.Take(perGroup)).Take(limit).ToList();
    }

    private static SensitivityLevel? MapLabel(string label) => label.Trim().ToLowerInvariant() switch
    {
        "public" => SensitivityLevel.Public,
        "internal" => SensitivityLevel.Internal,
        "confidential" => SensitivityLevel.Confidential,
        "top_secret" or "secret" or "restricted" => SensitivityLevel.Restricted,
        _ => null
    };

    private static BenchSample? TryParse(string line, string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string? id = GetString(root, "id");
            string? label = GetString(root, "label");
            string? docxPath = GetString(root, "docx_path");
            if (id == null || label == null || docxPath == null || MapLabel(label) is not SensitivityLevel truth)
            {
                return null;
            }
            string fullPath = Path.Combine(folder, Path.GetFileName(docxPath));
            if (!File.Exists(fullPath))
            {
                return null;
            }
            return new BenchSample(id, GetString(root, "domain") ?? "?", label, truth, fullPath);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
