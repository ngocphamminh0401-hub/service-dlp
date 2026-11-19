// [Thay đổi] Tối ưu P5 (giai đoạn 2): GỢI Ý chọn engine regex cho vài pattern đắt nhất. Đo 19/11/2026 (DlpRegexBench variants, 2.000 file D-raw, đã sửa thiên lệch thứ tự):
// engine NonBacktracking chậm hơn 1,47× nếu dùng cho mọi pattern, nhưng NHANH HƠN rõ ở các pattern có nhiều lựa chọn xen kẽ và lớp ký tự dài:
//   fraud_investigation_001#4: 58,6 -> 8,8 ms · vnpt_email_001#0: 42,3 -> 21,2 ms · health_001#8: 30,0 -> 20,8 ms (cùng kết quả khớp).
// Gợi ý nằm ở CODE (không sửa file rule JSON) để rules_ver không đổi — nếu đổi sẽ buộc quét lại toàn bộ DB khi nâng cấp. Mỗi gợi ý gắn mã băm chuỗi regex:
// sửa regex của rule thì gợi ý TỰ TẮT (và test RegexEngineHintsTests báo gợi ý cũ không còn áp dụng).
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DlpService.Scanning;

internal static class RegexEngineHints
{
    /// <summary>(RuleId, chỉ số pattern con) → mã băm (16 ký tự đầu của SHA-256, chữ thường) của chuỗi regex đã được đo.</summary>
    internal static readonly IReadOnlyDictionary<(string RuleId, int PatternIdx), string> NonBacktracking = new Dictionary<(string, int), string>
    {
        [("fraud_investigation_001", 4)] = "186ccff8cebdca46",
        [("vnpt_email_001", 0)] = "62f215c90f87c289",
        [("health_001", 8)] = "45318ba0eeee5f31",
    };

    /// <summary>Mã băm của chuỗi regex (16 ký tự đầu SHA-256 của UTF-8, chữ thường).</summary>
    internal static string Hash(string regexText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(regexText)))[..16].ToLowerInvariant();

    /// <summary>Tuỳ chọn engine cho 1 pattern: NonBacktracking nếu có gợi ý khớp mã băm, ngược lại Compiled như cũ.</summary>
    internal static RegexOptions OptionsFor(string? ruleId, int patternIdx, string regexText, bool enabled = true) =>
        enabled && ruleId != null && NonBacktracking.TryGetValue((ruleId, patternIdx), out string? hash) && Hash(regexText) == hash
            ? RegexOptions.NonBacktracking
            : RegexOptions.Compiled;
}
