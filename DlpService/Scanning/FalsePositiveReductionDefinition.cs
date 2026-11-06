// [Thay đổi] File mới (bước 5.7 cấu trúc): cấu hình chung giảm báo động giả, tách khỏi context_required ở cấp pattern
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

/// <summary>Cấu hình thô đọc từ trường "false_positive_reduction" trong file rule JSON.</summary>
public class FalsePositiveReductionDefinition
{
    [JsonPropertyName("min_context_window")]
    public int? MinContextWindow { get; set; }

    [JsonPropertyName("exclude_if_no_keywords")]
    public bool? ExcludeIfNoKeywords { get; set; }

    // [Thay đổi] Công thức Go: điểm cộng khi gần số thẻ có CVV + ngày hết hạn (VD 0.07 ở rule credit-card-*.json)
    [JsonPropertyName("cvv_expiry_boost")]
    public double? CvvExpiryBoost { get; set; }
}
