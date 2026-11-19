// [Thay đổi] File mới (Vòng 3, tối ưu A2): KeywordUniverse/Presence phải trả ĐÚNG như string.Contains(k, OrdinalIgnoreCase) —
// kể cả từ khoá có dấu tiếng Việt, từ khoá chồng nhau, khác hoa/thường, từ khoá rỗng, và từ khoá ngoài tập đã dựng.
using DlpService.Scanning;

namespace DlpService.Tests.Scanning;

public class KeywordPresenceIndexTests
{
    private static readonly string[] Keywords =
    [
        "cccd", "căn cước công dân", "số tài khoản", "stk", "tài khoản", "mật khẩu", "mat khau", "ví dụ", "vi du",
        "sao kê", "lương", "bảo hiểm xã hội", "bhxh", "xã hội", "Đồng Nai", "ĐIỆN THOẠI", "aa", "aaa", "ba", "bab"
    ];

    [Theory]
    [InlineData("Số CCCD của tôi là 012345678901")]
    [InlineData("CĂN CƯỚC CÔNG DÂN")]
    [InlineData("vì thế, ví dụ minh hoạ")]
    [InlineData("đồng nai, điện thoại")]
    [InlineData("aaaa")]                  // từ khoá chồng nhau: aa, aaa
    [InlineData("bab")]                   // ba + bab
    [InlineData("")]
    [InlineData("không có từ khoá nào ở đây")]
    public void Contains_GiongStringContains(string content)
    {
        var presence = new KeywordUniverse(Keywords).ForContent(content);
        foreach (var k in Keywords)
        {
            Assert.Equal(content.Contains(k, StringComparison.OrdinalIgnoreCase), presence.Contains(k));
        }
    }

    [Fact]
    public void Contains_NgauNhien_GiongStringContains()
    {
        var rnd = new Random(12345);
        string[] pieces = [.. Keywords, "x", " ", ".", "Ví", "dụ", "Á", "ả", "123", "\n"];
        var universe = new KeywordUniverse(Keywords);
        for (int n = 0; n < 500; n++)
        {
            var content = string.Concat(Enumerable.Range(0, rnd.Next(0, 40)).Select(_ =>
            {
                var piece = pieces[rnd.Next(pieces.Length)];
                return rnd.Next(3) == 0 ? piece.ToUpperInvariant() : piece;
            }));
            var presence = universe.ForContent(content);
            foreach (var k in Keywords)
            {
                Assert.True(content.Contains(k, StringComparison.OrdinalIgnoreCase) == presence.Contains(k),
                    $"Lệch ở từ khoá '{k}' với nội dung số {n}");
            }
        }
    }

    [Fact]
    public void TuKhoaRongVaNgoaiTap_XuLyNhuContains()
    {
        var presence = new KeywordUniverse(Keywords).ForContent("abc");
        Assert.True(presence.Contains(""));                         // "abc".Contains("") == true
        Assert.False(presence.ContainsAny([""], skipEmpty: true));  // bỏ từ khoá rỗng khi được yêu cầu
        Assert.True(presence.Contains("BC"));                       // ngoài tập → quay về Contains
        Assert.False(presence.Contains("zzz"));
    }

    [Fact]
    public void TapRong_KhongLoi()
    {
        var presence = new KeywordUniverse([]).ForContent("bất kỳ");
        Assert.False(presence.Contains("x"));
    }
}
