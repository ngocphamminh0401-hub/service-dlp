// [Thay đổi] File mới (M2): hàng chờ có trần — gộp theo path, dời hạn, đầy thì Full, lấy theo hạn sớm nhất, an toàn đa luồng.
// Dùng ĐỒNG HỒ GIẢ (Stopwatch ticks do test điều khiển) để kiểm hạn mà không phụ thuộc tốc độ máy.
using System.Collections.Concurrent;
using System.Diagnostics;
using DlpService.Watching;

namespace DlpService.Tests.Watching;

public class PendingFileQueueTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
    private long _clock = 1_000_000;
    private long Ms(double ms) => (long)(ms / 1000 * Stopwatch.Frequency);
    private PendingFileQueue Queue(int capacity = 100) => new(capacity, Delay, StringComparer.Ordinal, () => Interlocked.Read(ref _clock));
    private void Advance(double ms) => Interlocked.Add(ref _clock, Ms(ms));

    // Lấy 1 việc nếu có ngay (không chờ): huỷ sau 50 ms thật → null
    private static string? TakeNow(PendingFileQueue q)
    {
        using var cts = new CancellationTokenSource(50);
        try
        {
            return q.TakeDueAsync(cts.Token).GetAwaiter().GetResult().Path;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public void Cung_path_nhieu_su_kien_chi_xu_ly_1_lan()
    {
        var q = Queue();
        Assert.Equal(EnqueueResult.Added, q.TryAdd("/a"));
        Assert.Equal(EnqueueResult.Coalesced, q.TryAdd("/a"));
        Assert.Equal(EnqueueResult.Coalesced, q.TryAdd("/a"));
        Assert.Equal(1, q.Count);

        Advance(301);
        Assert.Equal("/a", TakeNow(q));
        Assert.Null(TakeNow(q));
    }

    [Fact]
    public void Chua_den_han_thi_chua_lay_su_kien_moi_doi_han()
    {
        var q = Queue();
        q.TryAdd("/a");
        Advance(200);
        Assert.Null(TakeNow(q));          // mới 200 ms < 300 ms debounce
        q.TryAdd("/a");                   // sự kiện mới → hạn dời tới 200 + 300
        Advance(200);
        Assert.Null(TakeNow(q));          // 400 ms: hạn cũ (300) đã qua nhưng đã bị dời
        Advance(101);
        Assert.Equal("/a", TakeNow(q));
    }

    [Fact]
    public void Day_thi_tra_Full_khong_vuot_tran()
    {
        var q = Queue(capacity: 3);
        for (int i = 0; i < 3; i++) Assert.Equal(EnqueueResult.Added, q.TryAdd($"/f{i}"));

        Assert.Equal(EnqueueResult.Full, q.TryAdd("/moi"));
        Assert.Equal(EnqueueResult.Coalesced, q.TryAdd("/f1")); // path đang chờ vẫn gộp được khi đầy
        Assert.Equal(3, q.Count);
    }

    [Fact]
    public void File_bi_ghi_lien_tuc_khong_chan_file_khac()
    {
        var q = Queue();
        q.TryAdd("/log");      // vào trước
        q.TryAdd("/baocao");
        Advance(250);
        q.TryAdd("/log");      // log bị ghi tiếp → dời hạn
        Advance(100);           // 350 ms: /baocao đến hạn, /log chưa (hạn 550)

        Assert.Equal("/baocao", TakeNow(q)); // FIFO sẽ đứng chờ /log ở đầu hàng
        Assert.Null(TakeNow(q));
    }

    [Fact]
    public void Lay_ra_roi_thi_su_kien_moi_them_lai_duoc()
    {
        var q = Queue();
        q.TryAdd("/a");
        Advance(301);
        Assert.Equal("/a", TakeNow(q));

        Assert.Equal(EnqueueResult.Added, q.TryAdd("/a")); // sự kiện trong lúc đang xử lý → việc mới, xử lý thêm 1 lần
        Advance(301);
        Assert.Equal("/a", TakeNow(q));
    }

    [Fact]
    public void Tuoi_viec_cho_lau_nhat_tinh_tu_su_kien_dau()
    {
        var q = Queue();
        q.TryAdd("/a");
        Advance(1000);
        q.TryAdd("/a"); // dời hạn nhưng KHÔNG đổi thời điểm sự kiện đầu

        Assert.InRange(q.OldestAgeMs, 999, 1001);
    }

    [Fact]
    public async Task Nhieu_luong_them_va_lay_khong_mat_khong_trung()
    {
        // Đồng hồ THẬT, debounce 0: 4 luồng thêm 4 × 2.000 path khác nhau, 3 worker lấy song song
        var q = new PendingFileQueue(100_000, TimeSpan.Zero, StringComparer.Ordinal);
        var taken = new ConcurrentBag<string>();
        using var cts = new CancellationTokenSource();
        var workers = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (true) taken.Add((await q.TakeDueAsync(cts.Token)).Path);
            }
            catch (OperationCanceledException) { }
        })).ToArray();

        Parallel.For(0, 4, t =>
        {
            for (int i = 0; i < 2000; i++) q.TryAdd($"/t{t}/f{i}");
        });
        for (int i = 0; i < 200 && taken.Count < 8000; i++) await Task.Delay(25);
        cts.Cancel();
        await Task.WhenAll(workers);

        Assert.Equal(8000, taken.Count);
        Assert.Equal(8000, taken.Distinct().Count());
    }
}
