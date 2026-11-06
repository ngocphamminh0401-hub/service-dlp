// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): nguồn sự kiện thay đổi file. Trước đây FileWatcherService gọi thẳng FileSystemWatcher;
// tách thành interface để mỗi hệ điều hành có cách theo dõi riêng nếu cần (Windows: ReadDirectoryChangesW, Linux: inotify, macOS: FSEvents)
// mà phần debounce/giới hạn đồng thời/xử lý file không phải đổi.
namespace DlpService.Platform;

/// <summary>Báo khi file trong các thư mục được theo dõi bị tạo/sửa/đổi tên/xoá.</summary>
/// <remarks>Các sự kiện có thể được gọi từ luồng bất kỳ (luồng của hệ điều hành/ThreadPool); người nghe phải tự an toàn đa luồng.</remarks>
public interface IFileChangeSource : IDisposable
{
    /// <summary>File vừa được tạo, sửa, hoặc đổi tên thành (đường dẫn mới). Tham số: đường dẫn đầy đủ.</summary>
    event Action<string>? FileChanged;

    /// <summary>File vừa bị xoá, hoặc bị đổi tên khỏi đường dẫn này. Tham số: đường dẫn đầy đủ.
    /// (Hiện FileWatcherService chưa xử lý sự kiện này — rủi ro R4 trong kế hoạch Linux; interface có sẵn để thêm sau.)</summary>
    event Action<string>? FileDeleted;

    /// <summary>Việc theo dõi 1 thư mục gặp sự cố. Tham số: thư mục gốc, đã mất sự kiện do tràn hàng đợi chưa
    /// (true → cần quét bù cả thư mục; false → lỗi khác, có thể đã ngừng theo dõi), và ngoại lệ gốc.</summary>
    event Action<string, bool, Exception>? Failed;

    /// <summary>Bắt đầu theo dõi 1 thư mục (kể cả thư mục con). Gọi nhiều lần để theo dõi nhiều thư mục.
    /// Thư mục phải tồn tại — bên gọi kiểm tra trước.</summary>
    /// <param name="folder">Thư mục cần theo dõi.</param>
    /// <param name="extensions">Chỉ báo các file có đuôi này (có dấu chấm, VD ".docx").</param>
    void Watch(string folder, IEnumerable<string> extensions);
}
