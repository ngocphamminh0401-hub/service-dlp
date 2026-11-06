// [Thay đổi] File mới (bước 5.7 cấu trúc): 1 pattern con bên trong rule nhiều-pattern (mảng "patterns" trong JSON)
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

public class PatternDefinition
{
    [JsonPropertyName("regex")]
    public string? Regex { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("context_required")]
    public bool ContextRequired { get; set; } = false;

    [JsonPropertyName("validators")]
    public List<string>? Validators { get; set; }

   [JsonPropertyName("override_level")]
    public string? OverrideLevel { get; set; }

    // [Thay đổi] Bậc 3 #2: proximity cấp pattern con — từ khoá phải nằm trong N ký tự quanh chỗ khớp (không xét câu).
    // Nếu có, ưu tiên hơn proximity_window cấp rule.
    [JsonPropertyName("require_keyword_within")]
    public int? RequireKeywordWithin { get; set; }
}
