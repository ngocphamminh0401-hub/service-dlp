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
