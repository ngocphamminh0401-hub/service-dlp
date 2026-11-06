// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): TEST HỢP ĐỒNG cho IDataFolderGuard. Điều mọi adapter phải đúng: sau Restrict, chính
// tài khoản chạy service vẫn tạo/đọc/ghi được file trong thư mục (nếu không DB sẽ không mở được). Việc "người khác bị chặn" không kiểm
// được trong test đơn tài khoản — phần đó kiểm bằng tay khi cài service thật (docs: kiểm tra ACL).
using DlpService.Platform;
using DlpService.Platform.Windows;

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

/// <summary>[Thay đổi] GĐ2 bước 2.2b: adapter Linux (chmod). Trên Windows trả null → các test bỏ qua.</summary>
public class LinuxDataFolderGuardTests : DataFolderGuardContractTests
{
    protected override IDataFolderGuard? Create() =>
        OperatingSystem.IsWindows() ? null : new DlpService.Platform.Linux.LinuxDataFolderGuard();

    [Fact]
    public void Thu_muc_0700_noi_dung_co_san_0600_va_thu_muc_con_0700()
    {
        if (OperatingSystem.IsWindows()) return;

        string folder = Path.Combine(Path.GetTempPath(), "dlp-guard-mode-" + Guid.NewGuid().ToString("N"));
        string sub = Path.Combine(folder, "con");
        Directory.CreateDirectory(sub);
        string file = Path.Combine(sub, "dlp.db");
        File.WriteAllText(file, "x");
        // Mô phỏng DB tạo từ lần chạy trước khi chưa khoá: ai cũng đọc được
        File.SetUnixFileMode(folder, (UnixFileMode)0b111_101_101); // 0755
        File.SetUnixFileMode(file, (UnixFileMode)0b110_100_100);   // 0644
        try
        {
            new DlpService.Platform.Linux.LinuxDataFolderGuard().Restrict(folder);

            const UnixFileMode dir700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(dir700, File.GetUnixFileMode(folder));
            Assert.Equal(dir700, File.GetUnixFileMode(sub));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
