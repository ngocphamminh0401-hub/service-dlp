// [Thay đổi] File mới (sửa lỗi watcher bị GC thu hồi khi service rảnh — 22/10/2026). Tái hiện đúng điều kiện lỗi: tạo service, chờ
// rảnh, ép GC, rồi thả file. Test 1 xác nhận khi CÓ giữ tham chiếu (cách Worker làm sau khi sửa) thì file vẫn được xử lý; test 2
// ghi lại cơ chế gây lỗi (không giữ tham chiếu → GC thu hồi → file KHÔNG được xử lý) để không ai vô tình quay lại cách cũ.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DlpService.Classification;
using DlpService.Scanning;
using DlpService.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DlpService.Tests.Watching;

public sealed class FileWatcherServiceGcTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-watch-gc-" + Guid.NewGuid().ToString("N"));

    public FileWatcherServiceGcTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Logger ghi lại mọi dòng để kiểm tra file đã được xử lý ("[DLP] <path>: sạch")
    private sealed class CaptureLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception));
    }

    private static FileWatcherService NewService(ILogger logger) =>
        new(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), logger);

    private static void ForceFullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // Thả file rồi chờ tối đa 5 giây xem service có ghi dòng kết quả cho đúng file đó không
    private bool DropAndWaitProcessed(CaptureLogger logger, string name)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, "Troi hom nay dep, khong co du lieu nhay cam.");
        for (int i = 0; i < 50; i++)
        {
            if (logger.Lines.Any(l => l.Contains(path) && l.Contains("sạch")))
            {
                return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }

    [Fact]
    public void GiuThamChieu_SauKhiRanhVaGc_VanXuLyFileMoi()
    {
        var logger = new CaptureLogger();
        using var service = NewService(logger); // biến được dùng tới cuối hàm → giữ tham chiếu mạnh, giống Worker._fileWatcher
        service.StartWatching(_folder);

        Assert.True(DropAndWaitProcessed(logger, "truoc-gc.txt"));
        Thread.Sleep(500); // xử lý xong → không còn task nào giữ service
        ForceFullGc();
        Assert.True(DropAndWaitProcessed(logger, "sau-gc.txt"), "watcher phải còn hoạt động sau GC khi có tham chiếu mạnh");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartWithoutKeepingReference(string folder, ILogger logger) => NewService(logger).StartWatching(folder);

    [Fact]
    public void KhongGiuThamChieu_SauGc_KhongConXuLyFile_CoCheGayLoiCu()
    {
        var logger = new CaptureLogger();
        StartWithoutKeepingReference(_folder, logger); // cách Worker làm TRƯỚC khi sửa (biến cục bộ không dùng sau await)
        ForceFullGc();
        Assert.False(DropAndWaitProcessed(logger, "mat-watcher.txt"),
            "nếu test này FAIL nghĩa là .NET đã đổi cách giữ FileSystemWatcher — cơ chế lỗi cũ không còn, có thể xoá test");
    }

    [Fact]
    public void Dispose_NgungTheoDoi_KhongNemLoi()
    {
        var logger = new CaptureLogger();
        var service = NewService(logger);
        service.StartWatching(_folder);
        service.Dispose();
        service.Dispose(); // gọi 2 lần không lỗi
        Assert.False(DropAndWaitProcessed(logger, "sau-dispose.txt"));
    }
}
