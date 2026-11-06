// [Thay đổi] Thêm namespace theo folder Watching/ và using để dùng FileReaderService (nay nằm ở namespace Reading)
using DlpService.Classification;
using DlpService.Pipeline; // [Thay đổi] Tách lõi bước 3: FileProcessor, ProcessOutcome
using DlpService.Platform; // [Thay đổi] Tách lõi bước 4: IFileChangeSource, IPathPolicy
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage; // [Thay đổi] Ghi finding + scan_result vào SQLite
using System.Collections.Concurrent; // [Thay đổi] Dùng ConcurrentDictionary cho các dict bị nhiều luồng truy cập
using System.Diagnostics;

namespace DlpService.Watching;

/// <summary>Theo dõi 1 thư mục, debounce sự kiện, đọc file rồi quét bằng RegexScanner khi có file mới/đổi.</summary>
/// <remarks>[Sửa lỗi] Nơi tạo PHẢI giữ tham chiếu mạnh tới đối tượng này suốt thời gian theo dõi (VD trường của Worker):
/// FileSystemWatcher của .NET chỉ tự giữ mình bằng tham chiếu yếu, không ai giữ thì GC thu hồi và việc theo dõi dừng âm thầm.</remarks>
// [Sửa lỗi] + IDisposable: tắt watcher và huỷ các lần xử lý đang chờ debounce khi service dừng
// [Thay đổi] GĐ3 (ke-hoach-trien-khai-linux.md mục 7): + xoá/đổi tên/di chuyển (R4), + tự theo dõi lại khi lỗi (R6), + quét khởi động song song (R5),
// + bỏ thư mục loại trừ, + quét đối soát định kỳ, + làm mới mẫu thư mục (người dùng mới), + xử lý lại file đọc trúng lúc đang ghi, + metric.
public class FileWatcherService : IDisposable
{
    /// <summary>[Thay đổi] GĐ3: thời gian debounce (gộp nhiều sự kiện của 1 file thành 1 lần xử lý).</summary>
    public const int DebounceMs = 300;

    private readonly FileReaderService _fileReader;

    // [Thay đổi] Tách lõi bước 3: pipeline xử lý 1 file (đọc → quét → phân loại → ghi DB) nằm ở Pipeline/FileProcessor. Lớp này chỉ còn
    // lo theo dõi thư mục + debounce + giới hạn đồng thời rồi gọi _processor.ProcessFile.
    private readonly FileProcessor _processor;
    // [Thay đổi] Bước B (checklist "chạy như Windows Service thật"): thay Console.WriteLine bằng ILogger —
    // Console không ai đọc khi chạy dưới Service Control Manager (không có console đính kèm).
    private readonly ILogger _logger;
    // [Thay đổi] Dictionary -> ConcurrentDictionary: dict này bị luồng watcher (ScheduleProcess) và luồng
    // ThreadPool (finally của ProcessAfterDebounceAsync) cùng đọc/ghi — Dictionary thường có thể hỏng dữ liệu.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingTimers = new(); // dict được tạo ngay khi khai báo

    // [Thay đổi] Tách lõi bước 3: dict _lastContentHash (chống quét trùng khi nội dung không đổi) đã chuyển sang FileProcessor cùng ProcessFile.

    // [Thay đổi] Nhóm D3: giữ watcher ở field thay vì biến cục bộ trong StartWatching — biến cục bộ sẽ mất
    // tham chiếu khi hàm kết thúc, GC có thể thu hồi watcher khiến service "câm" không rõ lý do.
    // [Sửa lỗi] Field này CHỈ đủ khi chính FileWatcherService còn được giữ — trước đây Worker giữ nó bằng biến cục bộ nên vẫn bị thu
    // hồi khi rảnh (xem Worker._fileWatcher). Tham chiếu mạnh phải nối liền từ Worker → FileWatcherService → FileSystemWatcher.
    // [Thay đổi] shared-core: theo dõi NHIỀU thư mục (máy trạm Linux cần Documents, Desktop...) → mỗi thư mục 1 watcher.
    // [Thay đổi] Tách lõi bước 4: các FileSystemWatcher nằm trong nguồn sự kiện IFileChangeSource (Platform/FileSystemWatcherSource). Tham chiếu mạnh
    // vẫn nối liền Worker → FileWatcherService → _source → FileSystemWatcher (xem lưu ý [Sửa lỗi] ở trên).
    private readonly IFileChangeSource _source;

    // [Thay đổi] Tách lõi bước 4: quy tắc đường dẫn theo hệ điều hành (so sánh hoa/thường, file tạm) — thay cho PathComparison tự viết ở đây
    private readonly IPathPolicy _pathPolicy;

    // [Thay đổi] GĐ3: kho kết quả (để xoá bản ghi file đã mất — R4, đối soát) và các tuỳ chọn theo dõi
    private readonly ResultStore? _store;
    private readonly WatchOptions _options;

    // [Thay đổi] shared-core: các thư mục gốc đang theo dõi — OnSourceFailed biết thư mục nào cần quét bù khi tràn buffer,
    // RelativeToWatchRoot biết tính đường dẫn tương đối theo thư mục gốc nào. Mọi truy cập đều giữ khoá _watchersLock.
    private readonly List<string> _watchFolders = [];
    private readonly Lock _watchersLock = new();

    // [Thay đổi] GĐ3: mẫu thư mục ("/home/*/Documents") cần mở rộng lại định kỳ để bắt người dùng mới (3.5)
    private readonly List<string> _patterns = [];

