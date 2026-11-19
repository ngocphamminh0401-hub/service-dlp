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
