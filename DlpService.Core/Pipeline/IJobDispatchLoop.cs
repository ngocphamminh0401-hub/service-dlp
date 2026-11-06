// [Thay đổi] File mới (kế hoạch tách lõi, bước 5): điểm nối giữa Worker (lõi) và các vòng điều phối chạy nền do host cung cấp (hiện chỉ có LLM).
// Lõi chỉ biết interface này: có host đăng ký thì Worker chạy nó cạnh vòng chính, không thì bỏ qua. Nhờ vậy lõi không phụ thuộc gRPC/LLM.
using DlpService.Scanning;
using DlpService.Storage;

namespace DlpService.Pipeline;

/// <summary>Vòng việc chạy nền do host cung cấp, chạy song song với vòng chính của Worker cho tới khi service dừng.</summary>
public interface IJobDispatchLoop
{
    /// <summary>Chạy vòng lặp. Trả về khi <paramref name="stoppingToken"/> bị huỷ; lỗi trong vòng không được làm dừng cả service.</summary>
    Task RunAsync(ResultStore store, RegexScanner scanner, CancellationToken stoppingToken);
}
