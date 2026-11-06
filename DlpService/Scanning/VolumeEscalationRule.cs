// [Thay đổi] File mới (Bậc 3 #4 - volume_escalation): bản "sẵn sàng" của VolumeEscalationDefinition, gắn vào SensitivePattern
namespace DlpService.Scanning;

/// <summary>Volume escalation đã sẵn sàng: file có đủ nhiều giá trị KHÁC NHAU của rule thì nâng mức mọi chỗ khớp của rule đó.</summary>
public class VolumeEscalationRule
{
    /// <summary>Các bậc ngưỡng hợp lệ, đã sắp xếp min_count GIẢM dần (RuleLoader đảm bảo không rỗng).</summary>
    public required List<(int MinCount, string EscalateTo)> Thresholds { get; init; }

    /// <summary>Mức nâng ứng với số giá trị khác nhau đếm được — lấy bậc CAO NHẤT đạt được (count ≥ min_count).
    /// Trả null nếu chưa đạt bậc nào.</summary>
    public string? LevelFor(int distinctCount)
    {
        foreach (var (minCount, escalateTo) in Thresholds)
        {
            if (distinctCount >= minCount)
            {
                return escalateTo;
            }
        }
        return null;
    }
}
