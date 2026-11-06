// [Thay đổi] File mới (Bậc 3 #5 - placeholder_exclusion): khuôn JSON thô của trường "placeholder_exclusion" trong file rule.
// Chỉ đọc phần LOẠI giá trị mẫu; doc_context_discount_keywords/factor thuộc Bậc 3 #6 (cần confidence scoring #7) nên
// chưa đọc ở đây.
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

/// <summary>Cấu hình placeholder thô đọc từ trường "placeholder_exclusion" trong file rule JSON.</summary>
public class PlaceholderExclusionDefinition
{
    /// <summary>Regex nhận diện giá trị mẫu, ví dụ "(?i)^(changeme\\d*|password\\d*)$".</summary>
    [JsonPropertyName("value_blocklist_patterns")]
    public List<string>? ValueBlocklistPatterns { get; set; }

    /// <summary>true = regex blocklist phải khớp TOÀN BỘ giá trị (engine tự bọc ^(?:...)$).</summary>
    [JsonPropertyName("value_blocklist_full_match")]
    public bool ValueBlocklistFullMatch { get; set; } = false;

    /// <summary>Giá trị test công khai, so khớp chính xác sau khi chỉ giữ chữ số, ví dụ "4111111111111111".</summary>
    [JsonPropertyName("known_test_values")]
    public List<string>? KnownTestValues { get; set; }

    // [Thay đổi] S1 giai đoạn 1: đọc 2 trường trước đây là "dead field" — dùng làm PENALTY cho điểm candidate (không loại
    // chỗ khớp, chỉ giảm điểm) khi cả tài liệu mang dấu hiệu hướng dẫn/mẫu/đào tạo.
    /// <summary>Từ khoá cho biết tài liệu là hướng dẫn/ví dụ/mẫu, ví dụ "hướng dẫn", "tutorial".</summary>
    [JsonPropertyName("doc_context_discount_keywords")]
    public List<string>? DocContextDiscountKeywords { get; set; }

    /// <summary>Hệ số nhân điểm khi tài liệu có từ khoá trên (0-1), ví dụ 0.5 = giảm một nửa.</summary>
    [JsonPropertyName("doc_context_discount_factor")]
    public double? DocContextDiscountFactor { get; set; }
}
