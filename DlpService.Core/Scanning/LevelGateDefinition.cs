// [Thay đổi] File mới (bước 5.7 cấu trúc): hạ mức nếu rule thiếu bằng chứng củng cố
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

/// <summary>Cấu hình gate thô đọc từ trường "level_gate" trong file rule JSON.</summary>
public class LevelGateDefinition
{
    [JsonPropertyName("require_corroboration")]
    public bool RequireCorroboration { get; set; } = false;

    [JsonPropertyName("fallback_level")]
    public string? FallbackLevel { get; set; }

    // [Thay đổi] Bậc 1 (cải thiện engine): giới hạn gate chỉ áp dụng cho MỘT SỐ pattern con cụ thể (tham chiếu
    // "pattern_N", 1-indexed theo thứ tự trong "patterns" của JSON). Dùng khi rule trộn lẫn pattern dễ FP (dãy số
    // trần, cần gate) với pattern có cấu trúc đặc trưng riêng (label rõ ràng, MRZ, checksum... nên miễn gate).
    // Cả hai rỗng (mặc định) = gate áp dụng cho MỌI pattern của rule — không đổi hành vi hiện có.
    [JsonPropertyName("applies_to_patterns")]
    public List<string>? AppliesToPatterns { get; set; }

    [JsonPropertyName("exempt_patterns")]
    public List<string>? ExemptPatterns { get; set; }

    // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: nếu khai, THAY THẾ 2 điều kiện củng cố mặc
    // định (≥2 lần khớp / có rule khác cùng nội dung ≥CONFIDENTIAL) bằng yêu cầu CỤ THỂ hơn — phải có ≥1 chỗ khớp
    // (bất kể rule nào) mang 1 trong các tag này. Dùng khi 2 điều kiện mặc định quá LỎNG (VD health_001: chỉ giữ
    // mức nếu có định danh cá nhân đi kèm, không phải bất kỳ tín hiệu CONFIDENTIAL+ nào khác trong nội dung).
    [JsonPropertyName("required_corroboration_tags")]
    public List<string>? RequiredCorroborationTags { get; set; }

    // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: nếu true VÀ RequiredCorroborationTags khai,
    // LOẠI BỎ luôn điều kiện "≥2 lần khớp" và "validator pass" — corroborated CHỈ còn dựa vào tag cụ thể. Dùng khi
    // "≥2 lần khớp" không phải bằng chứng đáng tin cho rule này (VD insurance_context_001: văn bản điều khoản bảo
    // hiểm PUBLIC dễ dàng nhắc "bồi thường" ≥2 lần một cách bình thường).
    [JsonPropertyName("strict_tags_only")]
    public bool StrictTagsOnly { get; set; } = false;

    // [Thay đổi] Bậc 3 #3: nếu độ tin cậy ĐỘNG của 1 chỗ khớp (confidence × weight + boost từ khoá) đạt ngưỡng này
    // thì coi là đã đủ bằng chứng củng cố, không bị gate hạ. Null = không có bypass (trước đây là dead field).
    [JsonPropertyName("min_confidence_bypass")]
    public double? MinConfidenceBypass { get; set; }
}
