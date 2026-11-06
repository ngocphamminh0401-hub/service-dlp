// [Thay đổi] File mới (kế hoạch tách lõi, bước 3): khoá hành vi che giá trị nhạy cảm khi ghi log (CLAUDE.md mục 7.5).
// [Thay đổi] GĐ2 (R7): cập nhật theo cách che mới — chỉ giữ 2 ký tự cuối, chuỗi <= 8 ký tự che hết.
using DlpService.Pipeline;

namespace DlpService.Tests.Pipeline;

public class LogMaskingTests
{
    [Fact]
    public void Gia_tri_dai_chi_giu_2_ky_tu_cuoi()
    {
        Assert.Equal("**********01", LogMasking.Mask("012345678901"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("12345678")] // đúng 8 ký tự vẫn che hết (VD mã OTP, số CMND cũ 9 số thì mới lộ 2 số cuối)
    public void Gia_tri_ngan_che_het(string value)
    {
        Assert.Equal("***", LogMasking.Mask(value));
    }

    [Fact]
    public void Ket_qua_khong_chua_dau_va_giua_cua_gia_tri_goc()
    {
        string masked = LogMasking.Mask("4111111111111111");

        Assert.DoesNotContain("411", masked);
        Assert.DoesNotContain("1111", masked);
        Assert.Equal(16, masked.Length);
    }

    [Fact]
    public void CCCD_khong_lo_qua_2_so()
    {
        // R7: cách cũ lộ "001" (mã tỉnh) + "345" → ghép với ngày sinh trong log là thu hẹp được rất nhiều
        string masked = LogMasking.Mask("001099012345");

        Assert.Equal(2, masked.Count(char.IsDigit));
    }
}