    // [Thay đổi] GĐ3: thư mục gốc đang được quét (khởi động / bù / đối soát) và đang chờ theo dõi lại (R6) — chặn chạy chồng 2 lần cho cùng 1 gốc
    private readonly ConcurrentDictionary<string, byte> _scanning = new();
    private readonly ConcurrentDictionary<string, byte> _recovering = new();

    // [Thay đổi] GĐ3: vòng nền (đối soát, làm mới mẫu, metric) + cờ dừng
    private readonly CancellationTokenSource _stopping = new();
    private Task? _background;
    private volatile bool _disposed;

    // [Thay đổi] GĐ3 (3.6): bộ đếm cho metric — chỉ là SỐ, không ghi đường dẫn (R7)
    private long _eventsChanged, _eventsDeleted, _processedFromEvents, _removedRecords, _changedDuringRead;
    private readonly ConcurrentQueue<double> _latencyMs = new();

    /// <summary>Tạo watcher với 1 scanner đã có sẵn rule để dùng khi quét file.</summary>
    /// <param name="scanner">Bộ quét regex, đã nạp rule từ trước.</param>
    /// <param name="engine">Engine phân loại theo domain, đã tự nạp module từ trước.</param>
    /// <param name="logger">Nơi ghi log kết quả (Console khi chạy tay, Event Log khi chạy dưới SCM).</param>
    /// <param name="routerOptions">[Thay đổi] Router ba vùng: ngưỡng T_high/T_low; null = mặc định 0,90/0,30.</param>
    /// <param name="store">[Thay đổi] Ghi scan_result/finding vào SQLite; null = không ghi DB (VD mở DB thất bại).</param>
    /// <param name="escalation">[Thay đổi] Eligibility + phân nhóm P1/P2/P3; null = mặc định.</param>
    /// <param name="maxConcurrency">[Thay đổi] Số file tối đa xử lý cùng lúc (≥ 1); mặc định <see cref="DefaultMaxConcurrency"/>.</param>
    /// <param name="changeSource">[Thay đổi] Tách lõi bước 4: nguồn sự kiện thay đổi file; null = FileSystemWatcherSource (như trước đây).</param>
    /// <param name="pathPolicy">[Thay đổi] Tách lõi bước 4: quy tắc đường dẫn; null = CaseInsensitivePathPolicy (hành vi Windows như trước đây —
    /// nơi gọi cũ đều chạy trên Windows; host của hệ điều hành khác PHẢI truyền chính sách của mình).</param>
    /// <param name="options">[Thay đổi] GĐ3: loại trừ thư mục, đối soát, làm mới mẫu, metric; null = mặc định (hành vi cũ).</param>
    public FileWatcherService(RegexScanner scanner, ClassificationEngine engine, ILogger logger, RouterOptions? routerOptions = null,
        ResultStore? store = null, EscalationOptions? escalation = null, FileConfidenceScorer? fileConfidence = null,
        int maxConcurrency = DefaultMaxConcurrency, IFileChangeSource? changeSource = null, IPathPolicy? pathPolicy = null,
        WatchOptions? options = null)
    {
        MaxConcurrency = Math.Max(1, maxConcurrency); // [Thay đổi] Giới hạn đồng thời: cấu hình sai (≤ 0) thì vẫn chạy được 1 file
        _gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        _pathPolicy = pathPolicy ?? new CaseInsensitivePathPolicy();
        _fileReader = new FileReaderService(_pathPolicy);
        _logger = logger;
        _store = store;
        _options = options ?? new WatchOptions();
        // [Thay đổi] Tách lõi bước 3: các giá trị mặc định (Router ba vùng, eligibility) giữ nguyên như cũ; relativePathOf = hàm tìm thư mục gốc ở dưới
        _processor = new FileProcessor(_fileReader, scanner, engine, logger,
            routerOptions ?? new RouterOptions(), store, escalation ?? new EscalationOptions(), fileConfidence,
            RelativeToWatchRoot, _pathPolicy);

        // [Thay đổi] Tách lõi bước 4: nghe sự kiện từ nguồn thay cho việc gọi thẳng FileSystemWatcher. FileChanged gộp Created/Changed/Renamed
        // (đúng như 3 handler cũ cùng gọi ScheduleProcess).
        // [Thay đổi] GĐ3 (R4): nghe thêm FileDeleted
        _source = changeSource ?? new FileSystemWatcherSource();
        _source.FileChanged += OnFileChanged;
        _source.FileDeleted += OnFileDeleted;
        _source.Failed += OnSourceFailed;
    }

    // [Thay đổi] Giới hạn số file xử lý cùng lúc (Watch:MaxConcurrency). Trước đây mỗi sự kiện file là 1 task chạy ngay → khi burst
    // service tự chạy ~19–33 file song song, đúng vùng thông lượng giảm. Đo DlpConcurrencyBench 29/10 (docs/buoi-15-tom-tat.md):
    // thông lượng bão hoà từ 6 luồng (895 file/s), 6 luồng có p95 14,5 ms so với 21 ms (8) / 39 ms (16), RAM thấp hơn.
    /// <summary>Số file xử lý cùng lúc mặc định khi không cấu hình.</summary>
    public const int DefaultMaxConcurrency = 6;

    /// <summary>Số file tối đa xử lý cùng lúc (đã chặn ≥ 1).</summary>
    public int MaxConcurrency { get; }

