// [Thay đổi] File mới (Bậc 3 #4 - volume_escalation): khuôn JSON thô của trường "volume_escalation" trong file rule
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

/// <summary>Cấu hình volume escalation thô đọc từ trường "volume_escalation" trong file rule JSON.</summary>
public class VolumeEscalationDefinition
{
    /// <summary>Các bậc ngưỡng, ví dụ [{min_count: 50, escalate_to: "CONFIDENTIAL"}, {min_count: 200, escalate_to: "SECRET"}].</summary>
    [JsonPropertyName("thresholds")]
    public List<VolumeThresholdDefinition>? Thresholds { get; set; }
}

/// <summary>1 bậc ngưỡng thô: đạt ít nhất MinCount giá trị khác nhau thì nâng lên EscalateTo.</summary>
public class VolumeThresholdDefinition
{
    [JsonPropertyName("min_count")]
    public int? MinCount { get; set; }

    [JsonPropertyName("escalate_to")]
    public string? EscalateTo { get; set; }
}
