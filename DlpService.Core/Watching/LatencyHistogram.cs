// [Thay đổi] File mới (M3 — hàng đợi mẫu độ trễ không giới hạn): thay ConcurrentQueue<double> _latencyMs của FileWatcherService. Hàng đợi cũ giữ
// MỌI mẫu cho tới lần ghi metric — mà metric mặc định TẮT (Windows) nên không bao giờ được lấy ra: ~32 byte/sự kiện, phình suốt vòng đời service.
// Histogram thang log: chỉ ĐẾM số mẫu rơi vào từng ô, mỗi ô rộng hơn ô trước 10 % (1 ms → ~1.000.000 ms ≈ 150 ô) → bộ nhớ cố định ~1,2 KB dù có
// bao nhiêu mẫu; phân vị lấy CẬN TRÊN của ô → sai số ≤ 10 %, đủ cho log vận hành (p50/p95). Ghi không khoá (Interlocked) — gọi từ nhiều luồng.
namespace DlpService.Watching;

/// <summary>Đếm phân bố độ trễ (ms) với bộ nhớ cố định, an toàn đa luồng.</summary>
public sealed class LatencyHistogram
{
    private const double Growth = 1.10;      // mỗi ô rộng hơn ô trước 10 %
    private const double MaxTrackedMs = 1e6; // ~16,7 phút; lớn hơn dồn vào ô cuối
    private static readonly double LogGrowth = Math.Log(Growth);
    private static readonly int BucketCount = (int)Math.Ceiling(Math.Log(MaxTrackedMs) / LogGrowth) + 2; // ô 0: < 1 ms

    private readonly long[] _buckets = new long[BucketCount];
    private long _maxBits = BitConverter.DoubleToInt64Bits(0); // max dạng bit của double để dùng Interlocked

    /// <summary>Kết quả 1 chu kỳ đo.</summary>
    public readonly record struct Snapshot(long Count, double P50, double P95, double Max);

    /// <summary>Ghi 1 mẫu (ms). Âm/NaN → coi là 0; quá lớn → ô cuối.</summary>
    public void Record(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) ms = 0;
        Interlocked.Increment(ref _buckets[IndexOf(ms)]);

        // max: vòng CompareExchange — chỉ ghi khi giá trị mới lớn hơn, luồng khác chen vào thì thử lại
        long current = Interlocked.Read(ref _maxBits);
        while (ms > BitConverter.Int64BitsToDouble(current))
        {
            long seen = Interlocked.CompareExchange(ref _maxBits, BitConverter.DoubleToInt64Bits(ms), current);
            if (seen == current) break;
            current = seen;
        }
    }

    /// <summary>Lấy số liệu rồi xoá về 0 (cho chu kỳ metric kế tiếp). Mẫu ghi đồng thời lúc đang lấy có thể rơi sang chu kỳ sau — chấp nhận được.</summary>
    public Snapshot SnapshotAndReset()
    {
        var counts = new long[BucketCount];
        long total = 0;
        for (int i = 0; i < BucketCount; i++)
        {
            counts[i] = Interlocked.Exchange(ref _buckets[i], 0);
            total += counts[i];
        }
        double max = BitConverter.Int64BitsToDouble(Interlocked.Exchange(ref _maxBits, BitConverter.DoubleToInt64Bits(0)));
        if (total == 0)
        {
            return new Snapshot(0, 0, 0, 0);
        }
        return new Snapshot(total, Math.Min(Quantile(counts, total, 0.50), max), Math.Min(Quantile(counts, total, 0.95), max), max);
    }

    // Ô chứa phân vị q → trả CẬN TRÊN của ô đó (không vượt max thật — xử lý ở nơi gọi)
    private static double Quantile(long[] counts, long total, double q)
    {
        long rank = (long)Math.Ceiling(q * total), seen = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            seen += counts[i];
            if (seen >= rank) return UpperBound(i);
        }
        return UpperBound(counts.Length - 1);
    }

    // Ô 0: [0, 1) ms; ô i ≥ 1: [1,1^(i-1), 1,1^i) ms
    private static int IndexOf(double ms)
    {
        if (ms < 1) return 0;
        int i = (int)Math.Floor(Math.Log(ms) / LogGrowth) + 1;
        return Math.Min(i, BucketCount - 1);
    }

    private static double UpperBound(int index) => index == 0 ? 1 : Math.Pow(Growth, index);
}