    // "Trạm gác": chỉ cho MaxConcurrency lần ProcessFile chạy cùng lúc, các file khác xếp hàng chờ. Không Dispose: task đang chạy dở
    // khi service dừng vẫn cần Release.
    private readonly SemaphoreSlim _gate;

    // [Thay đổi] Tách lõi bước 3: _fileConfidence, _escalation, _store, _routerOptions đã chuyển sang FileProcessor (chỉ pipeline dùng chúng).

    /// <summary>[Thay đổi] GĐ3: các thư mục gốc đang theo dõi (bản chụp).</summary>
    public IReadOnlyList<string> WatchedFolders
    {
        get { lock (_watchersLock) return [.. _watchFolders]; }
    }

    // [Thay đổi] Tách lõi bước 3: đường dẫn của file TÍNH TỪ thư mục gốc đang theo dõi, để xét từ khoá P2. Chuyển từ thân Escalate cũ sang
    // đây vì chỉ lớp này giữ danh sách thư mục gốc. Nhiều thư mục gốc → lấy thư mục DÀI NHẤT chứa file (thư mục lồng nhau thì chọn cái sâu
    // nhất); không thuộc thư mục nào (hoặc chưa StartWatching) → chỉ lấy tên file.
    private string RelativeToWatchRoot(string filePath)
    {
        string? root = FindRoot(filePath);
        return root != null ? Path.GetRelativePath(root, filePath) : Path.GetFileName(filePath);
    }

    // [Thay đổi] GĐ3: tách từ RelativeToWatchRoot để OnFileChanged dùng lại (xét đường dẫn loại trừ theo đúng thư mục gốc)
    private string? FindRoot(string filePath)
    {
        string? root = null;
        lock (_watchersLock)
        {
            foreach (var folder in _watchFolders)
            {
                string prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
                if (filePath.StartsWith(prefix, _pathPolicy.Comparison) && (root == null || folder.Length > root.Length))
                {
                    root = folder;
                }
            }
        }
        return root;
    }

    /// <summary>Bắt đầu theo dõi thư mục (mọi định dạng FileReaderService đọc được, kể cả thư mục con) và lắng nghe sự kiện Created/Changed/Renamed.</summary>
    /// <param name="folderPath">Đường dẫn thư mục cần theo dõi.</param>
    public void StartWatching(string folderPath)
    {
        // [Thay đổi] shared-core: thư mục không tồn tại → FileSystemWatcher ném ngoại lệ khó hiểu; báo rõ rồi bỏ qua thư mục đó,
        // không chặn các thư mục còn lại. (Trước đây ScanExistingFiles im lặng thoát nên service "chạy mà không quét gì".)
        if (!Directory.Exists(folderPath))
        {
            _logger.LogWarning("[FileWatcher] Thư mục theo dõi không tồn tại, bỏ qua: {folder}", folderPath);
            return;
        }

        // [Thay đổi] shared-core: ghi nhận thư mục vào danh sách TRƯỚC khi bật theo dõi, để RelativeToWatchRoot/OnSourceFailed
        // (chạy trên luồng khác ngay khi có sự kiện) đã thấy thư mục này
        lock (_watchersLock)
        {
            // [Thay đổi] GĐ3: làm mới mẫu có thể gọi lại cho thư mục đã theo dõi → bỏ qua
            if (_watchFolders.Any(f => string.Equals(f, folderPath, _pathPolicy.Comparison)))
            {
                return;
            }
            _watchFolders.Add(folderPath);
        }
        // [Thay đổi] Tách lõi bước 4: việc tạo FileSystemWatcher (cây thư mục con, buffer 64KB, filter theo đuôi, các sự kiện) nằm ở
        // FileSystemWatcherSource.Watch — chuyển nguyên văn từ đây. Danh sách đuôi vẫn lấy từ FileReaderService để thêm định dạng mới
        // không phải sửa watcher.
        // [Thay đổi] GĐ3: + quy tắc loại trừ thư mục; lỗi gắn theo dõi → không ném ra Worker, để cơ chế theo dõi lại (R6) thử tiếp
        try
        {
            _source.Watch(folderPath, _fileReader.SupportedExtensions, _options.Exclusion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogError(ex, "[FileWatcher] Không theo dõi được {folder} — sẽ thử lại", folderPath);
            StartRecovery(folderPath);
            return;
        }
        _logger.LogInformation("[FileWatcher] Xử lý tối đa {max} file cùng lúc", MaxConcurrency); // [Thay đổi] Giới hạn đồng thời

        // [Thay đổi] Nhóm D1 - FileSystemWatcher chỉ bắt sự kiện XẢY RA SAU khi bắt đầu theo dõi; file đã
        // nằm sẵn trong thư mục TRƯỚC lúc service khởi động (VD sau khi máy reboot) sẽ không bao giờ được
        // quét nếu không chủ động quét 1 lượt ngay tại đây.
        // [Thay đổi] GĐ3: + đối soát (xoá bản ghi file đã bị xoá lúc service tắt)
        ScanExistingFiles(folderPath, "khởi động");
        LogWatchUsage();
    }

    /// <summary>[Thay đổi] GĐ3 (3.5): ghi nhớ 1 mẫu thư mục (VD "/home/*/Documents") để vòng nền mở rộng lại định kỳ — người dùng tạo
    /// SAU lúc khởi động được theo dõi trong vòng <see cref="WatchOptions.PatternRefreshInterval"/>.</summary>
    public void TrackPattern(string pattern)
    {
        lock (_watchersLock)
        {
            _patterns.Add(pattern);
        }
    }

    /// <summary>[Thay đổi] GĐ3: bật vòng nền (đối soát định kỳ, làm mới mẫu, metric) theo <see cref="WatchOptions"/>. Gọi SAU khi đã
    /// StartWatching xong các thư mục ban đầu. Không có chu kỳ nào được bật → không tạo vòng nền.</summary>
    public void StartBackgroundTasks()
    {
        if (_background != null || _disposed) return;
        if (_options.ReconcileInterval <= TimeSpan.Zero && _options.PatternRefreshInterval <= TimeSpan.Zero && _options.MetricsInterval <= TimeSpan.Zero)
        {
            return;
        }
        _background = Task.Run(() => BackgroundLoopAsync(_stopping.Token));
    }

    // [Thay đổi] GĐ3: 1 vòng nền, mỗi giây xem việc nào đến hạn. Mỗi việc tự bắt lỗi — 1 lần lỗi không được làm dừng cả vòng.
    private async Task BackgroundLoopAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan nextReconcile = _options.ReconcileInterval, nextPattern = _options.PatternRefreshInterval, nextMetrics = _options.MetricsInterval;
        var lastMetrics = TimeSpan.Zero;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            var now = clock.Elapsed;
            Guard("kiểm tra watch", CheckWatchHealth);
            if (_options.PatternRefreshInterval > TimeSpan.Zero && now >= nextPattern)
            {
                nextPattern = now + _options.PatternRefreshInterval;
                Guard("làm mới mẫu thư mục", RefreshPatterns);
            }
            if (_options.ReconcileInterval > TimeSpan.Zero && now >= nextReconcile)
            {
                nextReconcile = now + _options.ReconcileInterval;
                foreach (var folder in WatchedFolders)
                {
                    if (token.IsCancellationRequested) return;
                    Guard("quét đối soát", () => ScanExistingFiles(folder, "đối soát"));
                }
            }
            if (_options.MetricsInterval > TimeSpan.Zero && now >= nextMetrics)
            {
                nextMetrics = now + _options.MetricsInterval;
                var span = now - lastMetrics;
                lastMetrics = now;
                Guard("ghi metric", () => LogMetrics(span));
            }
        }

