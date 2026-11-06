// [Thay đổi] File mới (mục 6.2/6.3 - LLM host thật): 2 chế độ.
//   dotnet run -- genmanifest --model-dir <path> [--model-ver X]
//     → quét thư mục model, băm SHA-256 từng file, ghi manifest.json. Chạy 1 LẦN khi đóng gói/thiết lập model,
//       KHÔNG chạy tự động lúc "serve".
//   dotnet run -- serve [--pipe TÊN] [--model-dir <path>] [--idle-unload-minutes N]
//     → kiểm SHA-256 so với manifest.json (mục 8 "Model bị thay" — sai thì DỪNG NGAY, không phục vụ), rồi đứng
//       chờ gRPC qua Named Pipe. Model INT4 thật CHƯA nạp vào RAM lúc này — chỉ nạp lười ở lượt Classify đầu tiên
//       (mục 6.3), có vòng lặp nền tự unload khi rảnh.
using DlpLlmHost;
using Microsoft.Extensions.Hosting.WindowsServices;

// [Thay đổi] Điều tra timeout 30s khi SCM khởi động service (Event 7009): đo thời gian 3 mốc khởi động.
// Mốc 1 ghi ngay ở đây — đầu chương trình, trước mọi việc nặng.
var startupClock = System.Diagnostics.Stopwatch.StartNew();
LogStartupMark("1. process bat dau", startupClock);

const string DefaultPipeName = "dlp-llm-host";
const string DefaultModelDir = @"D:\Service-DLP\models\phi-4-mini-instruct-onnx\cpu_and_mobile\cpu-int4-rtn-block-32-acc-level-4";

if (args.Length > 0 && args[0] == "genmanifest")
{
    string modelDir = ArgValue(args, "--model-dir") ?? DefaultModelDir;
    string modelVer = ArgValue(args, "--model-ver") ?? "phi-4-mini-instruct-onnx-cpu-int4-rtn-block32-acc4";
    var manifest = ModelManifestChecker.Generate(modelDir, modelVer);
    ModelManifestChecker.Save(modelDir, manifest);
    Console.WriteLine($"[llm-host] Đã ghi manifest.json ({manifest.Files.Count} file) vào {modelDir}");
    return 0;
}

if (args.Length > 0 && args[0] == "serve")
{
    string pipeName = ArgValue(args, "--pipe") ?? DefaultPipeName;
    string modelDir = ArgValue(args, "--model-dir") ?? DefaultModelDir;
    int idleMinutes = int.TryParse(ArgValue(args, "--idle-unload-minutes"), out var m) ? m : 10;
    // [Thay đổi] mục 6.2 ước lượng RAM 2-3GB dựa trên thiết kế CŨ (Generator mới mỗi lần gọi, không giữ KV-cache
    // sẵn); từ khi có prefix cache (mục 6.4, Generator dùng chung cấp phát SẴN buffer cho max_length tối đa), RAM
    // thực tế cao hơn hẳn — đo thật: 6144MB KHÔNG đủ (OnnxRuntimeGenAIException "bad allocation"), 10240MB chạy ổn
    // (xem docs/buoi-09-tom-tat.md). Mặc định 10240MB, có chừa dư so với mức đã kiểm chứng.
    // [Thay đổi] Buổi 11: nâng mặc định lên 12288MB. Log chẩn đoán + thí nghiệm: với trần 10240MB, lượt sinh ĐẦU TIÊN sau khi
    // nạp model báo "bad allocation" ở 4/4 lần (private đỉnh đo được ~9,8GB, sát trần); nâng trần lên 14000MB thì job đầu chạy Ok.
    // Đây là trần "commit" (bộ nhớ ảo cam kết), không phải RAM vật lý: máy 13,7GB RAM + pagefile 14GB, working set thực tế 4,4-7GB.
    int memoryLimitMb = int.TryParse(ArgValue(args, "--memory-limit-mb"), out var mem) ? mem : 12288;
    int cpuRatePercent = int.TryParse(ArgValue(args, "--cpu-rate-percent"), out var cpu) ? cpu : 50;
    // mục 5.5: tài khoản được phép kết nối pipe. Chưa có tài khoản dịch vụ thật (NT SERVICE\RegexSvc — cần bật
    // "sc sidtype RegexSvc unrestricted" khi cài đặt thật, mục 6.1) -> mặc định CHỈ chính user hiện tại (đủ để chặn
    // process khác trên máy multi-user, dù chưa đạt mức cô lập của tài khoản dịch vụ riêng).
    string? allowedAccount = ArgValue(args, "--allowed-account");
    // [Thay đổi] Khoảng nghỉ giữa các lần thử lại khi ĐỌC file model lỗi tạm thời lúc băm nền (mặc định 30s; chỉnh nhỏ để test)
    int verifyRetrySeconds = int.TryParse(ArgValue(args, "--verify-retry-seconds"), out var vr) ? Math.Max(0, vr) : 30;
    return await RunServer(pipeName, modelDir, TimeSpan.FromMinutes(idleMinutes), memoryLimitMb, cpuRatePercent, allowedAccount, startupClock, verifyRetrySeconds);
}

