// [Thay đổi] File mới (M1 — fail-closed khi không khoá được thư mục DB): ResultStore ném lỗi này khi được yêu cầu bảo vệ thư mục dữ liệu
// (requireProtectedFolder) mà không làm được. Ném TRƯỚC khi mở/tạo file DB → không có dữ liệu nào nằm trong thư mục chưa khoá.
// Worker bắt riêng loại này để DỪNG service (exit code 1), khác với lỗi DB thông thường (chạy tiếp không ghi DB).
namespace DlpService.Storage;

/// <summary>Không bảo vệ được thư mục chứa DB (DB chứa nhãn + đường dẫn file của mọi người dùng — không được để tài khoản khác đọc).</summary>
public sealed class DataFolderProtectionException : Exception
{
    /// <summary>Thư mục dữ liệu không khoá được.</summary>
    public string Folder { get; }

    public DataFolderProtectionException(string folder, string reason, Exception? inner = null)
        : base($"Không bảo vệ được thư mục dữ liệu {folder}: {reason}", inner)
    {
        Folder = folder;
    }
}
