// [Thay đổi] File mới (Bậc 3 #2 - proximity_window): khuôn JSON thô của trường "proximity_window" cấp rule
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

/// <summary>Cấu hình proximity thô đọc từ trường "proximity_window" trong file rule JSON.</summary>
public class ProximityWindowDefinition
{
    /// <summary>Khoảng cách tối đa (ký tự) từ chỗ khớp tới từ khoá, tính ra mỗi bên.</summary>
    [JsonPropertyName("max_chars")]
    public int? MaxChars { get; set; }

    /// <summary>Từ khoá có bắt buộc nằm cùng câu với chỗ khớp hay không.</summary>
    [JsonPropertyName("same_sentence_required")]
    public bool SameSentenceRequired { get; set; } = false;

    /// <summary>Nhóm từ khoá được xét: "primary" hoặc "primary_or_secondary".</summary>
    [JsonPropertyName("keyword_scope")]
    public string? KeywordScope { get; set; }
}
