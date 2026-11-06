// [Thay đổi] File mới (kế hoạch tách lõi, bước 5): vòng điều phối job LLM, chuyển NGUYÊN VĂN từ Worker.RunLlmDispatchLoopAsync sang thư mục Llm/.
// Worker (lõi) không còn biết gRPC, Named Pipe, kiểm tra CPU/pin — nó chỉ gọi IJobDispatchLoop nếu host đăng ký. Bản Linux (không LLM) không đăng ký gì.
using DlpService.Pipeline;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage;

namespace DlpService.Llm;

/// <summary>Gửi job LLM trong hàng đợi đi thẩm định (10 giây/lần), chỉ chạy trên host có LLM (Windows).</summary>
public sealed class LlmDispatchLoop(ILogger<LlmDispatchLoop> logger, IConfiguration configuration) : IJobDispatchLoop
{
    // [Thay đổi] mục 4.12 - Worker điều phối job: dựng GuardedInferenceClient + LlmJobDispatcher 1 lần, lặp mỗi 10
    // giây. Luôn "await" TryDispatchOnceAsync trước khi lặp lại → tự nhiên chỉ có 1 job chạy đồng thời (mục 4.12:
    // "1 job đồng thời"), không cần khoá/semaphore riêng.
    public async Task RunAsync(ResultStore store, RegexScanner scanner, CancellationToken stoppingToken)
    {
        string pipeName = configuration["Llm:PipeName"] ?? "dlp-llm";
        // [Thay đổi] mục 5.5: đường dẫn exe LLM host thật (vd DlpLlmHost.exe) để client tự kiểm tra sau khi kết
        // nối pipe — chưa cấu hình (chưa triển khai DlpLlmHost thật thay DlpFakeLlm) thì bỏ qua kiểm tra, KHÔNG ép
        // buộc, tránh chặn luồng dev/test hiện tại.
        string? serverExePath = configuration["Llm:ServerExePath"];
        var trustPolicy = serverExePath != null
            ? new PipeServerTrustPolicy(serverExePath, configuration.GetValue("Llm:RequireServerSignature", false))
            : null;
        var dispatcherOptions = new LlmJobDispatcherOptions
        {
            TokenBudget = Math.Max(200, configuration.GetValue("Llm:TokenBudget", 2000)),
            JobDeadline = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Llm:JobDeadlineSeconds", 60)))
        };
        var idleOptions = new MachineIdleOptions
        {
            MaxCpuPercent = configuration.GetValue("Llm:MaxCpuPercent", 50.0),
            MinBatteryPercent = configuration.GetValue("Llm:MinBatteryPercent", 20.0)
        };

        using var inferenceClient = InferenceClient.ConnectNamedPipe(pipeName, trustPolicy);
        var breaker = new CircuitBreaker();
        var guardedClient = new GuardedInferenceClient(inferenceClient, breaker);
        var idleChecker = new MachineIdleChecker(idleOptions);
        var fileReader = new FileReaderService();
        var dispatcher = new LlmJobDispatcher(store, fileReader, scanner, guardedClient, idleChecker, dispatcherOptions, logger);

        logger.LogInformation("[LLM] Worker điều phối job: pipe={pipe}, ngân sách {budget} token, deadline {deadline}s, CPU ≤ {cpu}%, pin ≥ {battery}%",
            pipeName, dispatcherOptions.TokenBudget, dispatcherOptions.JobDeadline.TotalSeconds, idleOptions.MaxCpuPercent, idleOptions.MinBatteryPercent);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string outcome = await dispatcher.TryDispatchOnceAsync(stoppingToken);
                if (outcome != "no_job" && !outcome.StartsWith("not_idle", StringComparison.Ordinal) && outcome != "circuit_open")
                {
                    logger.LogInformation("[LLM] {outcome}", outcome);
                }
                else if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("[LLM] {outcome}", outcome);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // [Thay đổi] Nguyên tắc P3: lỗi ở vòng điều phối LLM không được làm dừng cả service
                logger.LogWarning(ex, "[LLM] Lỗi vòng điều phối job, thử lại ở lượt sau");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
