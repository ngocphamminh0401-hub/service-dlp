// [Thay đổi] File mới (bước 5.5): module domain thứ hai, chứng minh thêm domain không cần sửa core
// [Thay đổi] Bổ sung bảng KnownSignals (viết lại từ tài liệu rule HR PDF, đặt trong rules/hr/) theo đúng mẫu
// FinanceModule.cs: mức đọc trực tiếp từ ScanMatch.Severity (đã áp level_gate nếu rule có khai báo), bảng này
// chỉ giữ LÝ DO hiển thị cho từng rule.
namespace DlpService.Classification.Modules;

/// <summary>Module domain nhân sự: xét mọi rule HR đã nạp (rules/hr/) cộng CCCD/Email làm tín hiệu chung.</summary>
public class HrModule : IClassifierModule
{
    /// <inheritdoc/>
    public string Domain => "HR";

    // Rule nào không có trong bảng thì HR domain coi như không liên quan, bỏ qua.
    private static readonly Dictionary<string, string> KnownSignals = new()
    {
        ["CCCD"] = "Có số CCCD (hồ sơ nhân sự)",
        ["Email"] = "Có địa chỉ email",
        ["HR-002 Biên bản xác minh kỷ luật liên quan sức khỏe cá nhân"] = "Biên bản xác minh kỷ luật liên quan sức khỏe cá nhân",
        ["HR-030 Văn bản nghiệp vụ nhân sự nội bộ thông thường"] = "Văn bản nghiệp vụ nhân sự nội bộ thông thường",
        ["HR-003 Chat nội bộ tiết lộ lý do sa thải kèm thuộc tính được bảo vệ"] = "Chat nội bộ tiết lộ lý do sa thải kèm thuộc tính được bảo vệ",
        ["HR-022 Thư mời nhận việc kèm đãi ngộ cá nhân"] = "Thư mời nhận việc kèm đãi ngộ cá nhân",
        ["HR-001 Hồ sơ xét kết nạp Đảng (lý lịch chính trị cá nhân/thân nhân)"] = "Hồ sơ xét kết nạp Đảng (lý lịch chính trị cá nhân/thân nhân)",
        ["HR-020 Hồ sơ/đơn khiếu nại quấy rối tình dục nơi làm việc"] = "Hồ sơ/đơn khiếu nại quấy rối tình dục nơi làm việc",
        ["HR-021 Biên bản điều tra tai nạn lao động kèm dữ liệu y tế nạn nhân"] = "Biên bản điều tra tai nạn lao động kèm dữ liệu y tế nạn nhân",
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

            // CCCD/Email (tín hiệu chung, không phải rule HR mới) giữ nguyên hành vi cũ: luôn Confidential.
            if (patternName is "CCCD" or "Email")
            {
                if (SensitivityLevel.Confidential > level)
                {
                    level = SensitivityLevel.Confidential;
                }
                continue;
            }

            // Mỗi chỗ khớp mang theo Severity riêng (có thể đã bị level_gate hạ xuống), lấy mức cao nhất
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

        // Không có lý do nào nghĩa là domain HR không quan tâm file này
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
