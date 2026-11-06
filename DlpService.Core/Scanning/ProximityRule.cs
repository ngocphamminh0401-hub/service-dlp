// [Thay đổi] File mới (Bậc 3 #2 - proximity_window): bản "sẵn sàng" của proximity, gắn vào SensitivePattern.
// Dựng từ 1 trong 2 nguồn: "proximity_window" cấp rule, hoặc "require_keyword_within" cấp pattern con.
namespace DlpService.Scanning;

/// <summary>Proximity đã sẵn sàng: từ khoá phải nằm trong MaxChars ký tự (và cùng câu nếu SameSentence) quanh chỗ khớp.</summary>
public class ProximityRule
{
    /// <summary>Khoảng cách tối đa (ký tự) tính ra mỗi bên chỗ khớp. RuleLoader đảm bảo &gt; 0.</summary>
    public required int MaxChars { get; init; }

    /// <summary>Từ khoá phải nằm cùng câu với chỗ khớp.</summary>
    public bool SameSentence { get; init; }

    /// <summary>true = xét cả từ khoá secondary; false = chỉ primary (keyword_scope = "primary").</summary>
    public bool IncludeSecondary { get; init; }

    /// <summary>Cho phép "chế độ bảng": rule khớp nhiều lần + có từ khoá ở bất kỳ đâu trong file thì bỏ qua proximity.
    /// Chỉ bật cho proximity_window cấp rule; require_keyword_within cấp pattern (ngày sinh, toạ độ) luôn chặt.</summary>
    public bool AllowTableFallback { get; init; }
}
