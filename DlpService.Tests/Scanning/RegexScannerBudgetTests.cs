// [Thay đổi] File mới (M4 — ngân sách thời gian quét cả 1 file): timeout 1 s của .NET chỉ áp cho TỪNG lần tìm 1 chỗ khớp, nên 1 rule có nhiều chỗ
// khớp chậm (mỗi chỗ chưa tới timeout) trước đây chạy không có trần. "Chậm mỗi chỗ khớp" được tạo ỔN ĐỊNH bằng validator ngủ 20 ms (validator
// chạy cho từng chỗ khớp, đúng điểm ngân sách được kiểm) — không phụ thuộc engine regex có tối ưu backtracking hay không.
using System.Diagnostics;
using System.Text.RegularExpressions;
using DlpService.Reading;
using DlpService.Scanning;

namespace DlpService.Tests.Scanning;

public class RegexScannerBudgetTests
{
    private static SensitivePattern SlowPerMatch(int sleepMs) => new()
    {
        Name = "Slow",
        RuleId = "slow_001",
        Regex = new Regex(@"\bX\b", RegexOptions.None, TimeSpan.FromSeconds(10)), // timeout lớn: KHÔNG bao giờ ném
        Severity = "RESTRICTED",
        Confidence = 0.95,
        Validator = _ => { Thread.Sleep(sleepMs); return true; }
    };

    private static string ManyX(int count) => string.Join("\n", Enumerable.Repeat("ma X", count));

    [Fact]
    public void Nhieu_cho_khop_cham_bi_chan_boi_ngan_sach_ca_file()
    {
        // 100 chỗ khớp × 20 ms = ~2 s nếu không có ngân sách; không chỗ nào chạm timeout 10 s → trước M4 không có gì dừng được
        var scanner = new RegexScanner([SlowPerMatch(20)], maxScanTime: TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        var result = scanner.Scan(ManyX(100));
        sw.Stop();

        Assert.True(result.BudgetExceeded);
        Assert.Empty(result.TimedOutPatterns);
        Assert.False(result.IsComplete);
        Assert.Equal(IncompleteReasons.ScanBudget, result.IncompleteReason);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"mất {sw.ElapsedMilliseconds} ms dù ngân sách 200 ms");
        Assert.InRange(result.Matches.Count, 1, 99); // giữ các chỗ khớp tìm được trước khi dừng
    }

    [Fact]
    public void Het_ngan_sach_thi_khong_chay_cac_rule_con_lai()
    {
        var later = new SensitivePattern
        {
            Name = "Later", RuleId = "later_001", Regex = new Regex(@"\bma\b"), Severity = "INTERNAL", Confidence = 0.95
        };
        var scanner = new RegexScanner([SlowPerMatch(20), later], maxScanTime: TimeSpan.FromMilliseconds(100));

        var result = scanner.Scan(ManyX(50));

        Assert.True(result.BudgetExceeded);
        Assert.DoesNotContain(result.Matches, m => m.PatternName == "Later");
    }

    [Fact]
    public void Regex_backtracking_qua_thoi_gian_bao_regex_timeout()
    {
        var redos = new SensitivePattern
        {
            Name = "Redos", RuleId = "redos_001",
            Regex = new Regex(@"^(a+)+$", RegexOptions.Multiline, TimeSpan.FromMilliseconds(50)),
            Severity = "RESTRICTED", Confidence = 0.95
        };
        var scanner = new RegexScanner([redos]);

        var result = scanner.Scan(new string('a', 40) + "!");

        Assert.Contains("Redos", result.TimedOutPatterns);
        Assert.False(result.BudgetExceeded);
        Assert.Equal(IncompleteReasons.RegexTimeout, result.IncompleteReason);
    }

    [Fact]
    public void Noi_dung_binh_thuong_ket_qua_giong_het_khi_khong_dat_ngan_sach()
    {
        var cccd = new SensitivePattern
        {
            Name = "CCCD", RuleId = "cccd_001", Regex = new Regex(@"\b\d{12}\b"), Severity = "RESTRICTED", Confidence = 0.95
        };
        string content = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"so {100000000000 + i}"));

        var withDefault = new RegexScanner([cccd]).Scan(content);
        var withHuge = new RegexScanner([cccd], maxScanTime: TimeSpan.FromHours(1)).Scan(content);

        Assert.True(withDefault.IsComplete);
        Assert.Null(withDefault.IncompleteReason);
        Assert.Equal(withHuge.Matches.Select(m => (m.Index, m.Value)), withDefault.Matches.Select(m => (m.Index, m.Value)));
    }

    [Fact]
    public void Ngan_sach_sai_thi_dung_mac_dinh()
    {
        Assert.Equal(RegexScanner.DefaultMaxScanTime, new RegexScanner([], maxScanTime: TimeSpan.Zero).MaxScanTime);
        Assert.Equal(RegexScanner.DefaultMaxScanTime, new RegexScanner([], maxScanTime: TimeSpan.FromSeconds(-1)).MaxScanTime);
        Assert.Equal(TimeSpan.FromSeconds(10), RegexScanner.DefaultMaxScanTime);
    }
}