Console.WriteLine("Dùng: dotnet run -- genmanifest --model-dir <path> [--model-ver X]");
Console.WriteLine("  hoặc: dotnet run -- serve [--pipe TÊN] [--model-dir <path>] [--idle-unload-minutes N] [--memory-limit-mb N] [--cpu-rate-percent N] [--allowed-account \"DOMAIN\\Account\"]");
return 1;

static string? ArgValue(string[] args, string flag) =>
    args.SkipWhile(a => a != flag).Skip(1).FirstOrDefault();

// [Thay đổi] Hàm phụ ghi 1 dòng "giờ thật | tên mốc | ms kể từ mốc 1" vào file. Dùng file thay vì Console vì
// khi chạy dưới SCM không có console. Lỗi ghi log (thiếu quyền, đĩa đầy...) bị nuốt — log không được làm sập service.
static void LogStartupMark(string markName, System.Diagnostics.Stopwatch clock)
{
    try
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DlpLlmHost");
        Directory.CreateDirectory(dir);
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {markName} | +{clock.ElapsedMilliseconds}ms{Environment.NewLine}";
        File.AppendAllText(Path.Combine(dir, "startup-timing.log"), line);
    }
    catch
    {
        // cố ý bỏ qua
    }
}

static async Task<int> RunServer(string pipeName, string modelDir, TimeSpan idleUnloadAfter, int memoryLimitMb, int cpuRatePercent, string? allowedAccount, System.Diagnostics.Stopwatch startupClock, int verifyRetrySeconds)
{
    // [Thay đổi] mục 6.1/6.3/8: áp giới hạn RAM/CPU + priority thấp cho CHÍNH tiến trình này SỚM NHẤT có thể
    // (trước khi nạp model, vốn là phần tốn RAM/CPU nhất) — best-effort, không chặn service nếu môi trường không hỗ trợ.
    if (OperatingSystem.IsWindows())
    {
        ResourceLimiter.ApplyLimitsBestEffort(memoryLimitMb * 1024L * 1024L, cpuRatePercent);
    }

    // [Thay đổi] Lỗi SCM kill sau 30s (Event 7009): trước đây băm SHA-256 cả 4,6 GB model NGAY ĐÂY, trước RunAsync —
    // boot nguội mất hơn 29s nên SCM huỷ service. Giờ chỉ đọc manifest.json (nhẹ) ở đây; việc băm chạy NỀN sau khi
    // service đã báo RUNNING (xem VerifyInBackgroundAsync). "Cổng toàn vẹn" bên dưới đảm bảo model KHÔNG được nạp
    // trước khi hash khớp — mục 8 "Model bị thay" vẫn giữ nguyên, chỉ đổi thời điểm kiểm.
    var manifest = ModelManifestChecker.Load(modelDir);
    var integrityGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    // [Thay đổi] mục 6.4: Generator dùng chung (prefix cache) cấp phát SẴN bộ nhớ KV-cache cho max_length tối đa —
    // khác với trước (mỗi lần tạo Generator mới chỉ cần đúng độ dài thực tế). Test thật cho thấy max_length lớn
    // (prefix ~1300 + suffix 3000 + 100) làm cấp phát thất bại (OnnxRuntimeGenAIException) do vượt RAM cho phép —
    // dùng cận DƯỚI ngân sách mục 4.10 (1500) thay vì cận trên (3000) cho maxSuffixTokens để giữ buffer vừa phải.
    var modelHost = new ModelHost(modelDir, manifest.ModelVer, idleUnloadAfter,
        PromptBuilder.Prefix, ClassifyOutputSchema.JsonSchemaStrict, maxSuffixTokens: 1500, maxNewTokens: 100,
        integrityGate: integrityGate.Task, fallbackJsonSchema: ClassifyOutputSchema.JsonSchema,
        preferredJsonSchema: ClassifyOutputSchema.JsonSchemaStrictCompact); // [Thay đổi] thử: chặt+compact → chặt → cũ

    var builder = WebApplication.CreateBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    // [Thay đổi] mục 14 "Đăng ký dịch vụ": khi SCM khởi động tiến trình này (không phải console tương tác), báo
    // Generic Host biết để xử lý đúng start/stop event + ghi Event Log. Khi chạy tay qua "dotnet run"/console như
    // suốt buổi nay, đây là NO-OP (tự phát hiện không có SCM, chạy như console app bình thường) — an toàn để luôn bật.
    if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
    {
        builder.Host.UseWindowsService();
    }
    // [Thay đổi] mục 5.5 "Bảo mật kênh": PipeSecurity chỉ cho phép SID của tài khoản được chỉ định (mặc định: user
    // hiện tại) kết nối — process khác (kể cả cùng máy, khác tài khoản) bị từ chối ở tầng hệ điều hành, không tới
    // được tầng gRPC. "FirstPipeInstance" (chống pipe squatting) là hành vi MẶC ĐỊNH của Kestrel Named Pipes
    // transport khi tạo instance đầu tiên — đã tự kiểm chứng qua thực tế: chạy 2 server trùng tên pipe trong buổi
    // này luôn báo UnauthorizedAccessException (đặc trưng của FILE_FLAG_FIRST_PIPE_INSTANCE bị từ chối), không
    // phải lỗi "hết instance" thông thường — không cần thêm code riêng cho việc này.
    if (OperatingSystem.IsWindows())
    {
        var pipeSecurity = PipeAcl.BuildPipeSecurity(allowedAccount);
        builder.WebHost.UseNamedPipes(namedPipeOptions =>
        {
            namedPipeOptions.PipeSecurity = pipeSecurity;
            // Kestrel mặc định dùng PipeOptions.CurrentUserOnly, xung đột với PipeSecurity tuỳ chỉnh — tự viết factory
            namedPipeOptions.CreateNamedPipeServerStream = context => PipeAcl.CreateSecuredServerStream(context, pipeSecurity);
        });
    }
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenNamedPipe(pipeName, listenOptions =>
        {
            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
        });
    });
    builder.Services.AddGrpc();
    builder.Services.AddSingleton(modelHost);
    builder.Services.AddSingleton<JobCancellationRegistry>();

    var app = builder.Build();
    app.MapGrpcService<InferenceHostService>();

    // Vòng lặp tự unload khi rảnh (mục 6.3) chạy song song với server, tự dừng khi app dừng
    _ = modelHost.RunIdleUnloadLoopAsync(app.Lifetime.ApplicationStopping);

    // [Thay đổi] Bắt đầu băm NỀN ngay khi host đã khởi động xong (pipe đã nghe, SCM đã thấy RUNNING).
    app.Lifetime.ApplicationStarted.Register(() =>
        _ = Task.Run(() => VerifyInBackgroundAsync(app, modelDir, manifest, integrityGate, verifyRetrySeconds, startupClock)));

    Console.WriteLine($"[llm-host] Đang chạy, Named Pipe: {pipeName} (ACL: {allowedAccount ?? "user hiện tại"}), model_dir={modelDir}, idle_unload={idleUnloadAfter}");
    // [Thay đổi] Mốc 3: ngay trước RunAsync — từ đây host mới kết nối SCM và báo RUNNING.
    LogStartupMark("3. sap RunAsync", startupClock);
    await app.RunAsync();
    // [Thay đổi] Hash sai → VerifyInBackgroundAsync đặt ExitCode = 1 rồi dừng app; trả đúng mã đó ra ngoài để SCM
    // coi là lỗi (áp recovery). Chạy bình thường thì ExitCode vẫn 0.
    return Environment.ExitCode;
}

