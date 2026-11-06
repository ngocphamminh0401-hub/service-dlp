// [Thay đổi] File mới (bước 5.3): module domain đầu tiên, áp luật tài chính lên kết quả regex đã có sẵn
namespace DlpService.Classification.Modules;

// : IClassifierModule = cam kết tuân theo "hợp đồng" đã định nghĩa ở IClassifierModule.cs
// ClassificationEngine tự tìm thấy class này qua reflection, không cần đăng ký ở đâu khác
/// <summary>Module domain tài chính: xét mọi rule liên quan tài chính đã nạp, mức lấy từ Severity thật của từng chỗ khớp (đã qua level_gate).</summary>
public class FinanceModule : IClassifierModule
{
    // => là "expression-bodied member": cách viết gọn cho thuộc tính chỉ có get, tương đương { get { return "Finance"; } }
    /// <inheritdoc/>
    public string Domain => "Finance";

    // [Thay đổi] Bước 5.7.5: bảng này giờ chỉ giữ LÝ DO hiển thị cho từng rule, không giữ mức nữa —
    // mức đọc trực tiếp từ ScanMatch.Severity (do RegexScanner gán, đã áp level_gate nếu rule có khai báo).
    // Rule nào không có trong bảng thì Finance domain coi như không liên quan, bỏ qua.
    private static readonly Dictionary<string, string> KnownSignals = new()
    {
        ["CreditCard"] = "Có số thẻ tín dụng",
        ["CreditCardVisa"] = "Có số thẻ Visa",
        ["CreditCardMastercard"] = "Có số thẻ Mastercard",
        ["CreditCardAmex"] = "Có số thẻ American Express",
        ["CreditCardJCB"] = "Có số thẻ JCB",
        ["CreditCardDiscover"] = "Có số thẻ Discover",
        ["CreditCardNapas"] = "Có số thẻ Napas nội địa",
        ["CreditCardUnionPay"] = "Có số thẻ UnionPay",
        ["OtpLabeled"] = "Có mã OTP/PIN/mã xác thực giao dịch",
        ["OtpStandalone"] = "Có mã OTP",
        ["OtpValueAfterLabel"] = "Có giá trị OTP/PIN sau nhãn \"là:\"",
        ["Cvv"] = "Có mã CVV/CVC thẻ",
        ["CCCD"] = "Có số CCCD",
        ["BankAccountVN"] = "Có số tài khoản ngân hàng Việt Nam",
        ["Iban"] = "Có số tài khoản IBAN quốc tế",
        ["TaxCodePersonalBare"] = "Có mã số thuế cá nhân",
        ["TaxCodePersonalLabeled"] = "Có mã số thuế cá nhân",
        ["TaxCodeBusiness"] = "Có mã số thuế doanh nghiệp (công khai)",
        ["SwiftBicVN"] = "Có mã SWIFT/BIC ngân hàng",
        ["FinComplianceReport"] = "Báo cáo tuân thủ/giám sát nội bộ",
        ["FinRiskPolicyDiscussion"] = "Trao đổi xây dựng chính sách quản lý rủi ro",
        ["FinTrainingGuide"] = "Tài liệu đào tạo/hướng dẫn quy trình nội bộ",
        ["FinLoanApprovalProcess"] = "Quy trình thẩm định/phê duyệt hồ sơ vay nội bộ",
        ["FinProcessChangeNotice"] = "Thông báo thay đổi quy trình nghiệp vụ nội bộ",
        ["FIN-013 Sao kê tài khoản thanh toán cá nhân"] = "Sao kê tài khoản thanh toán cá nhân",
        ["FIN-006 Thông tin lương / thu nhập / thù lao"] = "Có thông tin lương/thu nhập cá nhân",
        ["FIN-020 Trao đổi nội bộ số liệu kinh doanh chi nhánh chưa kiểm toán"] = "Số liệu kinh doanh chi nhánh nội bộ chưa kiểm toán",
        ["FIN-010 Header bảng Số thẻ/CVV/OTP"] = "Có bảng dữ liệu với cột Số thẻ/CVV/OTP",
        ["FIN-012 Sao kê thẻ tín dụng cá nhân"] = "Sao kê thẻ tín dụng cá nhân",
        ["FIN-015 Chat nội bộ tiết lộ dữ liệu tài khoản khách hàng"] = "Chat nội bộ tiết lộ dữ liệu tài khoản khách hàng",
        ["FIN-011 Danh sách khách hàng VIP/ưu tiên"] = "Danh sách khách hàng VIP/ưu tiên",
        // [Thay đổi] Đăng ký rule mới fin-transfer-confirmation.json (FIN-014, viết lại từ tài liệu rule financial PDF)
        ["FIN-014 Email xác nhận giao dịch chuyển khoản"] = "Email xác nhận giao dịch chuyển khoản",
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
            // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: dùng hàm dùng chung
            // (SensitivityLevelExtensions.ParseSensitivityLevel) thay vì bản ParseSeverity riêng của module này —
            // CompoundRuleModule mới cũng cần đúng logic parse này, gộp lại để tránh 5 bản trùng lặp.
            foreach (var match in matchesForThisRule)
            {
                var matchLevel = match.Severity.ParseSensitivityLevel();
                if (matchLevel > level)
                {
                    level = matchLevel;
                }
            }
        }

        // Không có lý do nào nghĩa là domain Finance không quan tâm file này
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
