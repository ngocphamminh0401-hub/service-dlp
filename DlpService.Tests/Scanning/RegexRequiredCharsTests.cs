// [Thay đổi] File mới (giai đoạn 2, tối ưu P1): bộ phân tích ký tự bắt buộc của regex phải BẢO THỦ — chỉ trả ký tự khi chắc chắn bắt buộc.
using System.Text.RegularExpressions;
using DlpService.Scanning;

namespace DlpService.Tests.Scanning;

public class RegexRequiredCharsTests
{
    [Theory]
    // Bắt buộc: ký tự thường ở cấp trên cùng
    [InlineData(@"[A-Za-z0-9._%+\-]+@(?:vnpt\.vn|vnpt-i\.vn)", "@")]
    [InlineData(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,7}", "@.")]    // '\.' (escape) cũng là ký tự bắt buộc
    [InlineData(@"\b[-+]?\d{1,2}\.\d{4,8}\s*,\s*[-+]?\d{1,3}\.\d{4,8}\b", ".,")] // ',' trong {1,2} KHÔNG được tính; '.' và ',' ngoài lượng từ thì có
    [InlineData(@"(?i)đ[ếe]n\s*[:=]\s*[a-z0-9._-]+@[a-z0-9.-]*benhvien", "@")]   // cờ (?i) đầu chuỗi; ':' nằm trong lớp ký tự nên không tính
    [InlineData(@"\b\d{1,4}\s*/\s*[A-ZĐ]{2,8}[\s\-/]?\d{4}\b", "/")]
    [InlineData(@"@+x", "@")]                                                    // '+' = ít nhất 1 lần
    [InlineData(@"a@{2,}b", "@")]                                                // {2,} = ít nhất 2 lần
    // KHÔNG bắt buộc hoặc không chắc
    [InlineData(@"@?abc", "")]                                                   // '?' = 0 lần
    [InlineData(@"@*abc", "")]
    [InlineData(@"@{0,2}abc", "")]
    [InlineData(@"@{,2}abc", "")]                                                // {,n}: không chắc → bỏ
    [InlineData(@"abc@|def", "")]                                                // phép chọn ở cấp trên cùng
    [InlineData(@"(?:foo@bar|baz)", "")]                                         // trong nhóm: không ghi nhận
    [InlineData(@"(foo@)?bar", "")]
    [InlineData(@"[@]abc", "")]                                                  // trong lớp ký tự: không ghi nhận
    [InlineData(@"(?<!@)abc", "")]                                               // lookbehind: không ghi nhận
    [InlineData(@"\d{4,8}", "")]                                                 // ',' trong lượng từ
    [InlineData(@"\p{L}+@", "@")]                                                // \p{L} đọc đúng, '@' sau nó
    [InlineData(@"(?x) a @ b", "")]                                              // cờ x: không phân tích
    [InlineData(@"[unclosed@", "")]                                              // lớp ký tự không đóng
    [InlineData(@"abc\", "")]                                                    // escape cụt
    [InlineData(@"abc)", "")]                                                    // ngoặc thừa
    [InlineData(@"(abc", "")]                                                    // nhóm không đóng
    public void Analyze_BaoThu(string pattern, string expectedChars)
    {
        char[] actual = RegexRequiredChars.Analyze(pattern, RegexOptions.None);
        Assert.Equal(expectedChars.OrderBy(c => c), actual.OrderBy(c => c));
    }

    [Fact]
    public void Analyze_ChuVaChuSo_KhongBaoGio_DuocGhiNhan()
    {
        // Chữ phụ thuộc IgnoreCase, chữ số dễ lẫn với backreference → chỉ ký tự ký hiệu ASCII
        Assert.Empty(RegexRequiredChars.Analyze("abc123VNM", RegexOptions.None));
    }

    [Fact]
    public void Analyze_TuyChonNguyHiem_Bo()
    {
        Assert.Empty(RegexRequiredChars.Analyze("a@b", RegexOptions.IgnorePatternWhitespace));
        Assert.Empty(RegexRequiredChars.Analyze("a@b", RegexOptions.RightToLeft));
    }

    [Fact]
    public void ChuoiBatBuoc_ThatSu_KhongSinhKhopKhiThieuKyTu()
    {
        // Kiểm chéo: với mọi ca có ký tự bắt buộc, regex KHÔNG khớp chuỗi thiếu ký tự đó
        string[] patterns =
        [
            @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,7}",
            @"\b[-+]?\d{1,2}\.\d{4,8}\s*,\s*[-+]?\d{1,3}\.\d{4,8}\b",
            @"\b\d{1,4}\s*/\s*[A-ZĐ]{2,8}[\s\-/]?\d{4}\b",
        ];
        string[] probes = ["abc 123 xyz", "user.name example.com", "12 34 5678", "10.1234 20.5678", "abc-ABC 2024"];
        foreach (var p in patterns)
        {
            var req = RegexRequiredChars.Analyze(p, RegexOptions.None);
            Assert.NotEmpty(req);
            var rx = new Regex(p);
            foreach (var probe in probes.Where(s => req.Any(c => !s.Contains(c))))
            {
                Assert.False(rx.IsMatch(probe), $"'{p}' khớp '{probe}' dù thiếu ký tự bắt buộc");
            }
        }
    }
}
