// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): cài đặt IFileChangeSource bằng FileSystemWatcher của .NET — mã DI CHUYỂN NGUYÊN VĂN từ
// FileWatcherService.StartWatching/Dispose. FileSystemWatcher là API chung của .NET (Windows dùng ReadDirectoryChangesW, Linux dùng inotify)
// nên cài đặt này dùng được cả hai; riêng Linux sẽ cần chỉnh thêm (giới hạn watch, thư mục ẩn...) ở giai đoạn Linux.
namespace DlpService.Platform;

/// <summary>Theo dõi thư mục bằng <see cref="FileSystemWatcher"/> (mỗi thư mục 1 watcher).</summary>
/// <remarks>[Sửa lỗi] FileSystemWatcher chỉ tự giữ mình bằng tham chiếu yếu → nơi tạo PHẢI giữ đối tượng này suốt thời gian theo dõi,
/// nếu không GC thu hồi và việc theo dõi dừng âm thầm. Danh sách _watchers bên dưới giữ các watcher bằng tham chiếu mạnh.</remarks>
public sealed class FileSystemWatcherSource : IFileChangeSource
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _lock = new();

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
        // Mỗi đuôi hỗ trợ 1 filter ("*.txt", "*.docx", ...)
        foreach (var extension in extensions)
        {
            watcher.Filters.Add("*" + extension);
        }
        watcher.Created += (_, e) => FileChanged?.Invoke(e.FullPath);
        watcher.Changed += (_, e) => FileChanged?.Invoke(e.FullPath);
        // [Thay đổi] Nhiều trình soạn thảo (VD VS Code) "tạo file mới" bằng cách ghi ra file tạm rồi ĐỔI TÊN thành tên thật — thao tác đó
        // chỉ phát sự kiện Renamed (không có Created/Changed). Đường dẫn mới là file cần quét; đường dẫn cũ coi như bị xoá.
        watcher.Renamed += (_, e) =>
        {
            FileDeleted?.Invoke(e.OldFullPath);
            FileChanged?.Invoke(e.FullPath);
        };
        watcher.Deleted += (_, e) => FileDeleted?.Invoke(e.FullPath);
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

    /// <summary>Dừng theo dõi tất cả thư mục.</summary>
    public void Dispose()
    {
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
