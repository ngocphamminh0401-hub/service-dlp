// [Thay đổi checklist Bậc 2 - "Compound rules"]: File mới — bản "sẵn sàng" của CompoundRuleDefinition
// (exclude_context_patterns đã biên dịch thành Regex), do CompoundRuleModule tự nạp và dùng.
using System.Text.RegularExpressions;

namespace DlpService.Classification;

/// <summary>1 compound rule đã kiểm tra hợp lệ và sẵn sàng để CompoundRuleModule dùng đánh giá.</summary>
public class CompoundRule
{
    public required string Name { get; init; }
    public required List<string> Conditions { get; init; }
    public string? MinComponentLevel { get; init; }
    public List<string>? ContextConditions { get; init; }
    public List<Regex> ExcludeContextPatterns { get; init; } = new();
    public required string ResultLevel { get; init; }
    public string? ViolationType { get; init; }
    public string? AlertPriority { get; init; }
}
