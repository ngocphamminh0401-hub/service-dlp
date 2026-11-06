// [Thay đổi] File mới (GĐ3, docs/ke-hoach-trien-khai-linux.md mục 4.3 + 8.1; phương án D trong docs/fsw-thu-muc-con-linux.md):
// nguồn sự kiện thay đổi file gọi THẲNG inotify của Linux, thay cho FileSystemWatcher của .NET. Lý do không dùng FileSystemWatcher trên Linux:
//   1. Không bỏ được thư mục con khỏi việc theo dõi: IncludeSubdirectories gắn watch lên MỌI thư mục con (.cache, .git, node_modules...)
//      → home thật hết max_user_watches (máy dev này: 8192). Ở đây thư mục bị loại trừ KHÔNG được gắn watch.
//   2. Không cho biết số watch đang dùng, số lần tràn hàng đợi → không cảnh báo / ghi metric được (mục 8.1).
//   3. Báo IN_MODIFY cho MỖI lần ghi → 1 file lớn = hàng trăm sự kiện, và dễ đọc phải file đang ghi dở. Ở đây file thường chỉ báo khi
//      ĐÓNG sau khi ghi (IN_CLOSE_WRITE) hoặc khi được chuyển/đổi tên tới (IN_MOVED_TO) → thường đúng 1 sự kiện cho 1 lần lưu.
//   4. Gốc bị xoá / chuyển đi: tự báo Failed để FileWatcherService dựng lại (R6).
// Một inotify instance (1 file descriptor) cho mọi thư mục gốc; 1 luồng nền đọc sự kiện (poll 250 ms để dừng được).
// Thư mục con mới (tạo / chuyển vào): gắn watch cho cả cây con rồi quét bù file có sẵn (vá khoảng hở ở Bước 7, như FileSystemWatcherSource).
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DlpService.Platform.Linux;

/// <summary>Theo dõi thư mục bằng inotify (Linux). Cài <see cref="IWatchStatistics"/> để ghi metric.</summary>
[SupportedOSPlatform("linux")]
public sealed class InotifyFileChangeSource : IFileChangeSource, IWatchStatistics
{
    // ---- Hằng số của <sys/inotify.h> ----
    private const uint IN_CLOSE_WRITE = 0x00000008;
    private const uint IN_MOVED_FROM = 0x00000040;
    private const uint IN_MOVED_TO = 0x00000080;
    private const uint IN_CREATE = 0x00000100;
    private const uint IN_DELETE = 0x00000200;
    private const uint IN_DELETE_SELF = 0x00000400;
    private const uint IN_MOVE_SELF = 0x00000800;
    private const uint IN_UNMOUNT = 0x00002000;
    private const uint IN_Q_OVERFLOW = 0x00004000;
    private const uint IN_IGNORED = 0x00008000;
    private const uint IN_ONLYDIR = 0x01000000;
    private const uint IN_DONT_FOLLOW = 0x02000000;
    private const uint IN_EXCL_UNLINK = 0x04000000;
    private const uint IN_ISDIR = 0x40000000;
    private const int IN_NONBLOCK = 0x800;    // = O_NONBLOCK
    private const int IN_CLOEXEC = 0x80000;   // = O_CLOEXEC
    private const short POLLIN = 0x1;
    private const int EINTR = 4, EAGAIN = 11, ENOSPC = 28;

