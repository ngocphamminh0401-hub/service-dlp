// [Thay đổi] File mới (M2 — backlog không giới hạn): hàng chờ CÓ TRẦN cho các file có sự kiện, thay cho "mỗi path 1 Task + CancellationTokenSource
// + 1 chỗ chờ trên SemaphoreSlim" (đo trước M2: 50.000 sự kiện → 50.000 việc chờ, heap +42 MB, tăng tuyến tính theo số file).
//  - Gộp theo path: sự kiện mới cho path đang chờ chỉ DỜI HẠN (debounce), không thêm việc.
//  - Xếp theo HẠN (heap) chứ không FIFO: file bị ghi liên tục (log đang chạy) cứ dời hạn mãi — FIFO sẽ làm worker đứng chờ nó, chặn mọi file
//    khác đã đến hạn.
//  - Heap dùng "xoá lười": dời hạn chỉ đẩy thêm 1 mục; lúc lấy ra, mục có hạn khác với hạn trong Dictionary là mục CŨ → bỏ qua.
//  - Đầy → TryAdd trả Full NGAY (không chờ) → luồng đọc sự kiện không bao giờ bị nghẽn; nơi gọi đánh dấu thư mục "dirty" để quét bù.
//  - Gỡ khỏi hàng chờ TRƯỚC khi xử lý → sự kiện đến trong lúc đang xử lý sẽ thêm lại → được xử lý thêm 1 lần (đúng hành vi cũ).
namespace DlpService.Watching;

/// <summary>Kết quả thêm 1 path vào hàng chờ.</summary>
public enum EnqueueResult
{
    /// <summary>Thêm mới.</summary>
    Added,

    /// <summary>Path đã đang chờ → chỉ dời hạn (gộp sự kiện).</summary>
    Coalesced,

    /// <summary>Hàng chờ đầy → KHÔNG thêm; nơi gọi phải có cách bù (đánh dấu thư mục cần quét lại).</summary>
    Full
}

/// <summary>Hàng chờ file có trần, gộp theo path, lấy theo hạn sớm nhất. An toàn đa luồng.</summary>
public sealed class PendingFileQueue
{
    private readonly record struct Entry(long DueTicks, long FirstEventTicks);

    private readonly Dictionary<string, Entry> _entries;
    private readonly PriorityQueue<string, long> _byDue = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _signal = new(0); // báo "có việc mới" cho worker đang chờ
    private readonly long _delayTicks;
    private readonly Func<long> _now;

    /// <param name="capacity">Số path KHÁC NHAU tối đa đang chờ.</param>
    /// <param name="delay">Debounce: xử lý sau khi path im lặng đủ khoảng này.</param>
    /// <param name="comparer">So sánh path (theo hệ điều hành: phân biệt hoa/thường hay không).</param>
    /// <param name="now">Đồng hồ (Stopwatch ticks); null = Stopwatch.GetTimestamp — test truyền đồng hồ giả.</param>
    public PendingFileQueue(int capacity, TimeSpan delay, StringComparer comparer, Func<long>? now = null)
    {
        Capacity = Math.Max(1, capacity);
        _entries = new Dictionary<string, Entry>(comparer);
        _now = now ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _delayTicks = (long)(delay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>Trần số path đang chờ.</summary>
    public int Capacity { get; }

    /// <summary>Số path đang chờ.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Tuổi (ms) của việc chờ lâu nhất, tính từ sự kiện ĐẦU TIÊN của path đó; 0 nếu rỗng. Duyệt hết — chỉ gọi khi ghi metric.</summary>
    public double OldestAgeMs
    {
        get
        {
            lock (_lock)
            {
                if (_entries.Count == 0) return 0;
                long oldest = _entries.Values.Min(e => e.FirstEventTicks);
                return (_now() - oldest) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
        }
    }

    /// <summary>Thêm path (hoặc dời hạn nếu đang chờ).</summary>
    public EnqueueResult TryAdd(string path)
    {
        long now = _now();
        long due = now + _delayTicks;
        bool wasEmpty;
        lock (_lock)
        {
            if (_entries.TryGetValue(path, out var existing))
            {
                _entries[path] = existing with { DueTicks = due };
                _byDue.Enqueue(path, due); // mục cũ trong heap thành "cũ", bỏ qua lúc lấy ra
                return EnqueueResult.Coalesced;
            }
            if (_entries.Count >= Capacity)
            {
                return EnqueueResult.Full;
            }
            wasEmpty = _entries.Count == 0;
            _entries[path] = new Entry(due, now);
            _byDue.Enqueue(path, due);
        }
        // Chỉ đánh thức khi hàng chờ ĐANG RỖNG: còn việc thì worker đã hẹn thức đúng hạn sớm nhất, mà việc mới luôn có hạn ≥ hạn đó. Release mỗi lần
        // thêm thì burst 10.000 file đẩy bộ đếm semaphore lên 10.000 → worker thức 10.000 lần liên tiếp chỉ để thấy "chưa đến hạn".
        if (wasEmpty)
        {
            _signal.Release();
        }
        return EnqueueResult.Added;
    }

    /// <summary>Đưa path vào lại để xử lý NGAY khi đến hạn bình thường (VD đọc trúng lúc đang ghi) — giống TryAdd.</summary>
    public EnqueueResult Retry(string path) => TryAdd(path);

    /// <summary>Lấy 1 path đã đến hạn (sớm nhất trước), chờ nếu chưa có. Path được GỠ khỏi hàng chờ trước khi trả về.</summary>
    /// <returns>(path, thời điểm sự kiện đầu tiên — Stopwatch ticks).</returns>
    /// <exception cref="OperationCanceledException">token bị huỷ.</exception>
    public async Task<(string Path, long FirstEventTicks)> TakeDueAsync(CancellationToken token)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                long now = _now();
                while (_byDue.TryPeek(out var path, out long due))
                {
                    if (!_entries.TryGetValue(path, out var entry) || entry.DueTicks != due)
                    {
                        _byDue.Dequeue(); // mục cũ (đã dời hạn) hoặc path đã được lấy đi → bỏ
                        continue;
                    }
                    if (due <= now)
                    {
                        _byDue.Dequeue();
                        _entries.Remove(path);
                        return (path, entry.FirstEventTicks);
                    }
                    break;
                }
                // Chưa có việc đến hạn: chờ tới hạn gần nhất, hoặc tối đa 1 s nếu rỗng (vẫn thức dậy khi có việc mới nhờ _signal)
                wait = _byDue.TryPeek(out _, out long nextDue)
                    ? TimeSpan.FromSeconds(Math.Max(0, nextDue - now) / (double)System.Diagnostics.Stopwatch.Frequency)
                    : TimeSpan.FromSeconds(1);
            }
            await _signal.WaitAsync(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait, token);
        }
    }
}
