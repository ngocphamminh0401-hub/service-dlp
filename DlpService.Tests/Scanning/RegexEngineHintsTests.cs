// [Thay đổi] File mới (giai đoạn 2, tối ưu P5): gợi ý engine NonBacktracking cho vài pattern đắt nhất phải (1) còn áp dụng đúng pattern đã đo (mã băm khớp),
// (2) cho KẾT QUẢ KHỚP GIỐNG HỆT engine backtracking (fuzz + bộ file thật khi đặt DLP_SCAN_CORPUS).
using System.Text;
using System.Text.RegularExpressions;
using DlpService.Reading;
using DlpService.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Tests.Scanning;

public class RegexEngineHintsTests
{
    private static string RulesDir => Path.Combine(AppContext.BaseDirectory, "rules");

    [Fact]
    public void MoiGoiY_ApDungDungMotPattern_VaTatDuocBangCo()
    {
        var withHints = new RuleLoader(NullLogger.Instance).LoadFromFolder(RulesDir);
        var withoutHints = new RuleLoader(NullLogger.Instance, useEngineHints: false).LoadFromFolder(RulesDir);

        var applied = withHints.Where(p => (p.Regex.Options & RegexOptions.NonBacktracking) != 0).Select(p => (p.RuleId, p.PatternIdx)).ToHashSet();
        // Mỗi gợi ý phải áp dụng cho đúng 1 pattern; nếu rule bị sửa regex (mã băm lệch) gợi ý tự tắt và test này báo để cập nhật bảng
        Assert.Equal(RegexEngineHints.NonBacktracking.Keys.Select(k => ((string?)k.RuleId, k.PatternIdx)).ToHashSet(), applied);
        Assert.DoesNotContain(withoutHints, p => (p.Regex.Options & RegexOptions.NonBacktracking) != 0);
        Assert.Equal(withHints.Count, withoutHints.Count);
    }

    [Theory]
    [InlineData("fraud_investigation_001", 4)]
    [InlineData("vnpt_email_001", 0)]
    [InlineData("health_001", 8)]
    public void NonBacktracking_CungKetQuaVoiBacktracking_Fuzz(string ruleId, int idx)
    {
        var patterns = new RuleLoader(NullLogger.Instance, useEngineHints: false).LoadFromFolder(RulesDir);
        var p = patterns.Single(x => x.RuleId == ruleId && x.PatternIdx == idx);
        var backtracking = new Regex(p.Regex.ToString(), RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        var nonBacktracking = new Regex(p.Regex.ToString(), RegexOptions.NonBacktracking, TimeSpan.FromSeconds(5));

        // Từ vựng: mọi "từ" (chuỗi chữ ≥ 2 ký tự) trong chính regex + dấu phân cách + nhiễu → tạo nhiều ca gần-khớp
        var words = Regex.Matches(p.Regex.ToString(), @"\p{L}{2,}").Select(m => m.Value).Distinct().ToList();
        string[] glue = [" ", "  ", "\t", "\n", ".", ",", "-", "@", "_", "x", "A1", "123", "vnpt.vn", ".gov.vn", "é", "Đ",
                         "user@", "a.b_c@", "nguyen-van@", "vnpt-i.vn", "vinaphone.vn", "vnpt.com.vn", "vnptit.vn", "a@vnpt.vn", "x.y@vnpt-net.vn"];
        var rnd = new Random(20261119 + idx);
        int checkedWithMatch = 0;
        for (int n = 0; n < 20000; n++)
        {
            var sb = new StringBuilder();
            int tokens = rnd.Next(1, 24);
            for (int t = 0; t < tokens; t++)
            {
                string tok = rnd.Next(3) == 0 ? glue[rnd.Next(glue.Length)] : words[rnd.Next(words.Count)];
                sb.Append(rnd.Next(4) == 0 ? tok.ToUpperInvariant() : tok);
                if (rnd.Next(3) == 0) sb.Append(' ');
            }
            string s = sb.ToString();
            var a = backtracking.Matches(s).Select(m => (m.Index, m.Length)).ToList();
            var b = nonBacktracking.Matches(s).Select(m => (m.Index, m.Length)).ToList();
            Assert.True(a.SequenceEqual(b), $"{ruleId}#{idx} khác nhau trên chuỗi số {n}: backtracking [{string.Join(",", a)}] vs NonBacktracking [{string.Join(",", b)}]");
            if (a.Count > 0) checkedWithMatch++;
        }
        Assert.True(checkedWithMatch > 200, $"Fuzz chỉ sinh {checkedWithMatch} chuỗi có khớp — từ vựng chưa đủ để kiểm");
    }

    [Fact]
    public void BoFileThat_CoGoiY_CungKetQuaKhongGoiY()
    {
        string? dir = Environment.GetEnvironmentVariable("DLP_SCAN_CORPUS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }
        int limit = int.TryParse(Environment.GetEnvironmentVariable("DLP_SCAN_CORPUS_LIMIT"), out int l) && l > 0 ? l : int.MaxValue;
        var on = new RegexScanner(new RuleLoader(NullLogger.Instance).LoadFromFolder(RulesDir));
        var off = new RegexScanner(new RuleLoader(NullLogger.Instance, useEngineHints: false).LoadFromFolder(RulesDir));
        var reader = new FileReaderService();
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(f => new[] { ".docx", ".pdf", ".pptx", ".txt", ".csv" }.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(f => f, StringComparer.Ordinal).Take(limit))
        {
            string? content = reader.TryReadFile(file).Content;
            if (content is null) continue;
            Assert.True(ScanSnapshot.Of(off.Scan(content)) == ScanSnapshot.Of(on.Scan(content)), $"Kết quả khác nhau ở {Path.GetRelativePath(dir, file)}");
            count++;
        }
        Assert.True(count > 0, "Không đọc được file nào trong DLP_SCAN_CORPUS");
    }
}
