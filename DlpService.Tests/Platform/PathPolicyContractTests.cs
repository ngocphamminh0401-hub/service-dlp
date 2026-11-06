// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): TEST HỢP ĐỒNG cho IPathPolicy. Lớp abstract chứa các điều mọi adapter phải đúng;
// mỗi hệ điều hành thêm 1 lớp con trả adapter của mình (Linux sau này: CaseSensitivePathPolicyTests) là có ngay cả bộ kiểm tra.
using DlpService.Platform;

namespace DlpService.Tests.Platform;

public abstract class PathPolicyContractTests
{
    protected abstract IPathPolicy Create();

    [Fact]
    public void File_thuong_khong_phai_file_tam()
    {
        Assert.False(Create().IsTemporaryFile(Path.Combine("a", "baocao.docx")));
    }

    [Fact]
    public void Chi_xet_ten_file_khong_xet_thu_muc()
    {
        // Thư mục cha có "~$" nhưng file thì bình thường → không phải file tạm
        Assert.False(Create().IsTemporaryFile(Path.Combine("a", "~$thumuc", "baocao.docx")));
    }

    [Fact]
    public void Khong_can_file_ton_tai_tren_dia()
    {
        // Hợp đồng: chỉ xét tên, không đụng đĩa → đường dẫn không tồn tại không ném lỗi
        var ex = Record.Exception(() => Create().IsTemporaryFile(Path.Combine("khong", "ton", "tai.docx")));
        Assert.Null(ex);
    }
}

/// <summary>Adapter Windows/mặc định: không phân biệt hoa/thường, bỏ file khoá "~$" của Office.</summary>
public class CaseInsensitivePathPolicyTests : PathPolicyContractTests
{
    protected override IPathPolicy Create() => new CaseInsensitivePathPolicy();

    [Fact]
    public void So_sanh_khong_phan_biet_hoa_thuong()
    {
        Assert.Equal(StringComparison.OrdinalIgnoreCase, Create().Comparison);
        Assert.StartsWith(@"c:\data\tmp\", @"C:\Data\Tmp\a.docx", Create().Comparison);
    }

    [Fact]
    public void File_khoa_tam_cua_Office_bi_bo_qua()
    {
        Assert.True(Create().IsTemporaryFile(Path.Combine("a", "~$baocao.docx")));
    }
}

/// <summary>[Thay đổi] GĐ2 bước 2.2a: adapter Linux — phân biệt hoa/thường, bỏ file tạm của các ứng dụng Linux.</summary>
public class LinuxPathPolicyTests : PathPolicyContractTests
{
    protected override IPathPolicy Create() => new DlpService.Platform.Linux.LinuxPathPolicy();

    [Fact]
    public void So_sanh_phan_biet_hoa_thuong()
    {
        Assert.Equal(StringComparison.Ordinal, Create().Comparison);
        // Linux: /home/a/Docs và /home/a/docs là HAI thư mục khác nhau
        Assert.False("/home/a/Docs/x.docx".StartsWith("/home/a/docs/", Create().Comparison));
    }

    [Theory]
    [InlineData("~$baocao.docx")]          // Office (file đến từ ổ chia sẻ Windows)
    [InlineData(".~lock.baocao.docx#")]    // LibreOffice
    [InlineData(".goutputstream-AB12CD")]  // GNOME
    [InlineData("baocao.txt~")]            // bản sao lưu vim/emacs/gedit
    [InlineData(".baocao.txt.swp")]        // vim
    [InlineData("baocao.txt.kate-swp")]    // Kate
    [InlineData("hopdong.pdf.part")]       // Firefox tải dở
    [InlineData("Unconfirmed 123.crdownload")] // Chrome tải dở
    [InlineData("lu1234abcd.tmp")]         // LibreOffice khi lưu
    [InlineData("HOPDONG.PDF.PART")]       // hậu tố do ứng dụng đặt → không phân biệt hoa/thường
    public void File_tam_cua_ung_dung_Linux_bi_bo_qua(string name)
    {
        Assert.True(Create().IsTemporaryFile(Path.Combine("/home/a", name)));
    }

    [Theory]
    [InlineData("baocao.docx")]
    [InlineData(".bashrc")]            // file ẩn bình thường không phải file tạm (việc bỏ thư mục ẩn thuộc GĐ3)
    [InlineData("so~tay.txt")]         // có "~" ở giữa tên
    [InlineData("partner-list.xlsx")]  // có chữ "part" nhưng không phải đuôi ".part"
    public void File_binh_thuong_khong_bi_bo_qua(string name)
    {
        Assert.False(Create().IsTemporaryFile(Path.Combine("/home/a", name)));
    }
}
