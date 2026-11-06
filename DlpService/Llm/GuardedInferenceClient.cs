// [Thay đổi] File mới (mục 4.12 - Circuit breaker + backoff): bọc InferenceClient bằng CircuitBreaker — Worker LLM
// (chưa xây) sẽ gọi qua lớp này thay vì gọi thẳng InferenceClient, để không phải tự nhớ cập nhật circuit breaker sau
// mỗi lần gọi. KHÔNG sửa InferenceClient.cs — lớp này chỉ bọc bên ngoài.
using Dlp.Inference;
using Status = Dlp.Inference.Status;

namespace DlpService.Llm;

/// <summary>InferenceClient có circuit breaker: tự bỏ qua việc gọi LLM khi mạch đang "ngắt" (Open), tự báo cáo
/// thành công/thất bại sau mỗi lần gọi thật.</summary>
public sealed class GuardedInferenceClient(InferenceClient inner, CircuitBreaker breaker) : IDisposable
{
    /// <summary>Trạng thái circuit breaker hiện tại — Worker dùng để quyết định "máy rảnh nhưng mạch đang ngắt thì
    /// khỏi thử" (mục 4.12: "Chỉ chạy khi... circuit breaker đang closed hoặc half-open").</summary>
    public CircuitState State => breaker.State;

    /// <summary>Như InferenceClient.TryGetInfoAsync, nhưng bỏ qua ngay (trả null) nếu mạch đang Open, và tự cập nhật
    /// circuit breaker theo kết quả — dùng làm "lượt dò thử" khi mạch đang HalfOpen (mục 4.12: "half-open thử GetInfo").</summary>
    public async Task<Info?> TryGetInfoAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!breaker.CanAttempt())
        {
            return null;
        }

        var info = await inner.TryGetInfoAsync(timeout, cancellationToken);
        if (info != null)
        {
            breaker.RecordSuccess();
        }
        else
        {
            breaker.RecordFailure();
        }
        return info;
    }

    /// <summary>Như InferenceClient.TryClassifyAsync, nhưng bỏ qua ngay (trả null) nếu mạch đang Open, và tự cập
    /// nhật circuit breaker: TIMEOUT/MODEL_ERROR hoặc mất kết nối tính là lỗi; còn lại (OK/BUSY/NOT_READY/
    /// BAD_CONTRACT) tính là "LLM vẫn sống, chỉ chưa làm được việc" — không làm mạch ngắt oan.</summary>
    public async Task<ClassifyRes?> TryClassifyAsync(ClassifyReq request, CancellationToken cancellationToken = default)
    {
        if (!breaker.CanAttempt())
        {
            return null;
        }

        var result = await inner.TryClassifyAsync(request, cancellationToken);
        // [Thay đổi] OUTPUT_TOO_LONG cố ý KHÔNG nằm trong danh sách lỗi: đó là lỗi riêng của MỘT file (LLM vẫn sống và trả lời
        // đúng), nếu tính vào breaker thì vài file xấu liên tiếp sẽ làm "ngắt mạch" và chặn luôn các file tốt.
        bool isFailure = result == null || result.Status == Status.Timeout || result.Status == Status.ModelError;
        if (isFailure)
        {
            breaker.RecordFailure();
        }
        else
        {
            breaker.RecordSuccess();
        }
        return result;
    }

    /// <summary>Cancel không tính vào circuit breaker — đây là dọn dẹp, không phải tín hiệu "LLM có sống không".</summary>
    public Task CancelAsync(string jobId, CancellationToken cancellationToken = default) =>
        inner.CancelAsync(jobId, cancellationToken);

    /// <inheritdoc/>
    public void Dispose() => inner.Dispose();
}
