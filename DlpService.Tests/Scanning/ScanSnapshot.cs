// [Thay đổi] File mới (giai đoạn 2, tối ưu P1/P2): chụp TOÀN BỘ kết quả 1 lần quét (từng chỗ khớp + mọi trường telemetry + timeout/budget) thành chuỗi,
// để test so "bật prefilter" với "tắt prefilter" phải GIỐNG HỆT. Không chứa giá trị nhạy cảm ngoài nội dung test tự dựng / file bạn chỉ định.
using System.Reflection;
using System.Text;
using DlpService.Scanning;

namespace DlpService.Tests.Scanning;

internal static class ScanSnapshot
{
    public static string Of(ScanResult r)
    {
        var sb = new StringBuilder();
        foreach (var m in r.Matches)
        {
            sb.Append(m.PatternName).Append('|').Append(m.Index).Append('|').Append(m.Value).Append('|').Append(m.Severity).Append('|').Append(m.Validated)
              .Append('|').Append(m.Category).Append('|').Append(m.RuleId).Append('|').Append(string.Join(",", m.Tags ?? []))
              .Append('|').Append(m.Confidence?.ToString("R")).Append('|').Append(m.Score.ToString("R")).Append('\n');
        }
        sb.Append("timedOut=").Append(string.Join(",", r.TimedOutPatterns)).Append(" budget=").Append(r.BudgetExceeded).Append('\n');
        foreach (var prop in typeof(ScanTelemetry).GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            object? v = prop.GetValue(r.Telemetry);
            string text = v is System.Collections.IEnumerable list and not string
                ? string.Join(",", list.Cast<object?>().Select(x => x?.ToString()))
                : v is double d ? d.ToString("R") : v?.ToString() ?? "null";
            sb.Append(prop.Name).Append('=').Append(text).Append('\n');
        }
        return sb.ToString();
    }
}
