// [Thay đổi] File mới (kế hoạch tách lõi, bước 3): khoá hành vi che giá trị nhạy cảm khi ghi log (CLAUDE.md mục 7.5).
using DlpService.Pipeline;

namespace DlpService.Tests.Pipeline;

public class LogMaskingTests
{
    [Fact]
    public void Gia_tri_dai_giu_3_ky_tu_dau_va_cuoi()
    {
        Assert.Equal("012******901", LogMasking.Mask("012345678901"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("123456")] // đúng 6 ký tự vẫn che hết (ngưỡng là <= 6)
    public void Gia_tri_ngan_che_het(string value)
    {
        Assert.Equal("***", LogMasking.Mask(value));
    }

    [Fact]
    public void Ket_qua_khong_chua_phan_giua_cua_gia_tri_goc()
    {
        string masked = LogMasking.Mask("4111111111111111");

        Assert.DoesNotContain("1111111111", masked);
        Assert.Equal(16, masked.Length);
    }
}
