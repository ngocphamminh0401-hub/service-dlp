// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): nguồn sự kiện thay đổi file. Trước đây FileWatcherService gọi thẳng FileSystemWatcher;
// tách thành interface để mỗi hệ điều hành có cách theo dõi riêng nếu cần (Windows: ReadDirectoryChangesW, Linux: inotify, macOS: FSEvents)
// mà phần debounce/giới hạn đồng thời/xử lý file không phải đổi.
// [Thay đổi] GĐ3: + loại trừ thư mục (3.2), + Unwatch để dựng lại khi lỗi (3.3, R6); FileDeleted nay được xử lý (3.1, R4).
namespace DlpService.Platform;

/// <summary>Báo khi file trong các thư mục được theo dõi bị tạo/sửa/đổi tên/xoá.</summary>
/// <remarks>Các sự kiện có thể được gọi từ luồng bất kỳ (luồng của hệ điều hành/ThreadPool); người nghe phải tự an toàn đa luồng.</remarks>
public interface IFileChangeSource : IDisposable
{
    /// <summary>File vừa được tạo, sửa, hoặc đổi tên thành (đường dẫn mới). Chỉ file đúng đuôi, không nằm trong thư mục bị loại trừ.
    /// Thư mục mới xuất hiện (tạo / chuyển vào / đổi tên thành) → báo cho từng file đúng đuôi đang có bên trong. Tham số: đường dẫn đầy đủ.</summary>
    event Action<string>? FileChanged;

    /// <summary>[Thay đổi] GĐ3 (R4): một đường dẫn vừa BIẾN MẤT — file bị xoá, hoặc thư mục bị xoá / đổi tên / chuyển ra ngoài, hoặc file bị đổi
    /// tên khỏi đường dẫn này. KHÔNG lọc theo đuôi: lúc này không còn biết đường dẫn từng là file hay thư mục, nên người nghe phải coi
    /// nó như "đường dẫn này và mọi thứ bên dưới nó không còn". Tham số: đường dẫn đầy đủ.</summary>
    event Action<string>? FileDeleted;

    /// <summary>Việc theo dõi 1 thư mục gặp sự cố. Tham số: thư mục gốc, đã mất sự kiện do tràn hàng đợi chưa
    /// (true → cần quét bù cả thư mục; false → lỗi khác, có thể đã ngừng theo dõi — người nghe nên Unwatch rồi Watch lại), và ngoại lệ gốc.</summary>
    event Action<string, bool, Exception>? Failed;

    /// <summary>Bắt đầu theo dõi 1 thư mục (kể cả thư mục con). Gọi nhiều lần để theo dõi nhiều thư mục.
    /// Thư mục phải tồn tại — bên gọi kiểm tra trước.</summary>
    /// <param name="folder">Thư mục cần theo dõi.</param>
    /// <param name="extensions">Chỉ báo các file có đuôi này (có dấu chấm, VD ".docx").</param>
    /// <param name="exclusion">[Thay đổi] GĐ3: thư mục con bỏ qua; null = không bỏ gì.</param>
    void Watch(string folder, IEnumerable<string> extensions, WatchExclusion? exclusion = null);

    /// <summary>[Thay đổi] GĐ3 (R6): ngừng theo dõi 1 thư mục gốc đã Watch (để dựng lại sau lỗi). Thư mục chưa Watch → không làm gì.</summary>
    void Unwatch(string folder);
}