// [Thay đổi] Kiểm SHA-256 model chạy NỀN sau khi service đã báo RUNNING.
//   - Khớp hết: mở cổng toàn vẹn → ModelHost mới được phép nạp model (job đang đợi chạy tiếp).
//   - Không khớp / thiếu file (model bị thay hoặc hỏng — lỗi THẬT): KHÔNG BAO GIỜ mở cổng; job đang đợi/đến sau nhận
//     MODEL_ERROR; ghi log (Event Log + console) rồi dừng service với mã lỗi 1 → SCM áp recovery (restart 60s/60s/300s).
//   - Lỗi ĐỌC tạm thời (file bị khoá, đĩa chập chờn): thử lại tối đa 3 lần, cách nhau retrySeconds, rồi mới coi là lỗi thật.
//   - Service đang dừng giữa chừng: dừng băm ngay (không treo SCM), huỷ cổng.
// Chỉ log tên file + hash để đối chiếu, không log nội dung file model.
static async Task VerifyInBackgroundAsync(WebApplication app, string modelDir, ModelManifest manifest,
    TaskCompletionSource gate, int retrySeconds, System.Diagnostics.Stopwatch startupClock)
{
    const int MaxReadAttempts = 3;
    var stopping = app.Lifetime.ApplicationStopping;
    string failure;
    try
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var errors = ModelManifestChecker.Verify(modelDir, manifest, stopping);
                LogStartupMark($"2. verify xong ({manifest.Files.Count} file, loi={errors.Count})", startupClock);
                if (errors.Count == 0)
                {
                    Console.WriteLine($"[llm-host] SHA-256 khớp manifest.json ({manifest.Files.Count} file), model_ver={manifest.ModelVer}");
                    gate.TrySetResult();
                    return;
                }
                failure = "SHA-256 không khớp manifest.json: " + string.Join(" | ", errors);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogStartupMark($"2. verify loi doc file lan {attempt}/{MaxReadAttempts}: {ex.GetType().Name}", startupClock);
                if (attempt >= MaxReadAttempts)
                {
                    failure = $"Không đọc được file model sau {MaxReadAttempts} lần thử: {ex.Message}";
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), stopping);
            }
        }
    }
    catch (OperationCanceledException)
    {
        gate.TrySetCanceled(stopping); // service đang dừng — không phải lỗi
        return;
    }

    Console.Error.WriteLine("[llm-host] KIỂM TRA TOÀN VẸN MODEL THẤT BẠI, không nạp model, dừng service: " + failure);
    app.Logger.LogCritical("Kiểm tra toàn vẹn model thất bại, dừng service: {failure}", failure); // vào Event Log khi chạy dưới SCM
    gate.TrySetException(new InvalidDataException(failure));
    Environment.ExitCode = 1;
    app.Lifetime.StopApplication();
}
