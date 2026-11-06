// [Thay đổi] File mới (giới hạn số file xử lý cùng lúc — Watch:MaxConcurrency): thả cùng lúc nhiều file rồi dựng lại khoảng thời gian
// xử lý của từng file từ dòng log [Perf] (thời điểm ghi log − "tổng X ms"). Khoảng dựng lại luôn NẰM TRONG khoảng thật (bắt đầu muộn
// hơn, kết thúc sớm hơn chút ít) → nếu số khoảng chồng nhau vượt giới hạn thì chắc chắn giới hạn không có hiệu lực.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DlpService.Classification;
using DlpService.Scanning;
using DlpService.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Tests.Watching;

public sealed partial class FileWatcherConcurrencyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-watch-conc-" + Guid.NewGuid().ToString("N"));

    public FileWatcherConcurrencyTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Ghi lại dòng [Perf] kèm thời điểm ghi (ms, đồng hồ Stopwatch chung)
    private sealed class PerfLogger : ILogger
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public ConcurrentQueue<(double AtMs, string Line)> Perf { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string line = formatter(state, exception);
            if (line.StartsWith("[Perf]", StringComparison.Ordinal))
            {
                Perf.Enqueue((_clock.Elapsed.TotalMilliseconds, line));
            }
        }
    }

    [GeneratedRegex(@"tổng ([\d.,]+) ms")]
    private static partial Regex TotalMs();

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ThaNhieuFileCungLuc_KhongVuotGioiHan(int max)
    {
        const int files = 12;
        var logger = new PerfLogger();
        using var service = new FileWatcherService(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), logger,
            maxConcurrency: max);
        service.StartWatching(_folder);

        // Nội dung đủ dài để mỗi file tốn chút thời gian quét → các lần xử lý có cơ hội chồng nhau nếu không bị giới hạn
        string content = string.Concat(Enumerable.Repeat("Troi hom nay dep, khong co du lieu nhay cam. ", 20_000));
        for (int i = 0; i < files; i++)
        {
            File.WriteAllText(Path.Combine(_folder, $"f{i:00}.txt"), content);
        }

        for (int i = 0; i < 300 && logger.Perf.Count < files; i++)
        {
            Thread.Sleep(100);
        }
        Assert.Equal(files, logger.Perf.Count);

        // "tổng X ms" được làm tròn 0,1 ms → tổng thật có thể nhỏ hơn số in ra tới 0,05 ms. Cộng bù 0,06 ms vào điểm bắt đầu để khoảng
        // dựng lại luôn nằm TRONG khoảng thật. Không bù thì file kế tiếp (bắt đầu vài µs sau khi file trước nhả chỗ) bị tính là "chồng"
        // 0,002–0,016 ms — đã đo khi viết test: lỗi đo, không phải lỗi giới hạn.
        var intervals = logger.Perf.Select(p =>
        {
            double total = double.Parse(TotalMs().Match(p.Line).Groups[1].Value, CultureInfo.CurrentCulture);
            return (Start: p.AtMs - total + 0.06, End: p.AtMs);
        }).ToList();

        // Số khoảng đang mở cao nhất: duyệt các mốc theo thời gian (kết thúc trước bắt đầu nếu trùng mốc)
        int open = 0, peak = 0;
        foreach (var (_, delta) in intervals.SelectMany(x => new[] { (x.Start, 1), (x.End, -1) }).OrderBy(e => e.Item1).ThenBy(e => e.Item2))
        {
            open += delta;
            peak = Math.Max(peak, open);
        }
        Assert.True(peak <= max, $"{peak} file xử lý cùng lúc, vượt giới hạn {max}");
    }

    [Fact]
    public void CauHinhSai_VanChayDuoc1File()
    {
        using var service = new FileWatcherService(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), NullLogger.Instance,
            maxConcurrency: 0);
        Assert.Equal(1, service.MaxConcurrency);
    }
}