        void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FileWatcher] Lỗi khi {what}", what);
            }
        }
    }

    // [Thay đổi] GĐ3 (3.5): mở rộng lại các mẫu, thư mục mới khớp (người dùng mới, Documents vừa tạo) → bắt đầu theo dõi
    private void RefreshPatterns()
    {
        string[] patterns;
        lock (_watchersLock) patterns = [.. _patterns];
        foreach (var pattern in patterns)
        {
            foreach (var folder in WatchFolderPatterns.Expand(pattern))
            {
                bool known;
                lock (_watchersLock) known = _watchFolders.Any(f => string.Equals(f, folder, _pathPolicy.Comparison));
                if (!known && !_disposed)
                {
                    _logger.LogInformation("[FileWatcher] Mẫu \"{pattern}\": thư mục mới {folder} — bắt đầu theo dõi", pattern, folder);
                    StartWatching(folder);
                }
            }
        }
    }

    // [Thay đổi] Nhóm D1: quét mọi file *.txt đang có sẵn (kể cả thư mục con, khớp IncludeSubdirectories ở
    // trên) ngay lúc bắt đầu theo dõi — dùng lại ĐÚNG logic xử lý 1 file (ProcessFile) mà luồng sự kiện
    // watcher vẫn dùng, không viết riêng 1 bản dễ lệch nhau.
    // [Thay đổi] GĐ3: dùng chung cho 3 lượt — khởi động, quét bù sau tràn hàng đợi, đối soát định kỳ. Thêm:
    //   - R5: xử lý SONG SONG (tối đa MaxConcurrency, vẫn xin chỗ ở trạm gác chung) thay vì từng file một;
    //   - bỏ thư mục loại trừ + không đi theo symlink (WatchExclusion), bỏ thư mục không đọc được thay vì dừng cả lượt quét;
    //   - đối soát (R4): bản ghi trong DB nằm dưới thư mục này mà file không còn trên đĩa → xoá (file bị xoá lúc service tắt / sự kiện bị mất);
    //   - file đọc trúng lúc đang ghi → hẹn xử lý lại qua debounce.
    private void ScanExistingFiles(string folderPath, string reason)
    {
        if (!Directory.Exists(folderPath) || _disposed)
        {
            return;
        }
        if (!_scanning.TryAdd(folderPath, 0))
        {
            return; // đang có 1 lượt quét khác cho thư mục này (VD tràn hàng đợi đúng lúc đối soát)
        }
        try
        {
            var clock = Stopwatch.StartNew();
            // [Thay đổi] Cache file_state: đếm số file quét / bỏ qua để log 1 dòng tổng kết (từng file bỏ qua chỉ log Debug)
            int total = 0, scanned = 0, unchanged = 0, retried = 0;
            var present = new ConcurrentDictionary<string, byte>(_pathPolicy.Comparison == StringComparison.Ordinal
                ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

            // [Thay đổi] Mở rộng định dạng: duyệt mọi file rồi lọc bằng IsSupported, thay cho mẫu "*.txt" cố định. Không
            // dùng mẫu "*.xls" của Directory.GetFiles vì trên Windows nó khớp NHẦM cả ".xlsx" (quirk tên 8.3).
            var files = _options.Exclusion.EnumerateFiles(folderPath).Where(_fileReader.IsSupported);
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency }, filePath =>
            {
                if (_disposed) return;
                present.TryAdd(filePath, 0);
                Interlocked.Increment(ref total);
                // [Thay đổi] Giới hạn đồng thời: lượt quét này chạy song song với file từ sự kiện watcher (watcher đã bật trước) → cũng phải
                // xin chỗ ở trạm gác, để tổng số file xử lý cùng lúc không vượt MaxConcurrency
                _gate.Wait();
                ProcessOutcome outcome;
                try
                {
                    outcome = _processor.ProcessFile(filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[FileWatcher] Lỗi xử lý file {filePath}", filePath);
                    return;
                }
                finally
                {
                    _gate.Release();
                }
                switch (outcome)
                {
                    case ProcessOutcome.Scanned: Interlocked.Increment(ref scanned); break;
                    case ProcessOutcome.Unchanged: Interlocked.Increment(ref unchanged); break;
                    case ProcessOutcome.ChangedDuringRead:
                        Interlocked.Increment(ref retried);
                        Interlocked.Increment(ref _changedDuringRead);
                        ScheduleProcess(filePath);
                        break;
                }
            });

            // Đối soát: bản ghi của file không còn trên đĩa → xoá
            int removed = 0;
            if (_store != null && !_disposed)
            {
                foreach (var path in _store.ListPathsUnder(folderPath))
                {
                    if (!present.ContainsKey(path) && !File.Exists(path))
                    {
                        removed += RemoveRecords(path);
                    }
                }
            }

            _logger.LogInformation("[FileWatcher] Quét ({reason}) {folder}: {total} file — quét {scanned}, bỏ qua {unchanged} không đổi, lỗi đọc {failed}, đang ghi dở {retried}, xoá {removed} bản ghi file không còn ({ms} ms)",
                reason, folderPath, total, scanned, unchanged, total - scanned - unchanged - retried, retried, removed, clock.ElapsedMilliseconds);
        }
        finally
        {
            _scanning.TryRemove(folderPath, out _);
        }
    }

    // [Thay đổi] Tách lõi bước 4: OnFileChanged/OnFileRenamed đã bỏ — FileSystemWatcherSource gộp Created/Changed/Renamed thành sự kiện FileChanged
    // (kể cả trường hợp trình soạn thảo ghi file tạm rồi đổi tên: chỉ có Renamed, vẫn được báo là FileChanged với đường dẫn mới).
    // [Thay đổi] GĐ3: OnFileChanged quay lại làm "cửa vào" cho FileChanged để đếm metric và bỏ đường dẫn trong thư mục loại trừ
    // (nguồn FileSystemWatcherSource đã lọc; dòng này là chốt chặn cho nguồn khác).
    private void OnFileChanged(string filePath)
    {
        Interlocked.Increment(ref _eventsChanged);
        string? root = FindRoot(filePath);
        if (root != null && _options.Exclusion.IsExcludedPath(root, filePath, isDirectory: false))
        {
            return;
        }
        ScheduleProcess(filePath);
    }

    // [Thay đổi] GĐ3 (R4): đường dẫn biến mất (file bị xoá, đổi tên khỏi, thư mục bị xoá / chuyển đi).
    //  - Đúng đuôi được hỗ trợ → đưa qua debounce như sự kiện sửa: tới lượt xử lý mà file không còn thì xoá bản ghi; xoá rồi tạo lại
    //    ngay (trình soạn thảo "lưu an toàn") thì thành 1 lần quét bình thường, không xoá nhầm.
    //  - Không đúng đuôi (có thể là thư mục, hoặc file tạm) → xoá ngay bản ghi của mọi thứ bên dưới đường dẫn này (thường không có gì → 1 lần đọc index).
    private void OnFileDeleted(string path)
    {
        Interlocked.Increment(ref _eventsDeleted);
        if (_fileReader.IsSupported(path))
        {
            ScheduleProcess(path);
            return;
        }
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            RemoveRecords(path);
        }
    }

    // [Thay đổi] GĐ3 (R4): xoá bản ghi DB + hash trong RAM của đường dẫn (và mọi thứ bên dưới). Lỗi DB chỉ log — lần đối soát sau thử lại.
    private int RemoveRecords(string path)
    {
        _processor.Forget(path);
        if (_store == null) return 0;
        try
        {
            int removed = _store.RemovePath(path);
            if (removed > 0)
            {
                Interlocked.Add(ref _removedRecords, removed);
                _logger.LogInformation("[FileWatcher] {path}: không còn trên đĩa — xoá bản ghi {count} file", path, removed);
            }
            return removed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FileWatcher] Không xoá được bản ghi của {path}", path);
            return 0;
        }
    }

    // [Thay đổi] Nhóm D3: xử lý lỗi của watcher, chia 2 trường hợp:
    //  - Tràn buffer: watcher VẪN chạy nhưng đã mất 1 số sự kiện -> log cảnh báo rồi quét lại cả thư mục để
    //    bù file bị sót (_lastContentHash sẽ tự bỏ qua file nội dung không đổi, nên không bị log lặp).
    //  - Lỗi khác (thư mục bị xoá, mất quyền, mất mạng...): watcher có thể đã ngừng hẳn -> log lỗi rõ ràng.
    // [Thay đổi] Tách lõi bước 4: nhận (thư mục, đã tràn hàng đợi chưa, ngoại lệ) từ IFileChangeSource thay vì ErrorEventArgs của FileSystemWatcher.
    // shared-core: chỉ quét bù đúng thư mục bị lỗi (không quét lại thư mục khác)
    // [Thay đổi] GĐ3: quét bù chạy NỀN (Task.Run) — trước đây chạy ngay trên luồng của nguồn sự kiện, chặn nó đọc tiếp → càng dễ tràn thêm.
    // Lỗi khác → R6: tự theo dõi lại khi thư mục dùng được (StartRecovery), thay vì chỉ log rồi "câm" mãi.
    private void OnSourceFailed(string watchFolder, bool overflowed, Exception ex)
    {
        if (_disposed) return;
        if (overflowed)
        {
            _logger.LogWarning("[FileWatcher] Buffer tràn, có thể đã mất sự kiện — quét lại toàn thư mục {folder}", watchFolder);

            // [Thay đổi] Bọc try/catch quanh lượt quét bù: OnWatcherError chạy trên luồng ThreadPool, exception
            // không được bắt ở đây sẽ làm SẬP cả service. Bắt lại -> ghi log -> service vẫn tiếp tục theo dõi.
            _ = Task.Run(() =>
            {
                try
                {
                    ScanExistingFiles(watchFolder, "bù sau tràn hàng đợi");
                }
                catch (Exception scanEx)
                {
                    _logger.LogError(scanEx, "[FileWatcher] Quét bù sau khi tràn buffer thất bại {folder}", watchFolder);
                }
            });
        }
        else
        {
            _logger.LogError(ex, "[FileWatcher] Watcher gặp lỗi, có thể đã ngừng theo dõi {folder} — sẽ tự theo dõi lại khi thư mục dùng được", watchFolder);
            StartRecovery(watchFolder);
        }
    }

    // [Thay đổi] GĐ3 (R6): chờ thư mục gốc dùng được lại (VD Documents bị xoá rồi tạo lại, ổ mạng mất rồi có lại) → gỡ theo dõi cũ, theo dõi
    // lại, quét đối soát. Chờ tăng dần 5 s → 10 s → … → tối đa 60 s. Mỗi thư mục chỉ 1 vòng chờ cùng lúc.
    private void StartRecovery(string folder)
    {
        if (_disposed || !_recovering.TryAdd(folder, 0)) return;
        var token = _stopping.Token;
        _ = Task.Run(async () =>
        {
            var delay = _options.RecoveryInitialDelay;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(delay, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    if (Directory.Exists(folder))
                    {
                        try
                        {
                            _source.Unwatch(folder);
                            _source.Watch(folder, _fileReader.SupportedExtensions, _options.Exclusion);
                            lock (_watchersLock)
                            {
                                if (!_watchFolders.Any(f => string.Equals(f, folder, _pathPolicy.Comparison))) _watchFolders.Add(folder);
                            }
                            _logger.LogInformation("[FileWatcher] Đã theo dõi lại {folder}", folder);
                            ScanExistingFiles(folder, "sau khi theo dõi lại");
                            return;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            _logger.LogWarning("[FileWatcher] Chưa theo dõi lại được {folder}: {error}", folder, ex.Message);
                        }
                    }
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.RecoveryMaxDelay.Ticks));
                }
            }
            finally
            {
                _recovering.TryRemove(folder, out _);
            }
        });
    }

    // [Thay đổi] Gộp logic debounce dùng chung cho cả 3 sự kiện (Created/Changed/Renamed) — trước đây chỉ
    // OnFileChanged có, giờ tách ra để OnFileRenamed dùng lại được, không chép lặp code.
    private void ScheduleProcess(string filePath)
    {
        // [Thay đổi] Mở rộng định dạng: bỏ qua file khoá tạm của Office ("~$baocao.docx") — Filters "*.docx" vẫn
        // khớp những file này nên phải lọc lại ở đây
        if (!_fileReader.IsSupported(filePath) || _disposed)
        {
            return;
        }

        // [Thay đổi] GĐ3: AddOrUpdate thay cho cặp TryGetValue + gán (2 bước rời: 2 luồng cùng gọi cho 1 file có thể cùng tạo CTS, cái
        // bị ghi đè không ai huỷ → file bị xử lý 2 lần). AddOrUpdate đổi giá trị nguyên tử; CTS cũ (nếu có) được huỷ ngay sau đó.
        var newCts = new CancellationTokenSource();
        CancellationTokenSource? replaced = null;
        _pendingTimers.AddOrUpdate(filePath, newCts, (_, old) => { replaced = old; return newCts; });
        if (replaced != null)
        {
            try
            {
                replaced.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // lần chạy cũ đã xong
            }
        }

        // [Thay đổi] Truyền cả CTS (thay vì chỉ Token) để finally dùng được TryRemove(KeyValuePair) so sánh đúng CTS
        // [Thay đổi] GĐ3: + thời điểm nhận sự kiện (đo độ trễ cho metric)
        _ = ProcessAfterDebounceAsync(filePath, newCts, Stopwatch.GetTimestamp());
    }

    // [Thay đổi] Tham số đổi từ CancellationToken sang CancellationTokenSource (xem finally bên dưới)
    private async Task ProcessAfterDebounceAsync(string filePath, CancellationTokenSource myCts, long eventTimestamp)
    {
        try
        {
            await Task.Delay(DebounceMs, myCts.Token);

            // [Thay đổi] GĐ3 (R4): hết debounce mà file không còn (bị xoá / đổi tên đi) → xoá bản ghi NGAY, KHÔNG xếp hàng ở trạm gác:
            // trạm gác giới hạn số file đang ĐỌC + QUÉT (việc nặng); xoá bản ghi chỉ là 1 lệnh DB. Đo khi viết (xoá 10.000 file, trạm gác 2 chỗ):
            // xếp hàng chung làm p95 độ trễ của cả lượt lên 2,8 s.
            if (!File.Exists(filePath))
            {
                _latencyMs.Enqueue(Math.Max(0, Stopwatch.GetElapsedTime(eventTimestamp).TotalMilliseconds - DebounceMs));
                Interlocked.Increment(ref _processedFromEvents);
                if (!Directory.Exists(filePath)) RemoveRecords(filePath);
                return;
            }

            // [Thay đổi] Giới hạn đồng thời: xin chỗ trước khi xử lý. Đang xếp hàng mà có sự kiện mới hơn cho cùng file (hoặc service
            // dừng) thì huỷ được ngay — không giữ chỗ cho 1 nội dung đã cũ.
            await _gate.WaitAsync(myCts.Token);
            try
            {
                // [Thay đổi] GĐ3 (3.6): độ trễ từ lúc nhận sự kiện tới lúc bắt đầu xử lý, đã trừ debounce
                _latencyMs.Enqueue(Math.Max(0, Stopwatch.GetElapsedTime(eventTimestamp).TotalMilliseconds - DebounceMs));
                Interlocked.Increment(ref _processedFromEvents);

                var outcome = _processor.ProcessFile(filePath);
                if (outcome == ProcessOutcome.ChangedDuringRead)
                {
                    // [Thay đổi] GĐ3 (3.4): đọc trúng lúc đang ghi → hẹn lại sau debounce (ghi xong thường có thêm sự kiện; hẹn lại để chắc)
                    Interlocked.Increment(ref _changedDuringRead);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(DebounceMs);
                        ScheduleProcess(filePath);
                    });
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        // [Thay đổi] TaskCanceledException → OperationCanceledException (lớp cha): huỷ khi đang chờ ở trạm gác cũng thuộc loại này
        catch (OperationCanceledException)
        {
            // Sự kiện mới hơn đã đến, bỏ qua lần xử lý này
        }
        catch (Exception ex)
        {
            // [Thay đổi] Nhóm D - bọc try/catch rộng: trước đây chỉ bắt TaskCanceledException, 1 lỗi bất ngờ
            // khi đọc/quét file (file bị khoá, encoding lạ, hết dung lượng đĩa...) sẽ trở thành exception
            // "unobserved" trong Task chạy rời (_ = ProcessAfterDebounceAsync(...)) — không crash service
            // nhưng cũng không ai biết đã xảy ra. Bắt lại và ghi rõ vào log thay vì để trôi đi âm thầm.
            _logger.LogError(ex, "[FileWatcher] Lỗi xử lý file {filePath}", filePath);
        }
        finally
        {
            // [Thay đổi] Chuyển từ cuối khối try sang finally: đảm bảo LUÔN dọn _pendingTimers dù xử lý
            // thành công, bị huỷ (TaskCanceledException), hay gặp lỗi bất ngờ (catch Exception ở trên) —
            // trước đây nếu có lỗi bất ngờ, dòng dọn dẹp này sẽ KHÔNG chạy, khiến filePath bị kẹt trong dict.
            //
            // [Thay đổi] Race condition: chỉ xoá entry nếu nó vẫn đúng là CTS của CHÍNH lần chạy này — nếu 1
            // sự kiện MỚI HƠN đã đến trong lúc lần chạy này đang xử lý (quá điểm Task.Delay có thể huỷ được),
            // _pendingTimers[filePath] lúc này đã bị ghi đè bằng CTS của lần chạy mới. Xoá vô điều kiện sẽ xoá
            // NHẦM CTS của lần chạy mới đó, khiến 1 sự kiện thứ 3 đến sau không còn huỷ được nó — phá vỡ đúng
            // cơ chế debounce.
            //
            // [Thay đổi] Thay cặp "TryGetValue rồi Remove" (2 bước rời, luồng khác có thể chen vào giữa) bằng
            // TryRemove(KeyValuePair): ConcurrentDictionary chỉ xoá khi value hiện tại ĐÚNG là myCts, và làm
            // việc so sánh + xoá liền 1 bước (atomic) — không luồng nào chen vào giữa được.
            _pendingTimers.TryRemove(new KeyValuePair<string, CancellationTokenSource>(filePath, myCts));
        }
    }

    // [Thay đổi] GĐ3 (3.2): sau khi gắn theo dõi, báo số watch so với giới hạn của hệ điều hành (chỉ nguồn nào đo được — inotify)
    private void LogWatchUsage()
    {
        if (_source is not IWatchStatistics stats) return;
        int used = stats.WatchCount, limit = stats.WatchLimit;
        if (stats.FailedWatchCount > 0)
        {
            _logger.LogError("[Watch] {failed} thư mục KHÔNG gắn được theo dõi (dùng {used}/{limit} watch) — thay đổi trong đó chỉ được phát hiện khi quét đối soát. Tăng giới hạn: sudo sysctl fs.inotify.max_user_watches=524288 (ghi vào /etc/sysctl.d/ để giữ sau khi khởi động lại), hoặc thêm thư mục vào Watch:ExcludeDirectoryNames",
                stats.FailedWatchCount, used, limit);
        }
        else if (limit > 0 && used * 100L >= limit * 80L)
        {
            _logger.LogWarning("[Watch] Đã dùng {used}/{limit} watch (≥ 80% giới hạn) — sắp hết. Tăng fs.inotify.max_user_watches hoặc loại bớt thư mục", used, limit);
        }
        else
        {
            _logger.LogInformation("[Watch] Đang dùng {used}/{limit} watch", used, limit < 0 ? "?" : limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // [Thay đổi] GĐ3 (3.2): cảnh báo NGAY khi hết watch / sắp hết trong lúc đang chạy (thư mục con tạo thêm sau khởi động). Trước đây chỉ
    // kiểm lúc StartWatching → hết watch lúc chạy chỉ thấy trong dòng metric. Mỗi loại cảnh báo tối đa 1 lần / 10 phút để không tràn log.
    private int _reportedFailedWatches;
    private DateTime _lastFailedWarningUtc = DateTime.MinValue, _lastUsageWarningUtc = DateTime.MinValue;

    private void CheckWatchHealth()
    {
        if (_source is not IWatchStatistics stats) return;
        var now = DateTime.UtcNow;
        int failed = stats.FailedWatchCount;
        if (failed > _reportedFailedWatches && now - _lastFailedWarningUtc >= TimeSpan.FromMinutes(10))
        {
            _reportedFailedWatches = failed;
            _lastFailedWarningUtc = now;
            _logger.LogError("[Watch] {failed} thư mục KHÔNG gắn được theo dõi (dùng {used}/{limit} watch — giới hạn tính chung cho mọi chương trình của tài khoản này) — thay đổi trong đó chỉ được phát hiện khi quét đối soát (Watch:ReconcileIntervalMinutes). Tăng giới hạn: sudo sysctl fs.inotify.max_user_watches=524288 (ghi vào /etc/sysctl.d/ để giữ sau khi khởi động lại), hoặc thêm thư mục vào Watch:ExcludeDirectoryNames",
                failed, stats.WatchCount, stats.WatchLimit);
        }
        else if (failed == 0 && stats.WatchLimit > 0 && stats.WatchCount * 100L >= stats.WatchLimit * 80L && now - _lastUsageWarningUtc >= TimeSpan.FromMinutes(10))
        {
            _lastUsageWarningUtc = now;
            _logger.LogWarning("[Watch] Đã dùng {used}/{limit} watch (≥ 80% giới hạn) — sắp hết. Tăng fs.inotify.max_user_watches hoặc loại bớt thư mục", stats.WatchCount, stats.WatchLimit);
        }
    }

    // [Thay đổi] GĐ3 (3.6, ke-hoach-trien-khai-linux.md 8.1): 1 dòng metric mỗi chu kỳ — CHỈ số, không đường dẫn/nội dung (R7)
    private void LogMetrics(TimeSpan span)
    {
        long changed = Interlocked.Exchange(ref _eventsChanged, 0);
        long deleted = Interlocked.Exchange(ref _eventsDeleted, 0);
        long processed = Interlocked.Exchange(ref _processedFromEvents, 0);
        long removed = Interlocked.Exchange(ref _removedRecords, 0);
        long retried = Interlocked.Exchange(ref _changedDuringRead, 0);
        var lat = new List<double>();
        while (_latencyMs.TryDequeue(out var l)) lat.Add(l);
        lat.Sort();
        double P(double q) => lat.Count == 0 ? 0 : lat[Math.Min(lat.Count - 1, (int)Math.Ceiling(q * lat.Count) - 1)];

        string watch = "—";
        if (_source is IWatchStatistics s)
        {
            watch = $"{s.WatchCount}/{(s.WatchLimit < 0 ? "?" : s.WatchLimit.ToString(System.Globalization.CultureInfo.InvariantCulture))} (lỗi {s.FailedWatchCount}), tràn tổng {s.OverflowCount}, sự kiện thô tổng {s.RawEventCount}";
        }
        _logger.LogInformation("[Metric] {sec:0}s: watch {watch} | sự kiện đổi {changed}, xoá {deleted} → xử lý {processed} | trễ p50 {p50:0} ms, p95 {p95:0} ms, max {max:0} ms | đang chờ {pending}, ghi dở {retried}, xoá bản ghi {removed}",
            span.TotalSeconds, watch, changed, deleted, processed, P(0.50), P(0.95), lat.Count == 0 ? 0 : lat[^1], _pendingTimers.Count, retried, removed);
    }

    // [Sửa lỗi] Watcher bị GC thu hồi: dừng theo dõi CHỦ ĐỘNG khi service dừng (thay vì chờ GC), huỷ các file đang chờ debounce
    // (chưa bắt đầu đọc) để không xử lý tiếp lúc DB sắp đóng. File ĐANG xử lý dở thì để chạy nốt — ResultStore tự bỏ qua khi đã đóng.
    /// <summary>Dừng theo dõi thư mục và huỷ các lần xử lý đang chờ.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopping.Cancel(); // [Thay đổi] GĐ3: dừng vòng nền + các vòng chờ theo dõi lại
        // [Thay đổi] Tách lõi bước 4: nguồn sự kiện tự tắt TẤT CẢ watcher (mỗi thư mục 1 cái); gỡ đăng ký trước để không nhận thêm sự kiện lúc đang dừng
        _source.FileChanged -= OnFileChanged;
        _source.FileDeleted -= OnFileDeleted;
        _source.Failed -= OnSourceFailed;
        _source.Dispose();

        foreach (var cts in _pendingTimers.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Lần xử lý đó vừa xong và tự dọn — bỏ qua
            }
        }
        GC.SuppressFinalize(this);
    }

}
