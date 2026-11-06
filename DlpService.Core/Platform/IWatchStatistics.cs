// [Thay đổi] File mới (GĐ3 bước 3.2/3.6): số liệu của nguồn sự kiện để ghi metric định kỳ và cảnh báo hết watch (ke-hoach-trien-khai-linux.md 8.1).
// Không bắt buộc: nguồn nào không đo được (FileSystemWatcher của .NET không cho biết số watch) thì không cài interface này.
namespace DlpService.Platform;

/// <summary>Số liệu của nguồn sự kiện thay đổi file. Chỉ là số đếm, không chứa đường dẫn hay nội dung (R7).</summary>
public interface IWatchStatistics
{
    /// <summary>Số thư mục đang được theo dõi (inotify: số watch đang dùng).</summary>
    int WatchCount { get; }

    /// <summary>Giới hạn số watch của hệ điều hành cho tài khoản này; -1 nếu không biết.</summary>
    int WatchLimit { get; }

    /// <summary>Số thư mục KHÔNG gắn được watch (hết giới hạn, mất quyền...) — các thư mục này chỉ được phát hiện thay đổi nhờ quét đối soát.</summary>
    int FailedWatchCount { get; }

    /// <summary>Tổng số lần hàng đợi sự kiện của hệ điều hành bị tràn (mỗi lần → quét bù).</summary>
    long OverflowCount { get; }

    /// <summary>Tổng số sự kiện thô nhận từ hệ điều hành (trước khi lọc đuôi/gộp).</summary>
    long RawEventCount { get; }
}
