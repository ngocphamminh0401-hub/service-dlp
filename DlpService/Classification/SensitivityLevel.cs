namespace DlpService.Classification;

/// <summary>4 mức độ nhạy cảm của 1 file, tăng dần từ Public đến Restricted.</summary>
public enum SensitivityLevel
{
    Public,
    Internal,
    Confidential,
    Restricted
}

// [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: gộp logic parse chuỗi Severity (trước đây lặp lại
// y hệt trong FinanceModule/HrModule/OrgModule/PiiModule, mỗi module tự có 1 bản riêng) thành 1 hàm dùng chung.
// CompoundRuleModule (mới) cần đúng logic này để so sánh mức giữa các match — nếu không gộp sẽ có 5 bản trùng lặp.
public static class SensitivityLevelExtensions
{
    /// <summary>Chuyển chuỗi Severity của rule (không phân biệt hoa/thường, cả kiểu cũ "High" lẫn kiểu PDF "SECRET")
    /// thành SensitivityLevel. Không nhận ra thì coi là Public (an toàn nhất khi không chắc).</summary>
    public static SensitivityLevel ParseSensitivityLevel(this string severity) => severity.Trim().ToUpperInvariant() switch
    {
        "RESTRICTED" or "SECRET" or "HIGH" => SensitivityLevel.Restricted,
        "CONFIDENTIAL" or "MEDIUM" => SensitivityLevel.Confidential,
        "INTERNAL" => SensitivityLevel.Internal,
        "PUBLIC" => SensitivityLevel.Public,
        _ => SensitivityLevel.Public
    };
}
