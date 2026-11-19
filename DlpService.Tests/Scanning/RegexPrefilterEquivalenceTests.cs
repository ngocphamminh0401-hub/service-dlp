// [Thay đổi] File mới (giai đoạn 2, tối ưu P1): bỏ qua pattern theo ký tự bắt buộc KHÔNG được làm đổi kết quả quét — so "bật" với "tắt" trên toàn bộ bộ rule thật.
// Test bổ sung chạy trên bộ file thật khi đặt DLP_SCAN_CORPUS=<thư mục chứa .docx/.pdf/.pptx/.txt/.csv> (tuỳ chọn DLP_SCAN_CORPUS_LIMIT=<số file>).
using DlpService.Reading;
using DlpService.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Tests.Scanning;

public class RegexPrefilterEquivalenceTests
{
    private static readonly Lazy<List<SensitivePattern>> Patterns = new(() =>
        new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules")));

    private static (RegexScanner On, RegexScanner Off) Scanners()
    {
        var on = new RegexScanner(Patterns.Value) { PrefilterEnabled = true };
        var off = new RegexScanner(Patterns.Value) { PrefilterEnabled = false };
        return (on, off);
    }

    public static IEnumerable<object[]> Contents()
    {
        yield return new object[] { "" };
        yield return new object[] { "Văn bản thường, không có ký hiệu đặc biệt nào để khớp" };
        yield return new object[] { "Liên hệ: nguyen.van.a@vnpt.vn hoặc tran@example.com để biết thêm. Số CCCD 012345678901" };
        yield return new object[] { "Email nội bộ user@sở.gov.vn và email thường a.b@c.org; điện thoại 0912345678" };
        yield return new object[] { "@ đứng một mình, a@, @b, và x@y" };
        yield return new object[] { "Toạ độ 10.123456, 106.654321 và ngày 12/03/2024; mã hồ sơ 123/ABC-2024" };
        yield return new object[] { "Số thẻ 4111 1111 1111 1111 hết hạn 12/28 CVV 123; STK 123456789012345" };
        yield return new object[] { "đến: hop.thu@benhvien.vn; đến: x@benhvienabc.com.vn" };
        yield return new object[] { string.Concat(Enumerable.Range(0, 400).Select(i => $"{i:D12}, ")) };
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public void BatTatPrefilter_CungKetQua(string content)
    {
        var (on, off) = Scanners();
        Assert.Equal(ScanSnapshot.Of(off.Scan(content)), ScanSnapshot.Of(on.Scan(content)));
    }

    // ---- P2: bỏ qua pattern context_required không validator khi file không có từ khoá ----

    private static SensitivePattern Ctx(string name, string regex, string[] keywords, Func<string, bool>? validator = null, bool excludeIfNoKw = false) => new()
    {
        Name = name,
        Regex = new System.Text.RegularExpressions.Regex(regex, System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(1)),
        Severity = "HIGH",
        ContextRequired = true,
        Keywords = [.. keywords],
        PrimaryKeywords = [.. keywords],
        ContextWindow = 40,
        Validator = validator,
        ExcludeIfNoKeywords = excludeIfNoKw,
        Confidence = 0.9,
    };

    [Theory]
    [InlineData("123456 và 654321")]                               // không có từ khoá nào
    [InlineData("alpha 123456")]
    [InlineData("beta 123456")]
    [InlineData("gamma 123456 654321 111111 222222 333333 444444")]
    [InlineData("delta 123456 và 999999")]
    [InlineData("chỉ có dãy số 12345678901234 trong file, không từ khoá")]
    [InlineData("")]
    public void P2_CacPatternTuyBien_BatTatCungKetQua(string content)
    {
        var patterns = new List<SensitivePattern>
        {
            Ctx("Dup", @"\b\d{6}\b", ["alpha"]),
            Ctx("Dup", @"\b\d{6}\b", ["beta"]),                  // cùng Name khác từ khoá: KHÔNG được bỏ qua
            Ctx("Solo", @"\b\d{6}\b", ["gamma"], excludeIfNoKw: true),
            Ctx("Val", @"\b\d{6}\b", ["delta"], validator: v => v != "999999"),   // có validator: giữ nguyên
            Ctx("EmptyKw", @"\b\d{6}\b", [""]),                  // từ khoá rỗng = luôn "có mặt"
        };
        var on = new RegexScanner(patterns, minConfidence: 0) { PrefilterEnabled = true };
        var off = new RegexScanner(patterns, minConfidence: 0) { PrefilterEnabled = false };
        Assert.Equal(ScanSnapshot.Of(off.Scan(content)), ScanSnapshot.Of(on.Scan(content)));
    }

    [Fact]
    public void P2_DieuKienDuDieuKien_DungNhuThietKe()
    {
        var patterns = new List<SensitivePattern>
        {
            Ctx("Solo", @"\b\d{6}\b", ["gamma"]),                // đủ điều kiện
            Ctx("Val", @"\b\d{6}\b", ["delta"], validator: _ => true),   // có validator → không
            Ctx("Dup", @"\b\d{6}\b", ["alpha"]),
            Ctx("Dup", @"\b\d{6}\b", ["beta"]),                  // tên trùng, từ khoá khác → cả hai không
            new SensitivePattern { Name = "NoCtx", Regex = new System.Text.RegularExpressions.Regex(@"\d+"), Severity = "LOW" }, // không context_required → không
        };
        Assert.Equal(1, new RegexScanner(patterns).ContextSkippablePatternCount);
    }

    [Fact]
    public void P2_BoRuleThat_CoNhieuPatternDuDieuKien()
    {
        // Chống hồi quy: bộ rule thật có 59 pattern context_required không validator; cho phép một số ít bị loại vì tên trùng từ khoá khác
        Assert.InRange(new RegexScanner(Patterns.Value).ContextSkippablePatternCount, 40, 59);
    }

    [Fact]
    public void CacPatternEmailThat_CoKyTuBatBuoc()
    {
        // Chống hồi quy bộ phân tích: các pattern email thật của bộ rule phải được nhận ra là cần '@'
        var emailPatterns = Patterns.Value.Where(p => p.Regex.ToString().TrimStart('\\', 'b').StartsWith("[A-Za-z0-9._%+", StringComparison.Ordinal)).ToList(); // dạng "<lớp ký tự>+@..."
        Assert.Equal(4, emailPatterns.Count); // email_001 ×3 + vnpt_email_001
        Assert.All(emailPatterns, p => Assert.Contains('@', RegexRequiredChars.Analyze(p.Regex.ToString(), p.Regex.Options)));
    }

    [Fact]
    public void BoFileThat_BatTatPrefilter_CungKetQua()
    {
        string? dir = Environment.GetEnvironmentVariable("DLP_SCAN_CORPUS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return; // chỉ chạy khi chỉ định bộ file thật
        }
        int limit = int.TryParse(Environment.GetEnvironmentVariable("DLP_SCAN_CORPUS_LIMIT"), out int l) && l > 0 ? l : int.MaxValue;
        var reader = new FileReaderService();
        var (on, off) = Scanners();
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Where(f => new[] { ".docx", ".pdf", ".pptx", ".txt", ".csv" }.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(f => f, StringComparer.Ordinal).Take(limit))
        {
            string? content = reader.TryReadFile(file).Content;
            if (content is null)
            {
                continue;
            }
            Assert.True(ScanSnapshot.Of(off.Scan(content)) == ScanSnapshot.Of(on.Scan(content)), $"Kết quả khác nhau ở {Path.GetRelativePath(dir, file)}");
            count++;
        }
        Assert.True(count > 0, "Không đọc được file nào trong DLP_SCAN_CORPUS");
    }
}