    // Sự kiện cần cho mỗi thư mục: file ghi xong, file/thư mục chuyển đến/đi, tạo (chỉ dùng cho thư mục), xoá, chính thư mục bị xoá/chuyển.
    // IN_DONT_FOLLOW: không đi theo symlink; IN_EXCL_UNLINK: bỏ sự kiện của file đã unlink nhưng còn mở; IN_ONLYDIR: chỉ gắn lên thư mục.
    private const uint WatchMask = IN_CLOSE_WRITE | IN_MOVED_FROM | IN_MOVED_TO | IN_CREATE | IN_DELETE | IN_DELETE_SELF | IN_MOVE_SELF
                                   | IN_ONLYDIR | IN_DONT_FOLLOW | IN_EXCL_UNLINK;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_init1(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_add_watch(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, uint mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_rm_watch(int fd, int wd);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd fds, nuint nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    // ---- Trạng thái ----
    // class (không phải record): so sánh theo THAM CHIẾU — Watch lại cùng đường dẫn tạo Root mới, sự kiện của Root cũ phải bị bỏ
    private sealed class Root(string path, HashSet<string> extensions, WatchExclusion exclusion)
    {
        public string Path { get; } = path;
        public HashSet<string> Extensions { get; } = extensions;
        public WatchExclusion Exclusion { get; } = exclusion;
        public bool FailedReported { get; set; }
    }

    private sealed record WatchedDir(int Wd, string Path, Root Root);

    private readonly int _fd;
    private readonly Thread _reader;
    private readonly Lock _mapLock = new();
    private readonly Dictionary<int, WatchedDir> _byWd = [];
    private readonly Dictionary<string, WatchedDir> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Root> _roots = new(StringComparer.Ordinal);
    private readonly Lock _raiseLock = new(); // báo sự kiện LẦN LƯỢT (xem FileSystemWatcherSource)
    private volatile bool _disposed;
    private int _failedWatches;
    private long _overflows;
    private long _rawEvents;

    /// <summary>Mở 1 inotify instance và bắt đầu luồng đọc sự kiện.</summary>
    /// <exception cref="Win32Exception">Không mở được inotify (VD hết max_user_instances).</exception>
    public InotifyFileChangeSource()
    {
        _fd = inotify_init1(IN_NONBLOCK | IN_CLOEXEC);
        if (_fd < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "inotify_init1 thất bại (có thể hết fs.inotify.max_user_instances)");
        }
        WatchLimit = ReadProcInt("/proc/sys/fs/inotify/max_user_watches");
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "DlpInotifyReader" };
        _reader.Start();
    }

    /// <inheritdoc/>
    public event Action<string>? FileChanged;

    /// <inheritdoc/>
    public event Action<string>? FileDeleted;

    /// <inheritdoc/>
    public event Action<string, bool, Exception>? Failed;

    /// <inheritdoc/>
    public int WatchCount { get { lock (_mapLock) return _byWd.Count; } }

    /// <inheritdoc/>
    public int WatchLimit { get; }

    /// <inheritdoc/>
    public int FailedWatchCount => Volatile.Read(ref _failedWatches);

    /// <inheritdoc/>
    public long OverflowCount => Interlocked.Read(ref _overflows);

    /// <inheritdoc/>
    public long RawEventCount => Interlocked.Read(ref _rawEvents);

