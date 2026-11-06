// [Thay đổi] Thêm using để dùng FileWatcherService (nay nằm ở namespace Watching)
using DlpService.Scanning; // [Thay đổi] để dùng RuleLoader và RegexScanner
using DlpService.Watching;
using DlpService.Classification;
using DlpService.Storage; // [Thay đổi] Ghi finding + scan_result
using DlpService.Llm; // [Thay đổi] mục 4.12 - Worker điều phối job LLM
using DlpService.Reading;
using DlpService.Reading.Native; // [Thay đổi] Tika native: cấu hình + vòng đời thư viện native

namespace DlpService;

/// <summary>Điểm khởi động luồng nền: nạp rule, tạo scanner và bắt đầu theo dõi thư mục watch-folder.</summary>
// [Thay đổi] Router ba vùng: nhận thêm IConfiguration (DI tự truyền) để đọc ngưỡng T_high/T_low từ appsettings.json
public class Worker(ILogger<Worker> logger, IConfiguration configuration) : BackgroundService
{
    // [Sửa lỗi] Watcher bị GC thu hồi khi service rảnh: trước đây FileWatcherService chỉ là BIẾN CỤC BỘ trong ExecuteAsync và không
    // được dùng sau "await Task.WhenAll" → bản Release không giữ nó trong state machine async; FileSystemWatcher của .NET chỉ tự giữ
    // mình bằng THAM CHIẾU YẾU khi chờ sự kiện → hết file đang xử lý + 1 lần GC là watcher bị thu hồi, service ngừng nhận file mới mà
    // KHÔNG báo lỗi (tái hiện 22/10: 10 file làm nóng xử lý xong rồi 44 file sau không được quét; stack: không luồng nào kẹt).
    // Giữ ở TRƯỜNG của Worker (hosted service, sống suốt vòng đời tiến trình) để luôn có tham chiếu mạnh.
    private FileWatcherService? _fileWatcher;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // [Thay đổi] Tika native: đọc cấu hình mục "TikaNative" TRƯỚC khi tạo FileReaderService đầu tiên (FileReaderService tự đăng ký
        // extractor bằng reflection và hỏi danh sách đuôi hỗ trợ ngay lúc được tạo). Mặc định Enabled = false → không có gì thay đổi.
        TikaNativeRuntime.Configure(configuration.GetSection("TikaNative").Get<TikaNativeOptions>() ?? new TikaNativeOptions(), logger);

        // [Thay đổi] Nạp rule từ thư mục rules/ nằm cạnh file .exe (không dùng đường dẫn tương đối,
        // vì khi chạy thành Windows Service thư mục làm việc không phải thư mục project)
        var ruleFolder = Path.Combine(AppContext.BaseDirectory, "rules");
        var patterns = new RuleLoader(logger).LoadFromFolder(ruleFolder);
        if (patterns.Count == 0)
        {
            logger.LogWarning("Không có rule nào được nạp, service sẽ không phát hiện được gì");
        }

        // [Thay đổi] Merger mục 7.4 "Lưu vết": tính rules_ver 1 lần lúc nạp rule, ghi kèm mọi scan_result sau này để
        // biết kết quả được tính bằng bộ rule nào (so sánh được khi rule thay đổi qua các lần deploy).
        var rulesVer = RuleLoader.ComputeRulesVersion(ruleFolder);

