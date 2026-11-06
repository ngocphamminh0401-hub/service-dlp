namespace DlpService.Classification;

/// <summary>Ý kiến của 1 module về mức độ nhạy cảm của file.</summary>
public class ClassificationResult
{
    /// <summary>Tên domain đưa ra kết luận này, ví dụ "Finance".</summary>
    public required string Domain { get; init; }

    /// <summary>Mức độ nhạy cảm theo domain đó.</summary>
    public required SensitivityLevel Level { get; init; }

    /// <summary>Lý do đánh mức này, để log và để người dùng hiểu (không chứa dữ liệu nhạy cảm nguyên văn).</summary>
    public List<string> Reasons { get; init; } = new();
}

