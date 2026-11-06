// [Thay đổi] File mới (GĐ3): kiểm tra FileWatcherService + ResultStore THẬT (SQLite trong thư mục tạm) ở các tình huống của kế hoạch GĐ3:
// xoá / đổi tên / chuyển thư mục ra ngoài (R4), đối soát lúc khởi động, thư mục loại trừ, thư mục gốc bị xoá rồi tạo lại (R6), người dùng
// mới khớp mẫu (3.5). Trên Linux dùng nguồn inotify riêng (đúng như host Linux), nơi khác dùng FileSystemWatcher.
using DlpService.Classification;
using DlpService.Platform;
using DlpService.Scanning;
using DlpService.Storage;
using DlpService.Watching;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Tests.Watching;

public sealed class FileWatcherLifecycleTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-life-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_work, "watch");
    private readonly ResultStore _store;
    private FileWatcherService? _service;

    public FileWatcherLifecycleTests()
    {
        Directory.CreateDirectory(Root);
        _store = new ResultStore(Path.Combine(_work, "data", "dlp.db"));
    }

    public void Dispose()
    {
        _service?.Dispose();
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static IFileChangeSource NewSource() =>
        OperatingSystem.IsLinux() ? new DlpService.Platform.Linux.InotifyFileChangeSource() : new FileSystemWatcherSource();

    private FileWatcherService Start(WatchOptions? options = null, bool watchRoot = true)
    {
        IPathPolicy policy = OperatingSystem.IsLinux() ? new DlpService.Platform.Linux.LinuxPathPolicy() : new CaseInsensitivePathPolicy();
        _service = new FileWatcherService(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), NullLogger.Instance,
            store: _store, changeSource: NewSource(), pathPolicy: policy, options: options);
        if (watchRoot) _service.StartWatching(Root);
        return _service;
    }

    private bool Has(string path) => _store.TryGetFileState(path) != null;

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public void Xoa_file_thi_xoa_ban_ghi()
    {
        Start();
        string file = Path.Combine(Root, "a.txt");
        File.WriteAllText(file, "noi dung");
        Assert.True(WaitFor(() => Has(file)), "file mới phải được ghi DB");

        File.Delete(file);

        Assert.True(WaitFor(() => !Has(file)), "xoá file phải xoá bản ghi");
    }

    [Fact]
    public void Doi_ten_file_thi_chuyen_ban_ghi_sang_ten_moi()
    {
        Start();
        string oldPath = Path.Combine(Root, "cu.txt");
        File.WriteAllText(oldPath, "noi dung");
        Assert.True(WaitFor(() => Has(oldPath)));

        string newPath = Path.Combine(Root, "moi.txt");
        File.Move(oldPath, newPath);

        Assert.True(WaitFor(() => Has(newPath) && !Has(oldPath)), "đổi tên: bản ghi cũ mất, bản ghi mới có");
    }

    [Fact]
    public void Chuyen_thu_muc_ra_ngoai_thi_xoa_ban_ghi_ca_cay()
    {
        string sub = Path.Combine(Root, "du-an", "con");
        Directory.CreateDirectory(sub);
        string f1 = Path.Combine(Root, "du-an", "a.txt");
        string f2 = Path.Combine(sub, "b.txt");
        File.WriteAllText(f1, "x");
        File.WriteAllText(f2, "y");
        Start();
        Assert.True(Has(f1) && Has(f2), "quét khởi động phải ghi cả 2 file");

        Directory.Move(Path.Combine(Root, "du-an"), Path.Combine(_work, "ngoai"));

        Assert.True(WaitFor(() => !Has(f1) && !Has(f2)), "chuyển thư mục ra ngoài: mọi bản ghi bên trong phải bị xoá");
    }

    [Fact]
    public void Khoi_dong_doi_soat_xoa_ban_ghi_file_da_mat_luc_service_tat()
    {
        string gone = Path.Combine(Root, "da-xoa.txt");
        string outside = Path.Combine(_work, "ngoai-vung", "khac.txt");
        foreach (var p in new[] { gone, outside })
        {
            _store.SaveScan(new ScanRecord(p, 1, DateTime.UtcNow, "h", 0, ResultStore.StatusFinal, false, "{}", []));
        }
        string kept = Path.Combine(Root, "con-day.txt");
        File.WriteAllText(kept, "x");

        Start();

        Assert.False(Has(gone), "bản ghi file không còn trong vùng theo dõi phải bị xoá khi khởi động");
        Assert.True(Has(outside), "bản ghi NGOÀI vùng theo dõi không được đụng tới");
        Assert.True(Has(kept));
    }

    [Fact]
    public void Thu_muc_bi_loai_tru_khong_duoc_quet()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".cache"));
        Directory.CreateDirectory(Path.Combine(Root, "node_modules"));
        string hidden = Path.Combine(Root, ".cache", "a.txt");
        string modules = Path.Combine(Root, "node_modules", "b.txt");
        string normal = Path.Combine(Root, "c.txt");
        File.WriteAllText(hidden, "x");
        File.WriteAllText(modules, "x");
        File.WriteAllText(normal, "x");

        Start(new WatchOptions { Exclusion = new WatchExclusion(true, ["node_modules"]) });

        Assert.True(Has(normal));
        Assert.False(Has(hidden));
        Assert.False(Has(modules));
    }

    [Fact]
    public void Goc_bi_xoa_roi_tao_lai_thi_tu_theo_doi_lai()
    {
        Start(new WatchOptions { RecoveryInitialDelay = TimeSpan.FromMilliseconds(200), RecoveryMaxDelay = TimeSpan.FromMilliseconds(400) });
        string before = Path.Combine(Root, "truoc.txt");
        File.WriteAllText(before, "x");
        Assert.True(WaitFor(() => Has(before)));

        Directory.Delete(Root, recursive: true);
        Assert.True(WaitFor(() => !Has(before)), "xoá gốc: bản ghi bên trong phải bị xoá");
        Thread.Sleep(500);
        Directory.CreateDirectory(Root);
        Thread.Sleep(1500); // chờ vòng theo dõi lại (R6) gắn xong
        string after = Path.Combine(Root, "sau.txt");
        File.WriteAllText(after, "y");

        Assert.True(WaitFor(() => Has(after)), "gốc tạo lại phải được theo dõi lại tự động (R6)");
    }

    // Nguồn sự kiện "câm" — mô phỏng sự kiện bị mất hoàn toàn (hết watch, tràn không báo...). Chỉ quét đối soát còn bắt được thay đổi.
    private sealed class SilentSource : IFileChangeSource
    {
        public event Action<string>? FileChanged { add { } remove { } }
        public event Action<string>? FileDeleted { add { } remove { } }
        public event Action<string, bool, Exception>? Failed { add { } remove { } }
        public void Watch(string folder, IEnumerable<string> extensions, WatchExclusion? exclusion = null) { }
        public void Unwatch(string folder) { }
        public void Dispose() { }
    }

    [Fact]
    public void Doi_soat_dinh_ky_bat_duoc_thay_doi_khi_mat_su_kien()
    {
        string existing = Path.Combine(Root, "co-san.txt");
        File.WriteAllText(existing, "x");
        _service = new FileWatcherService(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), NullLogger.Instance,
            store: _store, changeSource: new SilentSource(), options: new WatchOptions { ReconcileInterval = TimeSpan.FromSeconds(1) });
        _service.StartWatching(Root);
        _service.StartBackgroundTasks();
        Assert.True(Has(existing));

        string added = Path.Combine(Root, "moi-khong-co-su-kien.txt");
        File.WriteAllText(added, "y");
        File.Delete(existing);

        Assert.True(WaitFor(() => Has(added) && !Has(existing)), "đối soát định kỳ phải thêm file mới + xoá bản ghi file đã mất");
    }

    [Fact]
    public void Nguoi_dung_moi_khop_mau_duoc_theo_doi()
    {
        var service = Start(new WatchOptions { PatternRefreshInterval = TimeSpan.FromSeconds(1) }, watchRoot: false);
        string pattern = Path.Combine(Root, "*", "Documents");
        service.TrackPattern(pattern);
        service.StartBackgroundTasks();

        string docs = Path.Combine(Root, "nguoi-moi", "Documents");
        Directory.CreateDirectory(docs);
        string file = Path.Combine(docs, "hop-dong.txt");
        File.WriteAllText(file, "x"); // có sẵn trước khi được theo dõi → lượt quét khởi động của thư mục mới phải bắt được

        Assert.True(WaitFor(() => Has(file)), "người dùng mới khớp mẫu phải được theo dõi + quét");
        Assert.Contains(docs, service.WatchedFolders);
    }
}
