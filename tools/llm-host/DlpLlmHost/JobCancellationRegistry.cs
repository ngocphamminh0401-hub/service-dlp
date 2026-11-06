// [Thay đổi] File mới (mục 6.4 - "hỗ trợ Cancel, kiểm chứng runtime dừng vòng sinh sạch"): gRPC service của
// Kestrel tạo 1 instance InferenceHostService MỚI cho mỗi request (transient mặc định), nên Classify và Cancel
// KHÔNG chia sẻ state qua field instance được — phải dùng 1 singleton dùng chung để Cancel(job_id) tìm đúng
// CancellationTokenSource của lượt Classify đang chạy (mục 6.4: "một job đồng thời", nên tối đa 1 entry thật sự
// đang chạy tại 1 thời điểm, nhưng vẫn map theo job_id để Cancel gọi nhầm job không ảnh hưởng job khác).
using System.Collections.Concurrent;

namespace DlpLlmHost;

public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobs = new();

    public void Register(string jobId, CancellationTokenSource cts) => _jobs[jobId] = cts;

    public void Unregister(string jobId) => _jobs.TryRemove(jobId, out _);

    /// <summary>Huỷ job đang chạy nếu tìm thấy. Không báo lỗi nếu job_id không tồn tại (có thể đã xong, hoặc
    /// Regex service gọi Cancel trễ) — Cancel vốn chỉ là "cố gắng", không đảm bảo.</summary>
    public void TryCancel(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }
    }
}
