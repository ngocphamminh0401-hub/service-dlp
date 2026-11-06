// [Thay đổi] File mới (Bậc 3 #1 - escalation): bản "sẵn sàng" của EscalationDefinition, gắn vào SensitivePattern
namespace DlpService.Scanning;

/// <summary>Escalation đã sẵn sàng: trong vùng ContextWindow quanh chỗ khớp có 1 từ khoá thì nâng mức lên EscalateTo.</summary>
public class EscalationRule
{
    /// <summary>Từ khoá kích hoạt nâng mức (so khớp không phân biệt hoa thường). RuleLoader đảm bảo không rỗng.</summary>
    public required List<string> Keywords { get; init; }

    /// <summary>Mức nâng lên. Chỉ được NÂNG — nếu thấp hơn mức gốc thì giữ mức gốc.</summary>
    public required string EscalateTo { get; init; }
}
