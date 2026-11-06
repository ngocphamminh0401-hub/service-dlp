// [Thay đổi] File mới (bước 5.7 cấu trúc): loại chỗ khớp nếu gần đó có từ phủ định
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

/// <summary>Cấu hình negation filter thô đọc từ trường "negation_filter" trong file rule JSON.</summary>
public class NegationFilterDefinition
{
    [JsonPropertyName("window_chars")]
    public int WindowChars { get; set; } = 30;

    [JsonPropertyName("trigger_words")]
    public List<string> TriggerWords { get; set; } = new();

    // [Thay đổi] Bậc 1 (cải thiện engine): RegexScanner giờ phân biệt thật 2 giá trị: "suppress" (loại hẳn
    // chỗ khớp, hành vi mặc định/cũ) và "downgrade_to_gate" (hạ về level_gate.fallback_level, KHÔNG loại khỏi
    // kết quả). Giá trị khác hoặc để trống vẫn xử lý như "suppress" để không đổi hành vi các rule cũ.
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("applies_to_patterns")]
    public List<string>? AppliesToPatterns { get; set; }
}
