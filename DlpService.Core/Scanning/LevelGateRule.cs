// [Thay đổi] File mới (bước 5.7 cấu trúc): bản "sẵn sàng" của LevelGateDefinition, gắn vào SensitivePattern
namespace DlpService.Scanning;

/// <summary>Level gate đã sẵn sàng: cần đủ bằng chứng củng cố mới giữ mức gốc, không thì hạ xuống FallbackLevel.</summary>
public class LevelGateRule
{
    public required bool RequireCorroboration { get; init; }
    public required string FallbackLevel { get; init; }

    // [Thay đổi] Bậc 1 (cải thiện engine): giới hạn gate chỉ áp cho một số pattern con cụ thể (xem
    // LevelGateDefinition.AppliesToPatterns/ExemptPatterns). Cả hai rỗng/null = áp dụng cho mọi pattern con,
    // giữ nguyên hành vi cũ.
    public List<string>? AppliesToPatterns { get; init; }
    public List<string>? ExemptPatterns { get; init; }

    // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: xem giải thích ở LevelGateDefinition.
    public List<string>? RequiredCorroborationTags { get; init; }
    public bool StrictTagsOnly { get; init; }

    // [Thay đổi] Bậc 3 #3: xem giải thích ở LevelGateDefinition.MinConfidenceBypass
    public double? MinConfidenceBypass { get; init; }

    /// <summary>Kiểm tra gate có áp dụng cho pattern ở vị trí patIdx (0-indexed) hay không.
    /// Độ ưu tiên: nếu AppliesToPatterns không rỗng, chỉ áp cho pattern có trong danh sách đó;
    /// ExemptPatterns luôn loại trừ thêm (kể cả khi đã có trong AppliesToPatterns).</summary>
    public bool AppliesToPattern(int patIdx)
    {
        string ref_ = $"pattern_{patIdx + 1}";

        if (AppliesToPatterns is { Count: > 0 } && !AppliesToPatterns.Contains(ref_))
        {
            return false;
        }

        if (ExemptPatterns != null && ExemptPatterns.Contains(ref_))
        {
            return false;
        }

        return true;
    }
}