    /// <inheritdoc/>
    /// <exception cref="IOException">Không gắn được watch lên chính thư mục gốc.</exception>
    public void Watch(string folder, IEnumerable<string> extensions, WatchExclusion? exclusion = null)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var root = new Root(path, new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase), exclusion ?? WatchExclusion.None);
        lock (_mapLock)
        {
            if (_roots.ContainsKey(path)) RemoveTreeLocked(path, rmWatch: true);
            _roots[path] = root;
            if (AddWatchLocked(path, root) == null)
            {
                _roots.Remove(path);
                throw new IOException($"Không gắn được inotify watch lên {path} (errno {Marshal.GetLastPInvokeError()})");
            }
        }
        AddSubtree(path, root);
    }

    /// <inheritdoc/>
    public void Unwatch(string folder)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        lock (_mapLock)
        {
            if (_roots.Remove(path))
            {
                RemoveTreeLocked(path, rmWatch: true);
            }
        }
    }

    // ---- Gắn / gỡ watch ----

    // Gắn watch cho mọi thư mục con (bỏ thư mục loại trừ, không theo symlink). Gọi SAU khi đã gắn watch cho chính `dir`, để thư mục con
    // tạo ra trong lúc đang liệt kê vẫn có sự kiện IN_CREATE (→ lại vào đây; gắn trùng không sao, kernel trả lại cùng wd).
    private void AddSubtree(string dir, Root root)
    {
        IEnumerable<string> subdirs;
        try
        {
            subdirs = root.Exclusion.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // thư mục vừa bị xoá / không đọc được
        }
        lock (_mapLock)
        {
            if (!_roots.ContainsValue(root)) return; // đã Unwatch trong lúc liệt kê
            foreach (var sub in subdirs)
            {
                AddWatchLocked(sub, root);
            }
        }
    }

    private WatchedDir? AddWatchLocked(string dir, Root root)
    {
        int wd = inotify_add_watch(_fd, dir, WatchMask);
        if (wd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            // ENOSPC = hết max_user_watches; EACCES/ENOENT/ENOTDIR = mất quyền / vừa bị xoá. Đếm lại để cảnh báo + metric; thư mục này chỉ
            // còn được phát hiện thay đổi nhờ quét đối soát định kỳ.
            if (errno == ENOSPC || Directory.Exists(dir)) Interlocked.Increment(ref _failedWatches); // thư mục vừa bị xoá thì không tính
            return null;
        }
        // Gắn lại cùng thư mục (VD vừa chuyển chỗ trong vùng theo dõi) → kernel trả lại wd cũ: cập nhật đường dẫn mới
        if (_byWd.Remove(wd, out var old)) _byPath.Remove(old.Path);
        var entry = new WatchedDir(wd, dir, root);
        _byWd[wd] = entry;
        _byPath[dir] = entry;
        return entry;
    }

    // Gỡ mọi watch của `dir` và cây con của nó khỏi bảng. rmWatch=false khi kernel đã tự gỡ (thư mục bị xoá → IN_IGNORED sẽ tới).
    private void RemoveTreeLocked(string dir, bool rmWatch)
    {
        string prefix = dir + "/";
        var toRemove = _byPath.Values.Where(w => w.Path == dir || w.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var w in toRemove)
        {
            _byPath.Remove(w.Path);
            _byWd.Remove(w.Wd);
            if (rmWatch) inotify_rm_watch(_fd, w.Wd);
        }
    }

    // ---- Đọc và xử lý sự kiện ----

    private void ReadLoop()
    {
        var buffer = new byte[64 * 1024];
        var pfd = new PollFd { Fd = _fd, Events = POLLIN };
        while (!_disposed)
        {
            pfd.Revents = 0;
            int ready = poll(ref pfd, 1, 250);
            if (_disposed) return;
            if (ready <= 0) continue; // hết giờ chờ, hoặc bị ngắt (EINTR) → vòng lại
            nint n = read(_fd, buffer, buffer.Length);
            if (n <= 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (n < 0 && errno != EAGAIN && errno != EINTR && !_disposed)
                {
                    RaiseFailedForAll(false, new Win32Exception(errno, "đọc inotify thất bại"));
                    Thread.Sleep(1000);
                }
                continue;
            }
            int offset = 0;
            while (offset + 16 <= n)
            {
                int wd = BitConverter.ToInt32(buffer, offset);
                uint mask = BitConverter.ToUInt32(buffer, offset + 4);
                int len = (int)BitConverter.ToUInt32(buffer, offset + 12);
                string name = len == 0 ? "" : DecodeName(buffer, offset + 16, len);
                offset += 16 + len;
                Interlocked.Increment(ref _rawEvents);
                try
                {
                    Handle(wd, mask, name);
                }
                catch (Exception ex)
                {
                    // Lỗi xử lý 1 sự kiện không được làm chết luồng đọc (service sẽ "câm" mà không ai biết)
                    RaiseFailedForAll(false, ex);
                }
            }
        }
    }

    private void Handle(int wd, uint mask, string name)
    {
        if ((mask & IN_Q_OVERFLOW) != 0)
        {
            // Hàng đợi của kernel tràn (giải nén hàng chục nghìn file...) → đã mất sự kiện → mọi gốc phải quét bù
            Interlocked.Increment(ref _overflows);
            RaiseFailedForAll(true, new InternalBufferOverflowException("inotify: tràn hàng đợi sự kiện (IN_Q_OVERFLOW)"));
            return;
        }

        WatchedDir? dir;
        lock (_mapLock)
        {
            _byWd.TryGetValue(wd, out dir);
            if (dir != null && (mask & IN_IGNORED) != 0)
            {
                // Kernel đã gỡ watch (thư mục bị xoá, ổ bị gỡ, hoặc ta vừa rm_watch)
                _byWd.Remove(wd);
                if (_byPath.TryGetValue(dir.Path, out var cur) && cur.Wd == wd) _byPath.Remove(dir.Path);
            }
        }
        if (dir == null) return; // sự kiện của watch đã gỡ

        bool isRoot = dir.Path == dir.Root.Path;
        if ((mask & (IN_DELETE_SELF | IN_MOVE_SELF | IN_UNMOUNT | IN_IGNORED)) != 0)
        {
            // Thư mục con tự biến mất: sự kiện IN_DELETE / IN_MOVED_FROM ở thư mục cha đã lo phần báo FileDeleted.
            // Chính thư mục GỐC biến mất → không còn gì để theo dõi → báo Failed để FileWatcherService chờ nó xuất hiện lại rồi Watch lại (R6).
            if (isRoot && !_disposed)
            {
                bool stillWatched;
                lock (_mapLock) stillWatched = _roots.TryGetValue(dir.Root.Path, out var r) && ReferenceEquals(r, dir.Root);
                if (stillWatched && !dir.Root.FailedReported)
                {
                    dir.Root.FailedReported = true;
                    Failed?.Invoke(dir.Root.Path, false, new DirectoryNotFoundException($"Thư mục gốc không còn theo dõi được (inotify mask 0x{mask:X})"));
                }
            }
            return;
        }
        if (name.Length == 0) return;

        string full = dir.Path + "/" + name;
        bool isDir = (mask & IN_ISDIR) != 0;
        var root = dir.Root;

        if (isDir)
        {
            if ((mask & (IN_CREATE | IN_MOVED_TO)) != 0)
            {
                if (root.Exclusion.IsExcludedName(name)) return; // thư mục bị loại trừ: không gắn watch, không quét
                lock (_mapLock)
                {
                    if (!_roots.ContainsValue(root)) return;
                    AddWatchLocked(full, root);
                }
                AddSubtree(full, root);
                RescanNewDirectory(full, root);
            }
            else if ((mask & (IN_DELETE | IN_MOVED_FROM)) != 0)
            {
                // Chuyển đi: kernel GIỮ watch (theo inode) → phải tự gỡ cả cây; xoá: kernel tự gỡ (IN_IGNORED sẽ tới)
                lock (_mapLock) RemoveTreeLocked(full, rmWatch: (mask & IN_MOVED_FROM) != 0);
                if (!root.Exclusion.IsExcludedName(name)) RaiseDeleted(full);
            }
            return;
        }

        // File thường
        if ((mask & (IN_CLOSE_WRITE | IN_MOVED_TO)) != 0)
        {
            if (root.Extensions.Contains(Path.GetExtension(name))) RaiseChanged(full);
        }
        else if ((mask & (IN_DELETE | IN_MOVED_FROM)) != 0)
        {
            RaiseDeleted(full); // không lọc đuôi (hợp đồng FileDeleted)
        }
        // IN_CREATE của file thường: bỏ qua — file vừa tạo thường còn rỗng; IN_CLOSE_WRITE sẽ tới khi ghi xong
    }

    // Quét bù file có sẵn trong thư mục vừa xuất hiện (cp -r, giải nén, mv vào) — chạy nền để không chặn luồng đọc sự kiện
    private void RescanNewDirectory(string directory, Root root)
    {
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var file in root.Exclusion.EnumerateFiles(directory))
                {
                    if (_disposed) return;
                    if (root.Extensions.Contains(Path.GetExtension(file))) RaiseChanged(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Thư mục bị xoá/đổi tên ngay sau khi tạo → bỏ qua
            }
        });
    }

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

    private void RaiseFailedForAll(bool overflowed, Exception ex)
    {
        string[] roots;
        lock (_mapLock) roots = [.. _roots.Keys];
        foreach (var r in roots)
        {
            if (_disposed) return;
            Failed?.Invoke(r, overflowed, ex);
        }
    }

    // Tên trong inotify_event là UTF-8, đệm thêm byte 0 cho đủ len
    private static string DecodeName(byte[] buffer, int start, int len)
    {
        int end = Array.IndexOf(buffer, (byte)0, start, len);
        return System.Text.Encoding.UTF8.GetString(buffer, start, (end < 0 ? start + len : end) - start);
    }

    private static int ReadProcInt(string path)
    {
        try
        {
            return int.TryParse(File.ReadAllText(path).Trim(), out int v) ? v : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Dừng luồng đọc và đóng inotify (kernel tự gỡ mọi watch).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Join(TimeSpan.FromSeconds(2));
        close(_fd);
        lock (_mapLock)
        {
            _byWd.Clear();
            _byPath.Clear();
            _roots.Clear();
        }
    }
}
