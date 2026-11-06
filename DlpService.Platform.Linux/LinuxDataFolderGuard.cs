// [Thay đổi] File mới (GĐ2 bước 2.2b): khoá thư mục dữ liệu trên Linux bằng quyền Unix — tương đương WindowsDataFolderGuard (ACL).
// Thư mục → 0700 (rwx------), thư mục con → 0700, file có sẵn → 0600 (rw-------): chỉ CHỦ SỞ HỮU (tài khoản chạy service, sau này là user `dlp`)
// vào được; root vẫn vào được như mọi nơi khác. File tạo SAU (dlp.db-wal, dlp.db-shm của SQLite) có quyền theo umask, nhưng người khác không
// đi qua được thư mục cha 0700 nên vẫn không đọc được. Gói cài đặt (GĐ4) đặt thêm UMask=0077 trong unit systemd.
// Dùng File.SetUnixFileMode của .NET (không cần P/Invoke chmod).
using System.Runtime.Versioning;

namespace DlpService.Platform.Linux;

/// <summary>Chỉ chủ sở hữu thư mục (tài khoản chạy service) truy cập được: thư mục 0700, file 0600.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class LinuxDataFolderGuard : IDataFolderGuard
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute; // 0700
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;                                // 0600

    /// <inheritdoc/>
    public void Restrict(string folder)
    {
        // Không phải chủ sở hữu → hệ điều hành từ chối (EPERM) → .NET ném UnauthorizedAccessException, đúng hợp đồng của interface
        File.SetUnixFileMode(folder, DirectoryMode);

        // Siết cả nội dung có sẵn (VD DB tạo từ lần chạy trước khi chưa khoá). Không đi theo symlink để không đổi quyền thứ nằm ngoài thư mục.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", options))
        {
            File.SetUnixFileMode(dir, DirectoryMode);
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            File.SetUnixFileMode(file, FileMode);
        }
    }

    /// <inheritdoc/>
    public string Description => $"chỉ chủ sở hữu ({Environment.UserName}), thư mục 0700, file 0600";
}
