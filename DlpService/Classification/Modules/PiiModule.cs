// [Thay đổi] File mới: module domain "pii" (viết lại từ tài liệu rule financial/pii PDF, đặt trong rules/pii/)
// theo đúng mẫu FinanceModule.cs/OrgModule.cs — mức đọc trực tiếp từ ScanMatch.Severity (đã áp level_gate nếu
// rule có khai báo). Khác HrModule.cs (chỉ quan tâm CCCD/Email như tín hiệu hồ sơ nhân sự), module này phản
// ánh đúng mức nhạy cảm PII độc lập với domain nào đang dùng dữ liệu đó.
namespace DlpService.Classification.Modules;

/// <summary>Module domain dữ liệu cá nhân (PII): xét mọi rule pii đã nạp (rules/pii/).</summary>
public class PiiModule : IClassifierModule
{
    /// <inheritdoc/>
    public string Domain => "PII";

    // Rule nào không có trong bảng thì PII domain coi như không liên quan, bỏ qua.
    private static readonly Dictionary<string, string> KnownSignals = new()
    {
        ["CCCD"] = "Có số CCCD/CMND",
        ["Email"] = "Có địa chỉ email",
        ["PhoneVN"] = "Có số điện thoại Việt Nam",
        ["Dữ liệu sinh trắc học (vân tay, khuôn mặt, mống mắt)"] = "Có dữ liệu/nhắc đến sinh trắc học",
        ["Ngày tháng năm sinh"] = "Có ngày tháng năm sinh",
        ["PII-010 Dân tộc / Tôn giáo / Đảng viên / Lý lịch chính trị"] = "Có trường dân tộc/tôn giáo/đảng viên/lý lịch chính trị",
        ["Dữ liệu y tế / sức khỏe cá nhân"] = "Có dữ liệu y tế/sức khỏe cá nhân",
        ["PII-011 Dữ liệu vị trí / định vị / lịch sử di chuyển"] = "Có dữ liệu vị trí/định vị/lịch sử di chuyển",
        ["Hộ chiếu Việt Nam"] = "Có số hộ chiếu Việt Nam",
        ["Số BHXH / BHYT / Bảo hiểm xã hội"] = "Có số BHXH/BHYT",
        ["Họ tên người Việt Nam"] = "Có họ tên người Việt Nam",
    };

    /// <inheritdoc/>
    public ClassificationResult? Classify(ScanContext context)
    {
        var reasons = new List<string>();
        var level = SensitivityLevel.Public;

        foreach (var (patternName, reasonText) in KnownSignals)
        {
            var matchesForThisRule = context.Matches.Where(m => m.PatternName == patternName).ToList();
            if (matchesForThisRule.Count == 0)
            {
                continue;
            }

            reasons.Add(reasonText);

            // Mỗi chỗ khớp mang theo Severity riêng (có thể đã bị level_gate hạ xuống), lấy mức cao nhất trong số đó
            // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: dùng hàm dùng chung thay vì
            // ParseSeverity riêng của module này (xem SensitivityLevelExtensions).
            foreach (var match in matchesForThisRule)
            {
                var matchLevel = match.Severity.ParseSensitivityLevel();
                if (matchLevel > level)
                {
                    level = matchLevel;
                }
            }
        }

        // Không có lý do nào nghĩa là domain PII không quan tâm file này
        if (reasons.Count == 0)
        {
            return null;
        }

        return new ClassificationResult
        {
            Domain = Domain,
            Level = level,
            Reasons = reasons
        };
    }
}
