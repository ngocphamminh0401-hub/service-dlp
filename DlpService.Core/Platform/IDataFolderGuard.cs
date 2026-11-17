// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): bảo vệ thư mục chứa DB. Trước đây ResultStore gọi thẳng DataFolderSecurity (ACL Windows,
// System.Security.AccessControl) sau một lần kiểm tra OperatingSystem.IsWindows(). Tách thành interface để lõi (ResultStore) không phụ thuộc
// API Windows: Windows dùng ACL, Linux sau này dùng chủ sở hữu `dlp` + chmod 0700.
namespace DlpService.Platform;

/// <summary>Khoá quyền truy cập một thư mục dữ liệu của service.</summary>
public interface IDataFolderGuard
{
    /// <summary>Chỉ cho tài khoản chạy service (và quản trị) truy cập thư mục này, kể cả file con và file tạo sau.
    /// Thư mục phải là thư mục RIÊNG của service, không phải thư mục dùng chung.</summary>
    /// <exception cref="UnauthorizedAccessException">Không đủ quyền để đổi quyền thư mục.</exception>
    /// <param name="folder">Thư mục dữ liệu, đã tồn tại.</param>
    void Restrict(string folder);

    /// <summary>Mô tả ngắn quyền sau khi khoá, dùng để ghi log (VD "SYSTEM, Administrators, tài khoản hiện tại").</summary>
    string Description { get; }

    // [Thay đổi] M1: default interface member — guard Windows/Linux không phải sửa (mặc định true); chỉ NoOpDataFolderGuard trả false.
    // ResultStore dùng cờ này để không coi "guard không khoá gì" là đã bảo vệ (trước đây NoOp chạy êm, log "(không khoá)" rồi mở DB).
    /// <summary>true nếu <see cref="Restrict"/> thật sự giới hạn quyền truy cập (false = guard chỉ để chạy thử/test).</summary>
    bool Enforces => true;
}
