// [Thay đổi] File mới (ACL thư mục dữ liệu, kiến trúc mục 8 "DB bị đọc"): khoá quyền thư mục chứa dlp.db để tài khoản
// thường (Users, Authenticated Users) không mở được DB. Chỉ SYSTEM (tài khoản chạy Windows Service), Administrators và
// tài khoản đang chạy process (để "dotnet run" bằng tài khoản của mình vẫn ghi được DB) có quyền.
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DlpService.Storage;

/// <summary>Đặt ACL chặt cho thư mục dữ liệu của service.</summary>
[SupportedOSPlatform("windows")]
public static class DataFolderSecurity
{
    /// <summary>Tắt kế thừa quyền từ thư mục cha, bỏ các quyền đã kế thừa, chỉ cấp Full Control cho SYSTEM,
    /// Administrators và tài khoản hiện tại. Quyền được áp cho cả thư mục con và file bên trong (dlp.db, -wal, -shm).</summary>
    /// <param name="folder">Thư mục dữ liệu — phải là thư mục RIÊNG của service, không phải thư mục dùng chung.</param>
    /// <exception cref="UnauthorizedAccessException">Tài khoản hiện tại không có quyền đổi ACL của thư mục.</exception>
    public static void Restrict(string folder)
    {
        var security = new DirectorySecurity();

        // isProtected=true: không nhận quyền từ thư mục cha nữa; preserveInheritance=false: xoá luôn các quyền đã kế thừa
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var allowed = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };

        // Tài khoản đang chạy process: khi chạy như service thì chính là SYSTEM (đã có, không thêm trùng)
        using var current = WindowsIdentity.GetCurrent();
        if (current.User is { } user && !allowed.Contains(user))
        {
            allowed.Add(user);
        }

        foreach (var sid in allowed)
        {
            // ContainerInherit + ObjectInherit: quyền tự áp xuống thư mục con và file (kể cả file tạo sau)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(folder).SetAccessControl(security);
    }
}
