// [Thay đổi] File mới (mục 5 - Client phía Regex service): "đầu dây điện thoại" phía Regex service, nói chuyện với
// LLM process (thật hoặc giả) qua Named Pipe (mục 5.1) theo đúng hợp đồng contracts/inference.proto.
//
// Nguyên tắc P3 (mục 1.2): "LLM lỗi thì hệ thống thoái hóa về regex-only, không dừng" — MỌI hàm ở đây đều NUỐT lỗi
// kết nối (LLM process chưa chạy, crash, treo quá lâu...) và trả null thay vì ném exception lên trên. Nơi gọi chỉ
// cần kiểm tra null là biết phải dùng lại kết quả regex, không cần try/catch riêng.
using Dlp.Inference;
using Grpc.Core;
using Grpc.Net.Client;
using Status = Dlp.Inference.Status; // "Status" cũng có trong Grpc.Core (mã lỗi RPC) — chốt rõ dùng Status trong hợp đồng của mình

namespace DlpService.Llm;

/// <summary>Client gRPC gọi LLM process qua Named Pipe. Dùng using/Dispose để đóng kết nối khi xong việc.</summary>
public sealed class InferenceClient : IDisposable
{
    // Thời gian CHỜ THÊM ngoài deadline_ms của chính request — phòng trường hợp mạng/tuần tự hoá chậm, không tính
    // vào thời gian LLM được phép suy luận (deadline_ms đã gửi trong request cho LLM tự biết dừng đúng hạn).
    private static readonly TimeSpan DeadlineGrace = TimeSpan.FromSeconds(2);

    private readonly GrpcChannel _channel;
    private readonly Inference.InferenceClient _client;

    private InferenceClient(GrpcChannel channel)
    {
        _channel = channel;
        _client = new Inference.InferenceClient(channel);
    }

    /// <summary>Kết nối tới LLM process qua Named Pipe. Không kiểm tra pipe có ai lắng nghe hay không — lỗi kết nối
    /// chỉ lộ ra ở lần gọi RPC đầu tiên (TryGetInfoAsync/TryClassifyAsync), đúng như gRPC hoạt động (kết nối lười).</summary>
    /// <param name="pipeName">Tên Named Pipe của LLM process.</param>
    /// <param name="trustPolicy">[Thay đổi] mục 5.5: khác null thì sau MỖI lần kết nối, kiểm tra process phía server
    /// đúng là exe mong đợi (chống process giả mạo chiếm tên pipe) — không khớp thì NGẮT kết nối ngay, không dùng.
    /// Null = bỏ qua kiểm tra (dùng cho công cụ test/LLM giả — DlpFakeLlm — không có đường dẫn exe cố định để so).</param>
    public static InferenceClient ConnectNamedPipe(string pipeName, PipeServerTrustPolicy? trustPolicy = null)
    {
        var socketsHttpHandler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var clientStream = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.WriteThrough | System.IO.Pipes.PipeOptions.Asynchronous,
                    System.Security.Principal.TokenImpersonationLevel.Anonymous);
                try
                {
                    await clientStream.ConnectAsync(cancellationToken);

                    if (trustPolicy != null && OperatingSystem.IsWindows()
                        && !PipeServerVerifier.Verify(clientStream.SafePipeHandle, trustPolicy, out string reason))
                    {
                        throw new IOException($"Xác minh process phía server của Named Pipe '{pipeName}' thất bại: {reason}");
                    }

                    return clientStream;
                }
                catch
                {
                    clientStream.Dispose();
                    throw;
                }
            }
        };

        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = socketsHttpHandler });
        return new InferenceClient(channel);
    }

    /// <summary>Hỏi thăm LLM process (còn sống không, model gì, contract_ver mấy). Null nếu không kết nối được.</summary>
    /// <param name="timeout">Thời gian chờ tối đa.</param>
    public async Task<Info?> TryGetInfoAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _client.GetInfoAsync(new Empty(), deadline: DateTime.UtcNow.Add(timeout), cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // LLM process chưa bật (mục 6.3: delayed-auto-start, lazy-load) hoặc không phản hồi kịp — coi như chưa
            // sẵn sàng, KHÔNG phải lỗi nghiêm trọng
            return null;
        }
    }

    /// <summary>Nhờ LLM thẩm định 1 gói ngữ cảnh đã mask. Null nếu không kết nối được LLM process (khác với
    /// ClassifyRes.Status = TIMEOUT/BUSY/MODEL_ERROR — đó là LLM CÓ trả lời, chỉ là trả lời "không xong việc").</summary>
    public async Task<ClassifyRes?> TryClassifyAsync(ClassifyReq request, CancellationToken cancellationToken = default)
    {
        var clientDeadline = DateTime.UtcNow.AddMilliseconds(request.DeadlineMs).Add(DeadlineGrace);
        try
        {
            return await _client.ClassifyAsync(request, deadline: clientDeadline, cancellationToken: cancellationToken);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            // Quá hạn NGAY Ở PHÍA CLIENT (khác TIMEOUT do chính LLM tự báo) — LLM có thể đã treo, không trả lời gì cả
            return new ClassifyRes { JobId = request.JobId, Status = Status.Timeout };
        }
        catch (RpcException)
        {
            // Mất kết nối/LLM process crash giữa chừng — thoái hoá về regex-only (nguyên tắc P3)
            return null;
        }
    }

    /// <summary>Báo LLM process huỷ 1 job (file đã đổi hoặc service đang dừng). "Cố gắng tốt nhất" — lỗi thì bỏ qua,
    /// không quan trọng bằng việc không được làm treo/sập luồng gọi.</summary>
    public async Task CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.CancelAsync(new CancelReq { JobId = jobId }, cancellationToken: cancellationToken);
        }
        catch (RpcException)
        {
            // LLM process đã tắt hoặc không còn job đó — không cần báo lỗi, Cancel vốn chỉ là dọn dẹp
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _channel.Dispose();
}
