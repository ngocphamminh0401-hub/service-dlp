// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): TEST HỢP ĐỒNG cho IDataFolderGuard. Điều mọi adapter phải đúng: sau Restrict, chính
// tài khoản chạy service vẫn tạo/đọc/ghi được file trong thư mục (nếu không DB sẽ không mở được). Việc "người khác bị chặn" không kiểm
// được trong test đơn tài khoản — phần đó kiểm bằng tay khi cài service thật (docs: kiểm tra ACL).
using DlpService.Platform;

namespace DlpService.Tests.Platform;

public abstract class DataFolderGuardContractTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-guard-" + Guid.NewGuid().ToString("N"));

    protected DataFolderGuardContractTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Trả adapter cần kiểm tra, hoặc null nếu hệ điều hành hiện tại không áp dụng (test sẽ bỏ qua).</summary>
    protected abstract IDataFolderGuard? Create();

    [Fact]
    public void Sau_khi_khoa_van_tao_va_doc_duoc_file_trong_thu_muc()
    {
        var guard = Create();
        if (guard == null) return;

        guard.Restrict(_folder);

        string file = Path.Combine(_folder, "dlp.db");
        File.WriteAllText(file, "du lieu");
        Assert.Equal("du lieu", File.ReadAllText(file));
    }

    [Fact]
    public void Khoa_hai_lan_khong_loi()
    {
        var guard = Create();
        if (guard == null) return;

        guard.Restrict(_folder);
        var ex = Record.Exception(() => guard.Restrict(_folder));

        Assert.Null(ex);
    }

    [Fact]
    public void Co_mo_ta_de_ghi_log()
    {
        var guard = Create();
        if (guard == null) return;

        Assert.False(string.IsNullOrWhiteSpace(guard.Description));
    }
}

public class NoOpDataFolderGuardTests : DataFolderGuardContractTests
{
    protected override IDataFolderGuard? Create() => new NoOpDataFolderGuard();
}

public class WindowsDataFolderGuardTests : DataFolderGuardContractTests
{
    protected override IDataFolderGuard? Create() =>
        OperatingSystem.IsWindows() ? new WindowsDataFolderGuard() : null;
}
