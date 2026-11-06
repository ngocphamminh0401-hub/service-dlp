// [Thay đổi] File mới: module domain "org" (viết lại từ tài liệu rule financial/org PDF, đặt trong rules/org/)
// theo đúng mẫu FinanceModule.cs — mức đọc trực tiếp từ ScanMatch.Severity (đã áp level_gate nếu rule có khai báo).
namespace DlpService.Classification.Modules;

/// <summary>Module domain tổ chức/nội bộ (org): xét mọi rule org đã nạp (rules/org/).</summary>
public class OrgModule : IClassifierModule
{
    /// <inheritdoc/>
    public string Domain => "Org";

    // Rule nào không có trong bảng thì Org domain coi như không liên quan, bỏ qua.
    private static readonly Dictionary<string, string> KnownSignals = new()
    {
        ["ORG-009 Từ vựng nghiệp vụ nội bộ đặc thù ngân hàng/bảo hiểm"] = "Từ vựng nghiệp vụ nội bộ đặc thù ngân hàng/bảo hiểm",
        ["ORG-007 Văn bản nghiệp vụ nội bộ thông thường"] = "Văn bản nghiệp vụ nội bộ thông thường",
        ["ORG-001b Tài liệu BÍ MẬT / CONFIDENTIAL"] = "Tài liệu mang nhãn BÍ MẬT / CONFIDENTIAL",
        ["ORG-001 Tài liệu TỐI MẬT / MẬT / RESTRICTED"] = "Tài liệu mang nhãn TỐI MẬT / MẬT / RESTRICTED",
        ["ORG-003 Mã số hợp đồng / PO"] = "Có mã số hợp đồng / PO",
        ["ORG-005 Thông tin kết nối DB / credentials"] = "Có thông tin xác thực (password/API key/token)",
        ["ORG-006 Điều tra gian lận / AML / hình sự"] = "Liên quan điều tra gian lận/AML/hình sự",
        ["ORG-010 Giao dịch đáng ngờ/bất thường / rửa tiền (tên chính sách)"] = "Nhắc đến giao dịch đáng ngờ/bất thường hoặc rửa tiền",
        ["ORG-011 Văn bản nghiệp vụ nội bộ bệnh viện/y tế thông thường"] = "Văn bản nghiệp vụ nội bộ bệnh viện/y tế",
        ["ORG-008 Ngữ cảnh hồ sơ bồi thường/claim bảo hiểm"] = "Ngữ cảnh hồ sơ bồi thường/claim bảo hiểm",
        ["Mã nội bộ và nhãn phân loại VNPT"] = "Có mã nội bộ VNPT",
        ["ORG-004 Địa chỉ IP nội bộ (Private)"] = "Có địa chỉ IP nội bộ (RFC 1918)",
        ["Email domain nội bộ VNPT (tín hiệu tổ chức)"] = "Có email domain nội bộ VNPT",
        ["Watermark nội bộ / nhãn lưu hành hạn chế"] = "Có watermark/nhãn lưu hành nội bộ",
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

        // Không có lý do nào nghĩa là domain Org không quan tâm file này
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
