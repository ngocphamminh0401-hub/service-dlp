// [Thay đổi] File mới (bước 5.7 cấu trúc): bản "sẵn sàng" của NegationFilterDefinition, gắn vào SensitivePattern
namespace DlpService.Scanning;

/// <summary>Negation filter đã sẵn sàng: gần chỗ khớp có từ này thì xử lý theo Action.</summary>
public class NegationRule
{
    public required int WindowChars { get; init; }
    public required List<string> TriggerWords { get; init; }

    // [Thay đổi] Bậc 1 (cải thiện engine): "suppress" (mặc định) loại hẳn chỗ khớp; "downgrade_to_gate" hạ về
    // level_gate.fallback_level nhưng vẫn giữ trong kết quả. Trước đây field này bị RuleLoader đọc vào rồi bỏ,
    // khiến mọi rule khai "downgrade_to_gate" (income, watermark, internal-ip, health...) bị xử lý nhầm như
    // "suppress" — đây là một bugfix thật sự, không chỉ thêm tính năng mới.
    public required string Action { get; init; }

    /// <summary>Danh sách "pattern_N" (1-indexed) mà filter này áp dụng; rỗng/null = áp dụng cho mọi pattern con.</summary>
    public List<string>? AppliesToPatterns { get; init; }

    /// <summary>Kiểm tra filter có áp dụng cho pattern ở vị trí patIdx (0-indexed) hay không.</summary>
    public bool AppliesTo(int patIdx)
    {
        if (AppliesToPatterns == null || AppliesToPatterns.Count == 0)
        {
            return true;
        }

        return AppliesToPatterns.Contains($"pattern_{patIdx + 1}");
    }
}
