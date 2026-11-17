// [Thay đổi] File mới (M3): histogram độ trễ bộ nhớ cố định thay cho hàng đợi không giới hạn — kiểm độ chính xác phân vị (≤ 10 %), xoá sau mỗi chu
// kỳ, an toàn đa luồng, và FileWatcherService KHÔNG thu mẫu khi metric tắt.
using DlpService.Classification;
using DlpService.Scanning;
using DlpService.Watching;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Tests.Watching;

public class LatencyHistogramTests
{
    [Fact]
    public void Rong_thi_tra_0()
    {
        Assert.Equal(new LatencyHistogram.Snapshot(0, 0, 0, 0), new LatencyHistogram().SnapshotAndReset());
    }

    [Fact]
    public void Phan_vi_sai_so_khong_qua_10_phan_tram()
    {
        var h = new LatencyHistogram();
        for (int ms = 1; ms <= 1000; ms++) h.Record(ms);

        var s = h.SnapshotAndReset();

        Assert.Equal(1000, s.Count);
        Assert.InRange(s.P50, 500, 550);  // cận trên của ô: ≥ giá trị thật, ≤ +10 %
        Assert.InRange(s.P95, 950, 1000); // không vượt max thật
        Assert.Equal(1000, s.Max);
    }

    [Fact]
    public void Lay_so_lieu_xong_thi_xoa_ve_0()
    {
        var h = new LatencyHistogram();
        h.Record(42);
        h.SnapshotAndReset();

        Assert.Equal(0, h.SnapshotAndReset().Count);
    }

    [Fact]
    public void Gia_tri_bat_thuong_khong_nem_loi()
    {
        var h = new LatencyHistogram();
        h.Record(-5);
        h.Record(double.NaN);
        h.Record(0.3);
        h.Record(1e12); // quá thang đo → ô cuối

        var s = h.SnapshotAndReset();

        Assert.Equal(4, s.Count);
        Assert.Equal(1e12, s.Max);
    }

    [Fact]
    public void Nhieu_luong_cung_ghi_khong_mat_mau()
    {
        var h = new LatencyHistogram();

        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 100_000; i++) h.Record(i % 500);
        });

        var s = h.SnapshotAndReset();
        Assert.Equal(800_000, s.Count);
        Assert.Equal(499, s.Max);
    }

    [Fact]
    public void Metric_tat_thi_watcher_khong_thu_mau()
    {
        using var off = new FileWatcherService(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), NullLogger.Instance,
            options: new WatchOptions());
        using var on = new FileWatcherService(new RegexScanner([]), new ClassificationEngine(NullLogger.Instance), NullLogger.Instance,
            options: new WatchOptions { MetricsInterval = TimeSpan.FromSeconds(60) });

        Assert.Null(off.Latency);
        Assert.NotNull(on.Latency);
    }
}
