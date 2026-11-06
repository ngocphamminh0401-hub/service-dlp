// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): adapter Windows của IDataFolderGuard — bọc DataFolderSecurity (ACL) sẵn có, không đổi logic.
// Đây là phần chỉ host Windows được dùng; lõi không tham chiếu lớp này.
using System.Runtime.Versioning;
using DlpService.Storage;

namespace DlpService.Platform;

/// <summary>Khoá thư mục bằng ACL Windows: chỉ SYSTEM, Administrators và tài khoản đang chạy process.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDataFolderGuard : IDataFolderGuard
{
    /// <inheritdoc/>
    public void Restrict(string folder) => DataFolderSecurity.Restrict(folder);

    /// <inheritdoc/>
    public string Description => "SYSTEM, Administrators, tài khoản hiện tại";
}
