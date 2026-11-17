// [Thay đổi] File mới (M2 — hàng chờ có giới hạn + backpressure): bắn rất nhiều sự kiện qua 1 nguồn sự kiện GIẢ (không cần tạo file thật) rồi đo
// số việc đang chờ + bộ nhớ heap NGAY sau khi bắn (lúc mọi việc còn đang trong debounce). Số đo bản cũ (trước M2) ghi trong comment của test.
using DlpService.Classification;
using DlpService.Platform;
using DlpService.Scanning;
using DlpService.Storage;
using DlpService.Watching;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DlpService.Tests.Watching;

public sealed class FileWatcherBackpressureTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-bp-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_work, "watch");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Nguồn sự kiện giả: test tự bắn FileChanged
    internal sealed class FakeSource : IFileChangeSource
    {
        public event Action<string>? FileChanged;
        public event Action<string>? FileDeleted { add { } remove { } }
        public event Action<string, bool, Exception>? Failed { add { } remove { } }
        public void Fire(string path) => FileChanged?.Invoke(path);
        public void Watch(string folder, IEnumerable<string> extensions, WatchExclusion? exclusion = null) { }
        public void Unwatch(string folder) { }
        public void Dispose() { }
    }

    // Số đo bản cũ (trước M2, máy dev 17/11/2026): 50.000 sự kiện → đang chờ 50.000, heap +42 MB (mỗi path 1 Task + CTS + chỗ chờ semaphore).
    [Fact]
    public void Burst_50000_su_kien_khong_vuot_tran_va_danh_dau_quet_bu()
    {
        Directory.CreateDirectory(Root);
        var source = new FakeSource();
        using var service = new FileWatcherService(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), NullLogger.Instance,
            changeSource: source, maxConcurrency: 2);
        service.StartWatching(Root);

        long before = GC.GetTotalMemory(forceFullCollection: true);
        for (int i = 0; i < 50_000; i++) source.Fire(Path.Combine(Root, $"d{i % 100}", $"f{i}.txt"));
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long heapMb = (after - before) / 1024 / 1024;
        output.WriteLine($"50.000 sự kiện: đang chờ {service.PendingCount}, thư mục chờ quét bù {service.DirtyRootCount}, heap +{heapMb} MB");

        Assert.True(service.PendingCount <= 10_000, $"đang chờ {service.PendingCount} > trần 10.000");
        Assert.Equal(1, service.DirtyRootCount); // 40.000 sự kiện tràn → đúng 1 thư mục gốc cần quét bù (không 40.000 việc)
        Assert.True(heapMb < 15, $"heap +{heapMb} MB (bản cũ: +42 MB)");
    }

    private static ResultStore NewStore(string dir) => new(Path.Combine(dir, "data", "dlp.db"));

    private static bool WaitFor(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public void Hang_cho_day_khong_mat_file_nao_nho_quet_bu()
    {
        Directory.CreateDirectory(Root);
        using var store = NewStore(_work);
        var source = new FakeSource();
        using var service = new FileWatcherService(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), NullLogger.Instance,
            store: store, changeSource: source, maxConcurrency: 1, options: new WatchOptions { MaxPendingFiles = 50 });
        service.StartWatching(Root); // thư mục rỗng → lượt quét khởi động không có gì

        var files = Enumerable.Range(0, 300).Select(i => Path.Combine(Root, $"f{i:000}.txt")).ToList();
        foreach (var f in files)
        {
            File.WriteAllText(f, "noi dung");
            source.Fire(f); // 50 vào hàng chờ, 250 tràn
        }

        Assert.Equal(1, service.DirtyRootCount);
        Assert.True(WaitFor(() => files.All(f => store.TryGetFileState(f) != null)),
            $"còn {files.Count(f => store.TryGetFileState(f) == null)} file chưa có bản ghi — tràn hàng chờ không được làm mất việc");
        Assert.True(WaitFor(() => service.DirtyRootCount == 0));
    }

    [Fact]
    public void Gian_nhip_lam_cham_xu_ly()
    {
        Directory.CreateDirectory(Root);
        using var store = NewStore(_work);
        var source = new FakeSource();
        using var service = new FileWatcherService(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), NullLogger.Instance,
            store: store, changeSource: source, maxConcurrency: 1,
            options: new WatchOptions { MinDelayBetweenFiles = TimeSpan.FromMilliseconds(100) });
        service.StartWatching(Root);

        var files = Enumerable.Range(0, 10).Select(i => Path.Combine(Root, $"g{i}.txt")).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var f in files)
        {
            File.WriteAllText(f, "x");
            source.Fire(f);
        }
        Assert.True(WaitFor(() => files.All(f => store.TryGetFileState(f) != null)));

        // 2 worker (MaxConcurrency + 1), mỗi worker nghỉ 100 ms sau mỗi file: 10 file ≈ 300 ms debounce + 4–5 lượt × 100 ms
        Assert.True(sw.ElapsedMilliseconds >= 650, $"chỉ mất {sw.ElapsedMilliseconds} ms — giãn nhịp không có tác dụng");
    }
}
