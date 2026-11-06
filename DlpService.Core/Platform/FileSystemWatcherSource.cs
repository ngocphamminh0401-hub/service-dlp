// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): cài đặt IFileChangeSource bằng FileSystemWatcher của .NET — mã DI CHUYỂN NGUYÊN VĂN từ
// FileWatcherService.StartWatching/Dispose. FileSystemWatcher là API chung của .NET (Windows dùng ReadDirectoryChangesW, Linux dùng inotify)
// nên cài đặt này dùng được cả hai; riêng Linux sẽ cần chỉnh thêm (giới hạn watch, thư mục ẩn...) ở giai đoạn Linux.
namespace DlpService.Platform;

/// <summary>Theo dõi thư mục bằng <see cref="FileSystemWatcher"/> (mỗi thư mục 1 watcher).</summary>
/// <remarks>[Sửa lỗi] FileSystemWatcher chỉ tự giữ mình bằng tham chiếu yếu → nơi tạo PHẢI giữ đối tượng này suốt thời gian theo dõi,
/// nếu không GC thu hồi và việc theo dõi dừng âm thầm. Danh sách _watchers bên dưới giữ các watcher bằng tham chiếu mạnh.</remarks>
/// <remarks>[Thay đổi] Bước 7 tách lõi: Linux (inotify) KHÔNG tự theo dõi thư mục con mới — .NET phải tự đăng ký watch cho từng thư mục
/// con, mất vài chục ms. File ghi trong khoảng hở đó bị mất sự kiện vĩnh viễn (VD: cp -r, giải nén, git clone). Cách vá: khi thấy thư mục
/// con mới xuất hiện (tạo mới hoặc đổi tên/chuyển vào), chủ động quét các file đã có sẵn bên trong. Xem docs/fsw-thu-muc-con-linux.md.</remarks>
public sealed class FileSystemWatcherSource : IFileChangeSource
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _lock = new();

    // [Thay đổi] Bước 7: các sự kiện giờ đến từ 2 nơi (luồng watcher + luồng quét thư mục mới chạy nền). Khoá này bắt chúng báo LẦN LƯỢT,
    // vì người nghe (FileWatcherService.ScheduleProcess) không an toàn khi bị gọi song song cho cùng 1 file.
    private readonly Lock _raiseLock = new();
    private volatile bool _disposed;

    /// <inheritdoc/>
    public event Action<string>? FileChanged;

    /// <inheritdoc/>
    public event Action<string>? FileDeleted;

    /// <inheritdoc/>
    public event Action<string, bool, Exception>? Failed;

    /// <inheritdoc/>
    public void Watch(string folder, IEnumerable<string> extensions)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            // [Thay đổi] Nhóm D2 - mặc định FileSystemWatcher chỉ theo dõi ĐÚNG 1 cấp thư mục, bỏ sót mọi
            // thư mục con. Bật lên để nhìn xuyên xuống toàn bộ cây thư mục con.
            IncludeSubdirectories = true,
            // [Thay đổi] Nhóm D3: tăng buffer từ 8KB (mặc định) lên 64KB (mức tối đa khuyến nghị) để giảm
            // khả năng tràn khi nhiều file đổi dồn dập. (Trên Linux thuộc tính này không có tác dụng — xem nghien-cuu-chuyen-doi-linux.md 3.6.)
            InternalBufferSize = 64 * 1024
        };
        // [Thay đổi] Bước 7: BỎ watcher.Filters ("*.txt"...), tự lọc đuôi bằng tay. Lý do: Filters áp cho cả tên THƯ MỤC → thư mục "con"
        // không khớp "*.txt" nên không bao giờ có sự kiện "thư mục mới" để mà quét bù. Dùng 1 watcher không lọc thay vì thêm watcher thứ 2
        // chỉ để bắt thư mục — trên Linux mỗi watcher tốn riêng 1 inotify instance + 1 watch cho MỖI thư mục con (giới hạn max_user_watches).
        // So đuôi không phân biệt hoa/thường (".TXT" = ".txt") cho đồng nhất Windows/Linux — trước đây Linux phân biệt.
        var wanted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        bool Matches(string path) => wanted.Contains(Path.GetExtension(path));

        watcher.Created += (_, e) =>
        {
            if (Directory.Exists(e.FullPath)) RescanNewDirectory(e.FullPath, wanted);
            else if (Matches(e.FullPath)) RaiseChanged(e.FullPath);
        };
        watcher.Changed += (_, e) =>
        {
            // Thư mục cũng phát Changed khi nội dung bên trong đổi → bỏ qua (file bên trong đã có sự kiện riêng)
            if (Matches(e.FullPath) && !Directory.Exists(e.FullPath)) RaiseChanged(e.FullPath);
        };
        // [Thay đổi] Nhiều trình soạn thảo (VD VS Code) "tạo file mới" bằng cách ghi ra file tạm rồi ĐỔI TÊN thành tên thật — thao tác đó
        // chỉ phát sự kiện Renamed (không có Created/Changed). Đường dẫn mới là file cần quét; đường dẫn cũ coi như bị xoá.
        watcher.Renamed += (_, e) =>
        {
            // [Thay đổi] Bước 7: đổi tên/chuyển 1 THƯ MỤC vào → mọi file bên trong có đường dẫn mới → quét lại cả thư mục.
            // (Đường dẫn cũ của từng file không báo FileDeleted vì thư mục cũ đã không còn để liệt kê — FileDeleted hiện chưa ai xử lý, R4.)
            if (Directory.Exists(e.FullPath))
            {
                RescanNewDirectory(e.FullPath, wanted);
                return;
            }
            // [Thay đổi] Bước 7: chỉ báo phía nào khớp đuôi (trước đây Filters làm việc này một phần, đổi "a.txt" → "a.bak" vẫn báo a.bak)
            if (Matches(e.OldFullPath)) RaiseDeleted(e.OldFullPath);
            if (Matches(e.FullPath)) RaiseChanged(e.FullPath);
        };
        watcher.Deleted += (_, e) =>
        {
            if (Matches(e.FullPath)) RaiseDeleted(e.FullPath);
        };
        // [Thay đổi] Nhóm D3: nghe sự kiện Error — nếu không đăng ký, lỗi tràn buffer/mất thư mục trôi đi âm thầm
        watcher.Error += (_, e) =>
        {
            var ex = e.GetException();
            Failed?.Invoke(watcher.Path, ex is InternalBufferOverflowException, ex);
        };

        lock (_lock)
        {
            _watchers.Add(watcher);
        }
        watcher.EnableRaisingEvents = true;
    }

    // [Thay đổi] Bước 7: quét bù file có sẵn trong thư mục con vừa xuất hiện (kể cả thư mục cháu chắt bên trong).
    // Chạy nền (Task.Run): thư mục giải nén có thể chứa hàng nghìn file — quét ngay trên luồng watcher sẽ chặn nó, làm sự kiện dồn ứ.
    // File có thể bị báo 2 lần (1 lần từ đây, 1 lần từ sự kiện thường) → vô hại, FileWatcherService gộp lại bằng debounce 300ms theo đường dẫn.
    // Trên Windows bản vá này thừa (hệ điều hành đã báo đủ) nhưng vô hại vì cùng lý do.
    private void RescanNewDirectory(string directory, HashSet<string> wanted)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                {
                    if (_disposed) return;
                    if (wanted.Contains(Path.GetExtension(file))) RaiseChanged(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Thư mục bị xoá/đổi tên ngay sau khi tạo → không còn gì để quét bù, bỏ qua
            }
        });
    }

    // [Thay đổi] Bước 7: mọi lần báo sự kiện đi qua 2 hàm này để được khoá _raiseLock (báo lần lượt) và không báo sau khi đã Dispose
    private void RaiseChanged(string path)
    {
        lock (_raiseLock)
        {
            if (!_disposed) FileChanged?.Invoke(path);
        }
    }

    private void RaiseDeleted(string path)
    {
        lock (_raiseLock)
        {
            if (!_disposed) FileDeleted?.Invoke(path);
        }
    }

    /// <summary>Dừng theo dõi tất cả thư mục.</summary>
    public void Dispose()
    {
        _disposed = true; // [Thay đổi] Bước 7: chặn các lần quét bù còn đang chạy nền báo thêm sự kiện
        FileSystemWatcher[] toDispose;
        lock (_lock)
        {
            toDispose = [.. _watchers];
            _watchers.Clear();
        }
        foreach (var watcher in toDispose)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
    }
}