        // [Thay đổi] Tạo scanner từ danh sách rule, và engine tự nạp module domain, rồi truyền cả hai vào FileWatcherService
        // [Thay đổi] Công thức Go: ngưỡng MinConfidence đọc từ mục "Scan:MinConfidence" (thiếu → 0,60 như Go). Sai (ngoài
        // 0-1) thì dùng mặc định + cảnh báo, không chặn service khởi động — cùng cách xử lý với ngưỡng Router bên dưới.
        double minConfidence = configuration.GetValue("Scan:MinConfidence", CandidateScorer.DefaultMinConfidence);
        if (minConfidence is < 0 or > 1 || double.IsNaN(minConfidence))
        {
            logger.LogWarning("Scan:MinConfidence không hợp lệ ({value}), dùng mặc định {default:0.00}", minConfidence, CandidateScorer.DefaultMinConfidence);
            minConfidence = CandidateScorer.DefaultMinConfidence;
        }
        logger.LogInformation("Ngưỡng điểm chỗ khớp: MinConfidence={min:0.00}", minConfidence);
        var scanner = new RegexScanner(patterns, rulesVer, minConfidence);
        var engine = new ClassificationEngine(logger);
        // [Thay đổi] Router ba vùng: đọc mục "Score" { THigh, TLow }; thiếu hoặc sai (TLow ≥ THigh, ngoài 0-1) thì dùng
        // mặc định 0,90/0,30 + cảnh báo, không chặn service khởi động
        var routerOptions = configuration.GetSection("Score").Get<RouterOptions>() ?? new RouterOptions();
        if (!routerOptions.IsValid)
        {
            logger.LogWarning("Ngưỡng Score không hợp lệ (TLow={tLow}, THigh={tHigh}), dùng mặc định 0,30/0,90", routerOptions.TLow, routerOptions.THigh);
            routerOptions = new RouterOptions();
        }
        logger.LogInformation("Router ba vùng: T_low={tLow:0.00}, T_high={tHigh:0.00}", routerOptions.TLow, routerOptions.THigh);

