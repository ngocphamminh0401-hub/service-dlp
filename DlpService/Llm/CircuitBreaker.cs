// [Thay đổi] File mới (mục 4.12 - Circuit breaker + backoff): máy trạng thái 3 pha (Closed/Open/HalfOpen), giống
// cầu dao điện tự ngắt — lỗi liên tiếp quá nhiều thì ngừng gọi LLM hẳn 1 thời gian, dò thử lại 1 lần sau khi hết giờ,
// lần ngắt sau chờ LÂU HƠN lần trước (backoff) nếu dò thử vẫn thất bại. An toàn khi nhiều luồng cùng gọi.
namespace DlpService.Llm;

/// <summary>3 trạng thái của circuit breaker.</summary>
public enum CircuitState
{
    /// <summary>Bình thường — vẫn cho gọi LLM.</summary>
    Closed,

    /// <summary>Đã lỗi liên tiếp quá ngưỡng — KHÔNG cho gọi, đợi hết thời gian chờ.</summary>
    Open,

    /// <summary>Hết thời gian chờ — cho thử gọi lại ĐÚNG 1 lần để xem đã ổn chưa.</summary>
    HalfOpen
}

/// <summary>Cấu hình circuit breaker.</summary>
public sealed class CircuitBreakerOptions
{
    /// <summary>Số lỗi LIÊN TIẾP (ở trạng thái Closed) để chuyển sang Open.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Thời gian ngắt lần ĐẦU TIÊN (mục 4.12 gợi ý ví dụ 2 phút).</summary>
    public TimeSpan BaseOpenDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Thời gian ngắt tối đa — backoff không tăng mãi vô hạn.</summary>
    public TimeSpan MaxOpenDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Hệ số nhân thời gian ngắt mỗi lần dò thử (HalfOpen) vẫn thất bại (backoff).</summary>
    public double BackoffMultiplier { get; set; } = 2.0;
}

/// <summary>Circuit breaker cho việc gọi LLM process. Không tự biết "gọi cái gì" — chỉ nhận báo cáo
/// RecordSuccess/RecordFailure sau mỗi lần gọi thật, và quyết định lần sau có NÊN gọi nữa hay không.</summary>
public sealed class CircuitBreaker(CircuitBreakerOptions? options = null)
{
    private readonly CircuitBreakerOptions _options = options ?? new CircuitBreakerOptions();
    private readonly Lock _lock = new();

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTime _openedAtUtc;
    private TimeSpan _currentOpenDuration;
    private bool _halfOpenTrialInFlight;

    /// <summary>Trạng thái hiện tại (tự chuyển Open → HalfOpen nếu đã hết thời gian chờ).</summary>
    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                RefreshState();
                return _state;
            }
        }
    }

    /// <summary>Có nên thử gọi LLM lúc này không (mục 4.12: "circuit breaker đang closed hoặc half-open"). Ở
    /// HalfOpen chỉ cho ĐÚNG 1 lượt gọi cùng lúc — gọi xong phải báo RecordSuccess/RecordFailure mới cho lượt kế.</summary>
    public bool CanAttempt()
    {
        lock (_lock)
        {
            RefreshState();
            switch (_state)
            {
                case CircuitState.Closed:
                    return true;
                case CircuitState.HalfOpen when !_halfOpenTrialInFlight:
                    _halfOpenTrialInFlight = true;
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>Báo lần gọi vừa rồi THÀNH CÔNG — về Closed, xoá hết lỗi đã đếm, thời gian ngắt lần sau (nếu có) quay
    /// về mức cơ bản (hết backoff).</summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _currentOpenDuration = _options.BaseOpenDuration;
            _halfOpenTrialInFlight = false;
            _state = CircuitState.Closed;
        }
    }

    /// <summary>Báo lần gọi vừa rồi THẤT BẠI (không kết nối được, hoặc LLM trả TIMEOUT/MODEL_ERROR).</summary>
    public void RecordFailure()
    {
        lock (_lock)
        {
            bool wasHalfOpenTrial = _halfOpenTrialInFlight;
            _halfOpenTrialInFlight = false;

            if (_state == CircuitState.HalfOpen || wasHalfOpenTrial)
            {
                // Dò thử lại vẫn lỗi -> ngắt tiếp, chờ LÂU HƠN lần trước (backoff) — LLM có vẻ hỏng thật, không dò dồn dập
                Open(isRetryFailure: true);
                return;
            }

            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.FailureThreshold)
            {
                Open(isRetryFailure: false);
            }
        }
    }

    private void Open(bool isRetryFailure)
    {
        _state = CircuitState.Open;
        _openedAtUtc = DateTime.UtcNow;
        _currentOpenDuration = isRetryFailure
            ? TimeSpan.FromTicks(Math.Min((long)(_currentOpenDuration.Ticks * _options.BackoffMultiplier), _options.MaxOpenDuration.Ticks))
            : _options.BaseOpenDuration;
    }

    // Chuyển Open -> HalfOpen khi đã hết thời gian chờ. Không dùng Timer riêng — chỉ tính toán khi có ai hỏi trạng
    // thái (CanAttempt/State), đơn giản và không tốn luồng nền.
    private void RefreshState()
    {
        if (_state == CircuitState.Open && DateTime.UtcNow >= _openedAtUtc + _currentOpenDuration)
        {
            _state = CircuitState.HalfOpen;
        }
    }
}
