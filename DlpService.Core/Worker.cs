// [Thay đổi] Thêm using để dùng FileWatcherService (nay nằm ở namespace Watching)
using DlpService.Platform; // [Thay đổi] Tách lõi bước 1: IAppLayout
using DlpService.Scanning; // [Thay đổi] để dùng RuleLoader và RegexScanner
using DlpService.Watching;
using DlpService.Classification;
using DlpService.Storage; // [Thay đổi] Ghi finding + scan_result
using DlpService.Pipeline; // [Thay đổi] Tách lõi bước 5: IJobDispatchLoop
using DlpService.Reading;
using DlpService.Reading.Native; // [Thay đổi] Tika native: cấu hình + vòng đời thư viện native

namespace DlpService;

/// <summary>Điểm khởi động luồng nền: nạp rule, tạo scanner và bắt đầu theo dõi thư mục watch-folder.</summary>
// [Thay đổi] Router ba vùng: nhận thêm IConfiguration (DI tự truyền) để đọc ngưỡng T_high/T_low từ appsettings.json
// [Thay đổi] Tách lõi bước 1: nhận thêm IAppLayout (DI tự truyền) — mọi thư mục rule/DB/model lấy từ đây thay vì ghép AppContext.BaseDirectory
// [Thay đổi] Tách lõi bước 4: + IPathPolicy, IDataFolderGuard (adapter theo hệ điều hành do host đăng ký; DI tự truyền)
public class Worker(ILogger<Worker> logger, IConfiguration configuration, IAppLayout layout, IPathPolicy pathPolicy, IDataFolderGuard dataFolderGuard,
    IJobDispatchLoop? dispatchLoop = null) : BackgroundService // [Thay đổi] Tách lõi bước 5: vòng điều phối job (LLM) do host đăng ký, có thể không có
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
        var ruleFolder = layout.RulesFolder; // [Thay đổi] Tách lõi bước 1: từ IAppLayout
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
        var engine = new ClassificationEngine(logger, ruleFolder); // [Thay đổi] Tách lõi bước 1: truyền thư mục rule cho module compound
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
        // [Thay đổi] Tách lõi bước 1: đường dẫn tương đối/mặc định do IAppLayout quyết định (Windows: tính theo thư mục file chạy như cũ)
        string dbPath = layout.ResolvePath(configuration["Storage:DbPath"] ?? layout.DefaultDbPath);
        // [Thay đổi] Eligibility + P1/P2/P3 + hạn ngạch: đọc mục "Llm" (thiếu → mặc định trong code; từ khoá P2 mặc định nằm
        // ở appsettings.json). Chặn giá trị vô lý thay vì dừng service.
        var escalation = configuration.GetSection("Llm").Get<EscalationOptions>() ?? new EscalationOptions();
        escalation.MinTextChars = Math.Max(0, escalation.MinTextChars);
        escalation.SampleRatePct = Math.Clamp(escalation.SampleRatePct, 0, 100);
        var quota = configuration.GetSection("Llm:Quota").Get<JobQuotaOptions>() ?? new JobQuotaOptions();
        // [Thay đổi] Tách lõi bước 2: công tắc LLM. Tắt → bỏ hàng đợi llm_job, vòng điều phối và việc tạo bù job vùng xám bên dưới.
        bool llmEnabled = escalation.Enabled;
        if (!llmEnabled)
        {
            logger.LogInformation("[LLM] Tắt (Llm:Enabled=false): chỉ chạy regex, vùng xám ghi final + needs_review, không tạo llm_job");
        }
        logger.LogInformation("[Job] Eligibility: tối thiểu {min} ký tự, {excluded} đường dẫn loại trừ, {keywords} từ khoá P2, lấy mẫu P3 {rate}%. Hạn ngạch: P1 {p1}/giờ (vượt → hoãn), P2 {p2}/ngày, P3 {p3}/ngày, tối đa {max} job pending",
            escalation.MinTextChars, escalation.ExcludedPaths.Count, escalation.SuspiciousKeywords.Count, escalation.SampleRatePct,
            quota.P1PerHour, quota.P2PerDay, quota.P3PerDay, quota.MaxPendingJobs);

        ResultStore? store = null;
        try
        {
            store = new ResultStore(dbPath, logger, quota, dataFolderGuard); // [Thay đổi] truyền logger để cảnh báo ACL/PRAGMA; + hạn ngạch job
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
        if (store != null && llmEnabled) // [Thay đổi] Tách lõi bước 2
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
        // [Thay đổi] Tách lõi bước 1: như dbPath ở trên, qua IAppLayout
        string confPath = layout.ResolvePath(configuration["FileConfidence:ModelPath"] ?? layout.DefaultConfidenceModelPath);
        // [Thay đổi] GĐ2 (R11): công tắc "FileConfidence:Enabled" (mặc định true = như cũ). Bản Linux tắt: chỉ còn MỘT hệ chấm điểm
        // (S1 + Router) quyết định, không tốn thời gian tính một điểm chỉ để quan sát, và gói cài đặt không cần mang model.
        if (!configuration.GetValue("FileConfidence:Enabled", true))
        {
            logger.LogInformation("[Conf] FileConfidence tắt (FileConfidence:Enabled=false): chỉ dùng S1 + Router");
        }
        else
        {
            try
            {
                fileConfidence = new FileConfidenceScorer(FileConfidenceModel.Load(confPath));
                logger.LogInformation("[Conf] FileConfidence 2 nhánh (shadow) bản {version} từ {path}", fileConfidence.Version, confPath);
            }
            catch (Exception ex)
            {
                logger.LogWarning("[Conf] Không nạp được model FileConfidence {path}: {error} — bỏ qua tính điểm 2 nhánh", confPath, ex.Message);
            }
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
            maxConcurrency, pathPolicy: pathPolicy); // [Thay đổi] + giới hạn đồng thời; Tách lõi bước 4: + quy tắc đường dẫn
        // [Thay đổi] shared-core: thư mục theo dõi đọc từ cấu hình "Watch:Folders" (trước đây hard-code ổ D:). Không có mặc định:
        // thiếu/rỗng → báo LỖI rõ rồi dừng worker, vì chạy mà không theo dõi gì còn tệ hơn (service trông bình thường nhưng không quét).
        // [Thay đổi] GĐ2 (R8): mỗi mục có thể là MẪU ("/home/*/Documents") → mở rộng thành các thư mục có thật lúc khởi động
        var watchFolders = new List<string>();
        foreach (var entry in (configuration.GetSection("Watch:Folders").Get<string[]>() ?? []).Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            if (!WatchFolderPatterns.IsPattern(entry))
            {
                watchFolders.Add(Path.GetFullPath(entry));
                continue;
            }
            var expanded = WatchFolderPatterns.Expand(entry);
            if (expanded.Count == 0)
            {
                logger.LogWarning("Mẫu thư mục theo dõi \"{pattern}\" không khớp thư mục nào", entry);
            }
            else
            {
                logger.LogInformation("Mẫu thư mục theo dõi \"{pattern}\" → {count} thư mục", entry, expanded.Count);
            }
            watchFolders.AddRange(expanded);
        }
        watchFolders = watchFolders.Distinct().ToList();
        if (watchFolders.Count == 0)
        {
            logger.LogError("Chưa cấu hình \"Watch:Folders\" (danh sách thư mục cần theo dõi) trong appsettings.json — service không có gì để quét");
            _fileWatcher.Dispose();
            _fileWatcher = null;
            return;
        }
        foreach (var folder in watchFolders)
        {
            _fileWatcher.StartWatching(folder);
            logger.LogInformation("Đang theo dõi thư mục: {folder}", folder);
        }

        // [Thay đổi] Vá lỗi (buổi 11): EnqueueMissingGreyJobs ĐỌC scan_result.file_hash để tạo job bù — PHẢI chạy
        // SAU KHI StartWatching quét xong lượt đầu (ScanExistingFiles chạy đồng bộ bên trong, xong mới return), để
        // dùng đúng hash MỚI NHẤT. Trước đây gọi TRƯỚC StartWatching: dùng hash CŨ còn sót trong DB từ lần quét
        // trước, job tạo ra lệch hash với nội dung thật → LlmJobDispatcher luôn báo "stale_before" dù file không đổi
        // gì. Tệ hơn, job stale bị NOT EXISTS (...status<>'stale') coi là "chưa có job" nên MỖI LẦN RESTART lại tạo
        // thêm 1 job hỏng nữa cho cùng file, hàng đợi phình to dần qua từng lần khởi động (đã tái hiện thật).
        if (store != null && llmEnabled) // [Thay đổi] Tách lõi bước 2
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
        // [Thay đổi] Tách lõi bước 5: chạy vòng do host cung cấp (LLM) qua IJobDispatchLoop; host không đăng ký (Linux) hoặc LLM tắt thì bỏ qua
        Task llmLoop = store != null && llmEnabled && dispatchLoop != null
            ? dispatchLoop.RunAsync(store, scanner, stoppingToken)
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