        // [Thay đổi] Ghi finding + scan_result: mở DB SQLite (mặc định <thư mục chạy>/data/dlp.db, cấu hình ở mục
        // "Storage:DbPath"; đường dẫn tương đối tính theo thư mục chạy vì thư mục làm việc của Windows Service là System32).
        // Mở thất bại → service vẫn chạy, chỉ không ghi DB (lỗi lưu trữ không được làm dừng luồng quét).
        string dbPath = configuration["Storage:DbPath"] ?? Path.Combine("data", "dlp.db");
        if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
        }
        // [Thay đổi] Eligibility + P1/P2/P3 + hạn ngạch: đọc mục "Llm" (thiếu → mặc định trong code; từ khoá P2 mặc định nằm
        // ở appsettings.json). Chặn giá trị vô lý thay vì dừng service.
        var escalation = configuration.GetSection("Llm").Get<EscalationOptions>() ?? new EscalationOptions();
        escalation.MinTextChars = Math.Max(0, escalation.MinTextChars);
        escalation.SampleRatePct = Math.Clamp(escalation.SampleRatePct, 0, 100);
        var quota = configuration.GetSection("Llm:Quota").Get<JobQuotaOptions>() ?? new JobQuotaOptions();
        logger.LogInformation("[Job] Eligibility: tối thiểu {min} ký tự, {excluded} đường dẫn loại trừ, {keywords} từ khoá P2, lấy mẫu P3 {rate}%. Hạn ngạch: P1 {p1}/giờ (vượt → hoãn), P2 {p2}/ngày, P3 {p3}/ngày, tối đa {max} job pending",
            escalation.MinTextChars, escalation.ExcludedPaths.Count, escalation.SuspiciousKeywords.Count, escalation.SampleRatePct,
            quota.P1PerHour, quota.P2PerDay, quota.P3PerDay, quota.MaxPendingJobs);

        ResultStore? store = null;
        try
        {
            store = new ResultStore(dbPath, logger, quota); // [Thay đổi] truyền logger để cảnh báo ACL/PRAGMA; + hạn ngạch job
            logger.LogInformation("Ghi kết quả quét vào {dbPath}", dbPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Không mở được DB {dbPath}, service chạy tiếp nhưng không ghi kết quả vào DB", dbPath);
        }
        using var storeScope = store; // đóng DB khi service dừng

        // [Thay đổi] Hàng đợi llm_job (kiến trúc mục 3.2) khi khởi động: job running của lần chạy trước → pending.
        // Lỗi chỉ cảnh báo. KHÔNG tạo bù job vùng xám ở đây nữa — xem [Thay đổi] vá lỗi (buổi 11) bên dưới.
        int recovered = 0;
        if (store != null)
        {
            try
            {
                recovered = store.RecoverRunningJobs();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Job] Không khôi phục được hàng đợi llm_job");
            }
        }

        // [Thay đổi] Dọn dẹp theo lịch: đọc mốc giữ dữ liệu (≥ 1 ngày / ≥ 1 giờ để cấu hình sai không xoá sạch hay dọn liên tục)
        var cleanup = new CleanupOptions(
            JobDays: Math.Max(1, configuration.GetValue("Llm:JobRetentionDays", 30)),
            CacheDays: Math.Max(1, configuration.GetValue("Llm:CacheDays", 90)),
            AuditDays: Math.Max(1, configuration.GetValue("Llm:AuditRetentionDays", 180)),
            Interval: TimeSpan.FromHours(Math.Max(1, configuration.GetValue("Llm:CleanupIntervalHours", 24))));
        DateTime lastCleanupUtc = DateTime.UtcNow;
        RunCleanup(store, cleanup);

        // [Thay đổi] FileConfidence 2 nhánh (shadow): nạp hệ số từ <thư mục chạy>/confidence/file-confidence-v1.json (đổi được
        // qua "FileConfidence:ModelPath"). Lỗi → cảnh báo, service chạy tiếp không có điểm này (chỉ là tín hiệu quan sát).
        FileConfidenceScorer? fileConfidence = null;
        string confPath = configuration["FileConfidence:ModelPath"] ?? Path.Combine("confidence", "file-confidence-v1.json");
        if (!Path.IsPathRooted(confPath))
        {
            confPath = Path.Combine(AppContext.BaseDirectory, confPath);
        }
        try
        {
            fileConfidence = new FileConfidenceScorer(FileConfidenceModel.Load(confPath));
            logger.LogInformation("[Conf] FileConfidence 2 nhánh (shadow) bản {version} từ {path}", fileConfidence.Version, confPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning("[Conf] Không nạp được model FileConfidence {path}: {error} — bỏ qua tính điểm 2 nhánh", confPath, ex.Message);
        }

        // [Thay đổi] Giới hạn đồng thời: đọc "Watch:MaxConcurrency" (thiếu → 6). Sai (< 1) thì dùng mặc định + cảnh báo, không chặn
        // service khởi động — cùng cách xử lý với các ngưỡng khác ở trên.
        int maxConcurrency = configuration.GetValue("Watch:MaxConcurrency", FileWatcherService.DefaultMaxConcurrency);
        if (maxConcurrency < 1)
        {
            logger.LogWarning("Watch:MaxConcurrency không hợp lệ ({value}), dùng mặc định {default}", maxConcurrency, FileWatcherService.DefaultMaxConcurrency);
            maxConcurrency = FileWatcherService.DefaultMaxConcurrency;
        }

        // [Sửa lỗi] Gán vào trường _fileWatcher (không dùng biến cục bộ) — xem giải thích ở khai báo trường
        _fileWatcher = new FileWatcherService(scanner, engine, logger, routerOptions, store, escalation, fileConfidence, // [Thay đổi] + eligibility, + FileConfidence
            maxConcurrency); // [Thay đổi] + giới hạn đồng thời
        _fileWatcher.StartWatching(@"D:\Service-DLP\watch-folder");
        logger.LogInformation("Đang theo dõi thư mục: D:\\Service-DLP\\watch-folder");

        // [Thay đổi] Vá lỗi (buổi 11): EnqueueMissingGreyJobs ĐỌC scan_result.file_hash để tạo job bù — PHẢI chạy
        // SAU KHI StartWatching quét xong lượt đầu (ScanExistingFiles chạy đồng bộ bên trong, xong mới return), để
        // dùng đúng hash MỚI NHẤT. Trước đây gọi TRƯỚC StartWatching: dùng hash CŨ còn sót trong DB từ lần quét
        // trước, job tạo ra lệch hash với nội dung thật → LlmJobDispatcher luôn báo "stale_before" dù file không đổi
        // gì. Tệ hơn, job stale bị NOT EXISTS (...status<>'stale') coi là "chưa có job" nên MỖI LẦN RESTART lại tạo
        // thêm 1 job hỏng nữa cho cùng file, hàng đợi phình to dần qua từng lần khởi động (đã tái hiện thật).
        if (store != null)
        {
            try
            {
                int created = store.EnqueueMissingGreyJobs();
                var counts = store.CountJobsByStatus();
                logger.LogInformation("[Job] Khởi động: {recovered} running → pending, tạo bù {created} job vùng xám. Hiện có: {counts}",
                    recovered, created,
                    counts.Count == 0 ? "(trống)" : string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Job] Không tạo bù được job vùng xám");
            }
        }

        // [Thay đổi] mục 4.12: vòng lặp riêng cho việc gửi job LLM (10 giây/lần) — tách khỏi vòng dọn dẹp 1 phút để
        // job vùng xám được xử lý sớm hơn, không phải chờ tới chu kỳ dọn dẹp.
        Task llmLoop = store != null
            ? RunLlmDispatchLoopAsync(store, scanner, configuration, stoppingToken)
            : Task.CompletedTask;

        async Task RunMainLoopAsync()
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                }

                // [Thay đổi] Dọn dẹp theo lịch: vòng lặp vốn chạy mỗi phút → đủ chu kỳ thì dọn. Cập nhật mốc TRƯỚC khi dọn để dọn
                // lỗi cũng không thử lại mỗi phút (đợi chu kỳ sau).
                if (DateTime.UtcNow - lastCleanupUtc >= cleanup.Interval)
                {
                    lastCleanupUtc = DateTime.UtcNow;
                    RunCleanup(store, cleanup);
                }

                // [Thay đổi] Giãn nhịp "Worker running" từ 1 giây lên 1 phút/lần: ghi mỗi giây làm file log phình to
                // (~86.400 dòng/ngày) và tăng khả năng va chạm khi người dùng mở file log bằng Notepad
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        try
        {
            await Task.WhenAll(RunMainLoopAsync(), llmLoop);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service đang dừng bình thường — không phải lỗi
        }
        finally
        {
            // [Sửa lỗi] Service dừng: tắt watcher trước (ngừng nhận file mới) rồi mới đóng các thành phần khác
            _fileWatcher?.Dispose();
            _fileWatcher = null;

            // [Thay đổi] Tika native: huỷ parser và isolate GraalVM khi service dừng (an toàn khi chưa bật/chưa nạp)
            TikaNativeRuntime.Shutdown();
        }
    }

    // [Thay đổi] mục 4.12 - Worker điều phối job: dựng GuardedInferenceClient + LlmJobDispatcher 1 lần, lặp mỗi 10
    // giây. Luôn "await" TryDispatchOnceAsync trước khi lặp lại → tự nhiên chỉ có 1 job chạy đồng thời (mục 4.12:
    // "1 job đồng thời"), không cần khoá/semaphore riêng.
    private async Task RunLlmDispatchLoopAsync(ResultStore store, RegexScanner scanner,
        IConfiguration configuration, CancellationToken stoppingToken)
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

    // [Thay đổi] Dọn dẹp theo lịch: mốc giữ dữ liệu (ngày) + chu kỳ dọn, đọc từ appsettings.json mục "Llm"
    private sealed record CleanupOptions(int JobDays, int CacheDays, int AuditDays, TimeSpan Interval);

    // [Thay đổi] Dọn dẹp theo lịch: xoá job stale/failed, mục llm_cache và dòng audit quá hạn. KHÔNG dọn job done/llm_result
    // (tầng cache 1 cần). Lỗi chỉ cảnh báo — lần sau dọn lại. Mỗi lần dọn ghi đúng 1 dòng log.
    private void RunCleanup(ResultStore? store, CleanupOptions options)
    {
        if (store == null)
        {
            return;
        }

        try
        {
            int jobs = store.PurgeOldJobs(TimeSpan.FromDays(options.JobDays));
            int cache = store.PurgeLlmCache(TimeSpan.FromDays(options.CacheDays));
            int audit = store.PurgeOldAudit(TimeSpan.FromDays(options.AuditDays));
            logger.LogInformation("[Dọn dẹp] Xoá {jobs} job stale/failed > {jobDays} ngày, {cache} mục llm_cache > {cacheDays} ngày (còn {cacheLeft}), {audit} dòng audit > {auditDays} ngày. Lần sau: {next:HH:mm dd/MM}",
                jobs, options.JobDays, cache, options.CacheDays, store.CountLlmCache(), audit, options.AuditDays,
                DateTime.Now + options.Interval);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Dọn dẹp] Lỗi khi dọn dữ liệu cũ, thử lại ở chu kỳ sau");
        }
    }
}
