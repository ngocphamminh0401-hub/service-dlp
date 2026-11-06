// [Thay đổi] File mới (Bậc 3 #1 - escalation): khuôn JSON thô của trường "escalation" trong file rule
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

/// <summary>Cấu hình escalation thô đọc từ trường "escalation" trong file rule JSON.</summary>
public class EscalationDefinition
{
    /// <summary>Từ khoá "nguy hiểm" — nếu xuất hiện gần chỗ khớp thì nâng mức, ví dụ ["mật khẩu", "password"].</summary>
    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }

    /// <summary>Mức được nâng lên khi gặp từ khoá, ví dụ "SECRET".</summary>
    [JsonPropertyName("escalate_to")]
    public string? EscalateTo { get; set; }
}
