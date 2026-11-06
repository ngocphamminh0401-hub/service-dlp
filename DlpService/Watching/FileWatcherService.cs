// [Thay đổi] Thêm namespace theo folder Watching/ và using để dùng FileReaderService (nay nằm ở namespace Reading)
using DlpService.Classification;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage; // [Thay đổi] Ghi finding + scan_result vào SQLite
using System.Collections.Concurrent; // [Thay đổi] Dùng ConcurrentDictionary cho các dict bị nhiều luồng truy cập
using System.Text.Json; // [Thay đổi] labels_json
using System.Text;

namespace DlpService.Watching;

/// <summary>Theo dõi 1 thư mục, debounce sự kiện, đọc file rồi quét bằng RegexScanner khi có file mới/đổi.</summary>
/// <remarks>[Sửa lỗi] Nơi tạo PHẢI giữ tham chiếu mạnh tới đối tượng này suốt thời gian theo dõi (VD trường của Worker):
/// FileSystemWatcher của .NET chỉ tự giữ mình bằng tham chiếu yếu, không ai giữ thì GC thu hồi và việc theo dõi dừng âm thầm.</remarks>
// [Sửa lỗi] + IDisposable: tắt watcher và huỷ các lần xử lý đang chờ debounce khi service dừng
public class FileWatcherService : IDisposable
{
    private readonly FileReaderService _fileReader;
    private readonly RegexScanner _scanner; // [Thay đổi] scanner nhận từ ngoài, dùng để quét nội dung file
    private readonly ClassificationEngine _engine;
    // [Thay đổi] Bước B (checklist "chạy như Windows Service thật"): thay Console.WriteLine bằng ILogger —
    // Console không ai đọc khi chạy dưới Service Control Manager (không có console đính kèm).
    private readonly ILogger _logger;
    // [Thay đổi] Dictionary -> ConcurrentDictionary: dict này bị luồng watcher (ScheduleProcess) và luồng
    // ThreadPool (finally của ProcessAfterDebounceAsync) cùng đọc/ghi — Dictionary thường có thể hỏng dữ liệu.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingTimers = new(); // dict được tạo ngay khi khai báo

    // [Thay đổi] Nhóm D (checklist "chạy như Windows Service thật") - "bỏ qua quét trùng khi nội dung không
    // đổi": lưu hash nội dung lần quét gần nhất theo từng file, để nhận ra khi nhiều sự kiện watcher (Created/
    // Changed/Renamed) rời rạc cùng trỏ tới 1 lần lưu file thực tế (VD VS Code ghi file làm nhiều bước) không
    // bị quét/log lặp lại nhiều lần cho cùng 1 nội dung.
    // [Thay đổi] Dictionary -> ConcurrentDictionary: nhiều Task debounce và ScanExistingFiles (gọi từ
    // OnWatcherError) có thể cùng gọi ProcessFile -> cùng đọc/ghi dict này một lúc.
    private readonly ConcurrentDictionary<string, string> _lastContentHash = new();

    // [Thay đổi] Nhóm D3: giữ watcher ở field thay vì biến cục bộ trong StartWatching — biến cục bộ sẽ mất
    // tham chiếu khi hàm kết thúc, GC có thể thu hồi watcher khiến service "câm" không rõ lý do.
    // [Sửa lỗi] Field này CHỈ đủ khi chính FileWatcherService còn được giữ — trước đây Worker giữ nó bằng biến cục bộ nên vẫn bị thu
    // hồi khi rảnh (xem Worker._fileWatcher). Tham chiếu mạnh phải nối liền từ Worker → FileWatcherService → FileSystemWatcher.
    private FileSystemWatcher? _watcher;

    // [Thay đổi] Nhóm D3: nhớ thư mục đang theo dõi để OnWatcherError biết chỗ quét lại khi buffer bị tràn.
    private string _watchFolder = string.Empty;

    /// <summary>Tạo watcher với 1 scanner đã có sẵn rule để dùng khi quét file.</summary>
    /// <param name="scanner">Bộ quét regex, đã nạp rule từ trước.</param>
    /// <param name="engine">Engine phân loại theo domain, đã tự nạp module từ trước.</param>
    /// <param name="logger">Nơi ghi log kết quả (Console khi chạy tay, Event Log khi chạy dưới SCM).</param>
    /// <param name="routerOptions">[Thay đổi] Router ba vùng: ngưỡng T_high/T_low; null = mặc định 0,90/0,30.</param>
    /// <param name="store">[Thay đổi] Ghi scan_result/finding vào SQLite; null = không ghi DB (VD mở DB thất bại).</param>
    /// <param name="escalation">[Thay đổi] Eligibility + phân nhóm P1/P2/P3; null = mặc định.</param>
    /// <param name="maxConcurrency">[Thay đổi] Số file tối đa xử lý cùng lúc (≥ 1); mặc định <see cref="DefaultMaxConcurrency"/>.</param>
    public FileWatcherService(RegexScanner scanner, ClassificationEngine engine, ILogger logger, RouterOptions? routerOptions = null,
        ResultStore? store = null, EscalationOptions? escalation = null, FileConfidenceScorer? fileConfidence = null,
        int maxConcurrency = DefaultMaxConcurrency)
    {
        MaxConcurrency = Math.Max(1, maxConcurrency); // [Thay đổi] Giới hạn đồng thời: cấu hình sai (≤ 0) thì vẫn chạy được 1 file
        _gate = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        _fileReader = new FileReaderService();
        _scanner = scanner;
        _engine = engine;
        _logger = logger;
        _routerOptions = routerOptions ?? new RouterOptions(); // [Thay đổi] Router ba vùng
        _store = store; // [Thay đổi] Ghi finding + scan_result
        _escalation = escalation ?? new EscalationOptions(); // [Thay đổi] Eligibility + P1/P2/P3
        _fileConfidence = fileConfidence; // [Thay đổi] FileConfidence 2 nhánh (shadow)
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

    // [Thay đổi] FileConfidence 2 nhánh (shadow): null = không nạp được model → bỏ qua, không ảnh hưởng luồng quét
    private readonly FileConfidenceScorer? _fileConfidence;

    // [Thay đổi] Eligibility + phân nhóm P1/P2/P3 (đọc từ appsettings.json mục "Llm" ở Worker)
    private readonly EscalationOptions _escalation;

    // [Thay đổi] Ghi finding + scan_result: kho kết quả SQLite (null = không ghi)
    private readonly ResultStore? _store;

    // [Thay đổi] Router ba vùng: ngưỡng định tuyến (đọc từ appsettings.json mục "Score" ở Worker)
    private readonly RouterOptions _routerOptions;

    /// <summary>Bắt đầu theo dõi thư mục (mọi định dạng FileReaderService đọc được, kể cả thư mục con) và lắng nghe sự kiện Created/Changed/Renamed.</summary>
    /// <param name="folderPath">Đường dẫn thư mục cần theo dõi.</param>
    public void StartWatching(string folderPath)
    {
        // [Thay đổi] Nhóm D3: lưu lại thư mục và dùng field _watcher thay cho biến cục bộ "var watcher"
        _watchFolder = folderPath;
        _watcher = new FileSystemWatcher(folderPath)
        {
            // [Thay đổi] Mở rộng định dạng: bỏ "Filter = *.txt" cố định — danh sách đuôi lấy từ FileReaderService
            // (thêm vào Filters ngay dưới), để thêm định dạng mới không phải sửa watcher.
            // [Thay đổi] Nhóm D2 - mặc định FileSystemWatcher chỉ theo dõi ĐÚNG 1 cấp thư mục, bỏ sót mọi
            // thư mục con. Bật lên để nhìn xuyên xuống toàn bộ cây thư mục con của watch-folder.
            IncludeSubdirectories = true,
            // [Thay đổi] Nhóm D3: tăng buffer từ 8KB (mặc định) lên 64KB (mức tối đa khuyến nghị) để giảm
            // khả năng tràn khi nhiều file đổi dồn dập. Tăng hơn nữa sẽ tốn bộ nhớ kernel (non-paged pool).
            InternalBufferSize = 64 * 1024
        };
        // [Thay đổi] Mở rộng định dạng: mỗi đuôi hỗ trợ 1 filter ("*.txt", "*.docx", ...)
        foreach (var extension in _fileReader.SupportedExtensions)
        {
            _watcher.Filters.Add("*" + extension);
        }
        _watcher.Created += OnFileChanged;
        _watcher.Changed += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        // [Thay đổi] Nhóm D3: nghe sự kiện Error — nếu không đăng ký, lỗi tràn buffer/mất thư mục trôi đi âm thầm
        _watcher.Error += OnWatcherError;

        _watcher.EnableRaisingEvents = true;
        _logger.LogInformation("[FileWatcher] Xử lý tối đa {max} file cùng lúc", MaxConcurrency); // [Thay đổi] Giới hạn đồng thời

        // [Thay đổi] Nhóm D1 - FileSystemWatcher chỉ bắt sự kiện XẢY RA SAU khi bắt đầu theo dõi; file đã
        // nằm sẵn trong thư mục TRƯỚC lúc service khởi động (VD sau khi máy reboot) sẽ không bao giờ được
        // quét nếu không chủ động quét 1 lượt ngay tại đây.
        ScanExistingFiles(folderPath);
    }

    // [Thay đổi] Nhóm D1: quét mọi file *.txt đang có sẵn (kể cả thư mục con, khớp IncludeSubdirectories ở
    // trên) ngay lúc bắt đầu theo dõi — dùng lại ĐÚNG logic xử lý 1 file (ProcessFile) mà luồng sự kiện
    // watcher vẫn dùng, không viết riêng 1 bản dễ lệch nhau.
    private void ScanExistingFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        // [Thay đổi] Cache file_state: đếm số file quét / bỏ qua để log 1 dòng tổng kết (từng file bỏ qua chỉ log Debug)
        int total = 0, scanned = 0, unchanged = 0;

        // [Thay đổi] Mở rộng định dạng: duyệt mọi file rồi lọc bằng IsSupported, thay cho mẫu "*.txt" cố định. Không
        // dùng mẫu "*.xls" của Directory.GetFiles vì trên Windows nó khớp NHẦM cả ".xlsx" (quirk tên 8.3).
        foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).Where(_fileReader.IsSupported))
        {
            total++;
            // [Thay đổi] Giới hạn đồng thời: lượt quét này chạy song song với file từ sự kiện watcher (watcher đã bật trước) → cũng phải
            // xin chỗ ở trạm gác, để tổng số file xử lý cùng lúc không vượt MaxConcurrency
            _gate.Wait();
            ProcessOutcome outcome;
            try
            {
                outcome = ProcessFile(filePath);
            }
            finally
            {
                _gate.Release();
            }
            switch (outcome)
            {
                case ProcessOutcome.Scanned: scanned++; break;
                case ProcessOutcome.Unchanged: unchanged++; break;
            }
        }

        _logger.LogInformation("[FileWatcher] Quét {folder}: {total} file — quét {scanned}, bỏ qua {unchanged} không đổi, lỗi đọc {failed}",
            folderPath, total, scanned, unchanged, total - scanned - unchanged);
    }

    // [Thay đổi] Cache file_state: kết quả xử lý 1 file (để ScanExistingFiles đếm)
    private enum ProcessOutcome
    {
        /// <summary>Đã quét (và ghi DB nếu có).</summary>
        Scanned,
        /// <summary>Không đổi so với lần quét trước → bỏ qua.</summary>
        Unchanged,
        /// <summary>Đọc file thất bại.</summary>
        Failed
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        ScheduleProcess(e.FullPath);
    }

    // [Thay đổi] Nhiều trình soạn thảo (VD VS Code) "tạo file mới" bằng cách ghi ra file tạm rồi ĐỔI TÊN
    // thành tên thật — thao tác đó chỉ phát sự kiện Renamed, KHÔNG phát Created lẫn Changed, nên watcher cũ
    // (chỉ nghe 2 sự kiện đó) bỏ sót hoàn toàn những file tạo theo kiểu này.
    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        ScheduleProcess(e.FullPath);
    }

    // [Thay đổi] Nhóm D3: xử lý lỗi của watcher, chia 2 trường hợp:
    //  - Tràn buffer: watcher VẪN chạy nhưng đã mất 1 số sự kiện -> log cảnh báo rồi quét lại cả thư mục để
    //    bù file bị sót (_lastContentHash sẽ tự bỏ qua file nội dung không đổi, nên không bị log lặp).
    //  - Lỗi khác (thư mục bị xoá, mất quyền, mất mạng...): watcher có thể đã ngừng hẳn -> log lỗi rõ ràng.
    //    Việc tự khởi động lại watcher để dành cho bước sau.
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();

        if (ex is InternalBufferOverflowException)
        {
            _logger.LogWarning("[FileWatcher] Buffer tràn, có thể đã mất sự kiện — quét lại toàn thư mục {folder}", _watchFolder);

            // [Thay đổi] Bọc try/catch quanh lượt quét bù: OnWatcherError chạy trên luồng ThreadPool, exception
            // không được bắt ở đây sẽ làm SẬP cả service. Bắt lại -> ghi log -> service vẫn tiếp tục theo dõi.
            try
            {
                ScanExistingFiles(_watchFolder);
            }
            catch (Exception scanEx)
            {
                _logger.LogError(scanEx, "[FileWatcher] Quét bù sau khi tràn buffer thất bại {folder}", _watchFolder);
            }
        }
        else
        {
            _logger.LogError(ex, "[FileWatcher] Watcher gặp lỗi, có thể đã ngừng theo dõi {folder}", _watchFolder);
        }
    }

    // [Thay đổi] Gộp logic debounce dùng chung cho cả 3 sự kiện (Created/Changed/Renamed) — trước đây chỉ
    // OnFileChanged có, giờ tách ra để OnFileRenamed dùng lại được, không chép lặp code.
    private void ScheduleProcess(string filePath)
    {
        // [Thay đổi] Mở rộng định dạng: bỏ qua file khoá tạm của Office ("~$baocao.docx") — Filters "*.docx" vẫn
        // khớp những file này nên phải lọc lại ở đây
        if (!_fileReader.IsSupported(filePath))
        {
            return;
        }

        if(_pendingTimers.TryGetValue(filePath, out var oldCts))
        {
            oldCts.Cancel();
        }

        var newCts = new CancellationTokenSource();
        _pendingTimers[filePath] = newCts;

        // [Thay đổi] Truyền cả CTS (thay vì chỉ Token) để finally dùng được TryRemove(KeyValuePair) so sánh đúng CTS
        _ = ProcessAfterDebounceAsync(filePath, newCts);
    }

    // [Thay đổi] Tham số đổi từ CancellationToken sang CancellationTokenSource (xem finally bên dưới)
    private async Task ProcessAfterDebounceAsync(string filePath, CancellationTokenSource myCts)
    {
        try
        {
            await Task.Delay(300, myCts.Token);

            // [Thay đổi] Giới hạn đồng thời: xin chỗ trước khi xử lý. Đang xếp hàng mà có sự kiện mới hơn cho cùng file (hoặc service
            // dừng) thì huỷ được ngay — không giữ chỗ cho 1 nội dung đã cũ.
            await _gate.WaitAsync(myCts.Token);
            try
            {
                ProcessFile(filePath);
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

    // [Thay đổi] Tách ra từ ProcessAfterDebounceAsync: phần "đọc file -> kiểm tra trùng hash -> quét ->
    // phân loại -> log" không liên quan gì tới debounce/CancellationToken — tách riêng để dùng lại được ở cả
    // luồng sự kiện watcher (qua ProcessAfterDebounceAsync) LẪN ScanExistingFiles (quét khi khởi động, Nhóm D1),
    // tránh viết 2 bản logic dễ lệch nhau.
    // [Thay đổi] Cache file_state: trả ProcessOutcome thay cho void để ScanExistingFiles đếm được số file bỏ qua
    private ProcessOutcome ProcessFile(string filePath)
    {
        // [Thay đổi] Đo hiệu năng service thật: bấm giờ từng bước, ghi 1 dòng [Perf] ở cuối mỗi file ĐÃ QUÉT (file bỏ qua
        // nhờ cache không ghi). Mốc thời gian lấy bằng Stopwatch (độ phân giải < 1 µs), không ảnh hưởng luồng xử lý.
        var perf = System.Diagnostics.Stopwatch.StartNew();
        double cacheMs = 0, readMs = 0, hashMs = 0, scanMs = 0, classifyMs = 0, saveMs = 0;
        double Lap(ref double mark) { double now = perf.Elapsed.TotalMilliseconds, d = now - mark; mark = now; return d; }
        double lastMark = 0;

        // [Thay đổi] Cache file_state: chụp size/mtime TRƯỚC khi đọc file. Nếu lấy SAU khi quét (như TrySave cũ), file bị
        // sửa đúng lúc đang quét sẽ lưu mtime MỚI đi kèm kết quả của nội dung CŨ → lần sau tầng 1 coi là "không đổi" và
        // nội dung mới không bao giờ được quét. Chụp trước: file sửa giữa chừng → mtime lưu là cũ → lần sau quét lại.
        var info = new FileInfo(filePath);
        bool exists = info.Exists;
        long size = exists ? info.Length : 0;
        DateTime mtimeUtc = exists ? info.LastWriteTimeUtc : DateTime.UtcNow;

        // [Thay đổi] Cache tầng 1 (kiến trúc mục 2.3 bước 2): size + mtime giống hệt lần trước và lần trước đã kết luận
        // được → bỏ qua, KHÔNG mở file (trích text là bước tốn thời gian nhất)
        var cached = TryGetCached(filePath);
        if (exists && cached is { HasConclusion: true } && cached.SameSizeAndMtime(size, mtimeUtc))
        {
            _logger.LogDebug("[FileWatcher] {filePath}: không đổi (size/mtime), bỏ qua", filePath);
            return ProcessOutcome.Unchanged;
        }

        cacheMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng: tra cache file_state (đọc DB)

        // [Thay đổi] Mở rộng định dạng: TryReadTextFile → TryReadFile (tự chọn extractor theo đuôi file)
        var result = _fileReader.TryReadFile(filePath);
        readMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng: trích text
        if (result.Success)
        {
            string content = result.Content ?? string.Empty;
            string hash = ComputeContentHash(content);
            hashMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng: băm SHA-256

            // [Thay đổi] Cache tầng 2: size/mtime đổi nhưng text trích ra y hệt (VD mở file rồi Save lại, không sửa gì)
            // → không quét lại, chỉ cập nhật size/mtime mới để lần sau tầng 1 bỏ qua được luôn
            if (cached is { HasConclusion: true } && cached.FileHash == hash)
            {
                _lastContentHash[filePath] = hash;
                TryMarkUnchanged(filePath, size, mtimeUtc);
                _logger.LogDebug("[FileWatcher] {filePath}: nội dung không đổi (file_hash), bỏ qua", filePath);
                return ProcessOutcome.Unchanged;
            }

            // [Thay đổi] Nhóm D - "bỏ qua quét trùng khi nội dung không đổi": so hash nội dung lần này với
            // lần quét gần nhất của ĐÚNG file này — trùng thì bỏ qua hẳn (không quét, không log), vì đây
            // chỉ là 1 sự kiện watcher "thừa" cho cùng 1 lần lưu, không phải dữ liệu mới cần xem xét.
            // [Thay đổi] Cache file_state: giữ lại cache trong RAM này để vẫn chống quét trùng khi không mở được DB
            if (_lastContentHash.TryGetValue(filePath, out var previousHash) && previousHash == hash)
            {
                return ProcessOutcome.Unchanged;
            }
            _lastContentHash[filePath] = hash;

            // [Thay đổi] Mở rộng định dạng: đọc được nhưng chưa trọn vẹn (bị cắt vì quá dài, PDF scan không có text)
            // — ghi cảnh báo để biết kết quả quét có thể thiếu. Đặt SAU kiểm tra hash để không lặp cảnh báo.
            if (result.Warning != null)
            {
                _logger.LogWarning("[FileWatcher] {filePath}: {warning}", filePath, result.Warning);
            }

            // [Thay đổi] Thay vì in nguyên nội dung file (có thể lộ dữ liệu nhạy cảm), quét rồi chỉ in kết quả đã che
            // [Thay đổi] Log timeout: Scan() trả ScanResult (chỗ khớp + rule bị timeout) thay cho danh sách chỗ khớp
            var scan = _scanner.Scan(content);
            var matches = scan.Matches;
            scanMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng: quét regex (gồm counter near-miss)

            // [Thay đổi] Đo hiệu năng: ghi dòng [Perf] — gọi ngay trước mỗi "return ProcessOutcome.Scanned" bên dưới
            void LogPerf()
            {
                _logger.LogInformation("[Perf] {filePath}: cache {cache:0.0} ms, đọc {read:0.0} ms, băm {hash:0.0} ms, quét {scan:0.0} ms, phân loại {classify:0.0} ms, ghi DB {save:0.0} ms, tổng {total:0.0} ms",
                    filePath, cacheMs, readMs, hashMs, scanMs, classifyMs, saveMs, perf.Elapsed.TotalMilliseconds);
            }

            // [Thay đổi] Log timeout: có rule quá thời gian → quét CHƯA TRỌN, không đủ căn cứ kết luận. Cảnh báo (tên rule
            // + đường dẫn file), KHÔNG gắn mức, KHÔNG log "sạch" (trước đây timeout bị nuốt im lặng → file có thể bị log
            // "sạch" dù chưa quét hết — lỗ để né phát hiện). Chỉ log tên rule, không log giá trị trong file.
            if (!scan.IsComplete)
            {
                _logger.LogWarning("[DLP] {filePath}: quét CHƯA TRỌN — {count} rule quá thời gian: {rules}",
                    filePath, scan.TimedOutPatterns.Count, string.Join(", ", scan.TimedOutPatterns));
                _logger.LogWarning("[DLP] {filePath}: CHƯA KẾT LUẬN ĐƯỢC — không gắn mức, cần xem lại ({count} chỗ khớp tìm được trước khi dừng)",
                    filePath, matches.Count);

                // Xoá hash đã lưu ở trên để lần quét sau (file đổi, quét bù khi buffer tràn) được thử lại thay vì bị bỏ qua
                // vì "nội dung không đổi". Chỉ xoá nếu hash vẫn là của lần này (luồng khác có thể vừa ghi hash mới hơn).
                _lastContentHash.TryRemove(new KeyValuePair<string, string>(filePath, hash));

                // [Thay đổi] Ghi DB: chưa kết luận → provisional, needs_review, s1 = 0, không có nhãn. Vẫn lưu finding của
                // các chỗ khớp tìm được trước khi dừng (chỉ mã rule + vị trí).
                classifyMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng
                TrySave(filePath, size, mtimeUtc, hash, 0, ResultStore.StatusProvisional, needsReview: true, labelsJson: null, BuildFindings(matches, null));
                saveMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng
                LogPerf();
                return ProcessOutcome.Scanned;
            }

            if (matches.Count == 0)
            {
                // [Thay đổi] mục 1.3/4.8: "sạch" và "cần OCR" là 2 trường hợp khác nhau — file cần OCR chưa từng được
                // đọc ra chữ nên KHÔNG BIẾT có nhạy cảm hay không, không phải thật sự "sạch"
                if (result.NeedsOcr)
                {
                    _logger.LogWarning("[DLP] {filePath}: cần OCR (không có text layer) — không đẩy LLM", filePath);
                }
                else
                {
                    _logger.LogInformation("[DLP] {filePath}: sạch", filePath);
                }

                // [Thay đổi] Eligibility + P1/P2/P3: file sạch vẫn có thể xuống LLM nếu tên đáng nghi (P2) hoặc được chọn mẫu (P3)
                var decision = Escalate(RouteZone.NoMatch, content.Length, filePath, hash, result.NeedsOcr);

                // [Thay đổi] FileConfidence (shadow): file sạch → nhánh B/Short ("có thật là Public không?")
                var conf = ComputeFileConfidence(filePath, SensitivityLevel.Public, matches, scan.Telemetry);

                classifyMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng: file sạch → chỉ eligibility + FileConfidence

                // [Thay đổi] Ghi DB: sạch → final, Public, không có finding (P2/P3 vẫn final — theo regex là sạch)
                var outcome = TrySave(filePath, size, mtimeUtc, hash, 0, ResultStore.StatusFinal, needsReview: false,
                    LabelsJson(SensitivityLevel.Public, []), [], llmPriority: (int?)decision.Group, needsOcr: result.NeedsOcr, conf: conf);
                saveMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng
                LogJobOutcome(filePath, decision, outcome);
            }
            else
            {
                _logger.LogInformation("[DLP] {filePath}: phát hiện {count} chỗ nhạy cảm", filePath, matches.Count);
                foreach (var m in matches)
                {
                    // [Thay đổi] Bậc 3 #7: thêm điểm tin cậy ("—" nếu rule không khai confidence), để quan sát phân bố
                    // điểm thật mà chỉnh min_confidence của từng rule
                    _logger.LogInformation(" - {pattern} tại vị trí {index}: {masked} (tin cậy {confidence})",
                        m.PatternName, m.Index, Mask(m.Value), m.Confidence?.ToString("0.00") ?? "—");
                }
                var context = new ScanContext
                {
                    FilePath = filePath,
                    Content = content,
                    Matches = matches
                };
                var (level, results) = _engine.Classify(context);

                _logger.LogInformation("[DLP] {filePath}: mức {level}", filePath, level);
                foreach (var r in results)
                {
                    _logger.LogInformation("  - {domain}: {reasons}", r.Domain, string.Join(", ", r.Reasons));
                }

                // [Thay đổi] S1 giai đoạn 1: tính S1 cấp file. Chi tiết theo rule chỉ ghi ở mức Debug (không hiện khi
                // chạy thật vì appsettings.json mặc định Information). Chỉ log mã rule + điểm, không log giá trị.
                var s1 = FileScorer.Compute(matches, content.Length);
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[S1] {filePath}: Internal={i:0.000} Confidential={c:0.000} Restricted={r:0.000} | {rules}",
                        filePath, s1.Internal, s1.Confidential, s1.Restricted,
                        string.Join(", ", s1.Rules.Select(x => $"{x.RuleKey}({x.Level}, {x.DistinctValues} gt, {x.Score:0.00})")));
                }

                // [Thay đổi] Router ba vùng (baseline v1): mức GIỮ NGUYÊN như engine gán ở trên; S1 của mức đó là độ tin
                // cậy. Vùng xám → cần LLM thẩm định — chưa có LLM process nên hiện chỉ đánh dấu needs_review trong log.
                var route = ScoreRouter.Route(level, s1, matches.Count, _routerOptions);
                if (route.NeedsLlm)
                {
                    _logger.LogWarning("[DLP] {filePath}: độ tin cậy {confidence:0.00} (vùng xám {tLow:0.00}-{tHigh:0.00}) — cần LLM thẩm định, needs_review",
                        filePath, route.Confidence, _routerOptions.TLow, _routerOptions.THigh);
                }
                else
                {
                    _logger.LogInformation("[DLP] {filePath}: độ tin cậy {confidence:0.00} ({zone}) — final",
                        filePath, route.Confidence, route.Zone == RouteZone.Certain ? "chắc chắn" : "tín hiệu yếu");
                }

                // [Thay đổi] FileConfidence (shadow): tính song song với S1 để so sánh — Router ở trên vẫn quyết định
                var conf = ComputeFileConfidence(filePath, level, matches, scan.Telemetry);

                // [Thay đổi] Eligibility + P1/P2/P3: vùng xám → P1 nếu qua điều kiện cần; tín hiệu yếu → P2/P3 như file sạch
                var decision = Escalate(route.Zone, content.Length, filePath, hash);

                // [Thay đổi] Vùng xám nhưng KHÔNG đủ điều kiện gửi LLM (VD quá ít text): không có job nào sẽ chốt kết quả nên
                // ghi luôn final + needs_review=1 (giữ điểm regex, cần người xem — giống kiến trúc mục 7 khi LLM thất bại). Để
                // provisional thì EnqueueMissingGreyJobs lúc khởi động sẽ tạo bù job, vượt qua bộ lọc.
                bool waitsForLlm = decision.Group == EscalationGroup.P1;
                if (route.NeedsLlm && !waitsForLlm)
                {
                    _logger.LogWarning("[DLP] {filePath}: vùng xám nhưng không gửi LLM ({reason}) — final, needs_review", filePath, decision.Reason);
                }

                classifyMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng: engine + S1 + Router + FileConfidence + eligibility

                // [Thay đổi] Ghi DB: vùng xám (đủ điều kiện) → provisional + needs_review (chờ LLM); còn lại → final. s1 = độ tin
                // cậy của mức.
                var outcome = TrySave(filePath, size, mtimeUtc, hash, route.Confidence,
                    waitsForLlm ? ResultStore.StatusProvisional : ResultStore.StatusFinal,
                    needsReview: route.NeedsLlm,
                    LabelsJson(level, results.Select(r => r.Domain)),
                    BuildFindings(matches, s1),
                    // [Thay đổi] Hàng đợi llm_job: nhóm P1/P2/P3 → tạo job (cùng transaction với scan_result, qua hạn ngạch)
                    llmPriority: (int?)decision.Group,
                    conf: conf); // [Thay đổi] FileConfidence (shadow)
                saveMs = Lap(ref lastMark); // [Thay đổi] Đo hiệu năng
                LogJobOutcome(filePath, decision, outcome);
            }

            LogPerf(); // [Thay đổi] Đo hiệu năng
            return ProcessOutcome.Scanned;
        }
        else
        {
            _logger.LogWarning("[FileWatcher] Đọc thất bại {filePath}: {error}", filePath, result.ErrorMessage);
            return ProcessOutcome.Failed;
        }
    }

    // [Thay đổi] FileConfidence 2 nhánh (shadow): tính + log 1 dòng (chỉ nhánh/điểm/domain, không có giá trị nhạy cảm).
    // Lỗi bất kỳ chỉ cảnh báo và trả null — đây là tín hiệu quan sát, không được làm hỏng luồng quét.
    private FileConfidenceResult? ComputeFileConfidence(string filePath, SensitivityLevel level, List<ScanMatch> matches, ScanTelemetry telemetry)
    {
        if (_fileConfidence == null)
        {
            return null;
        }

        try
        {
            var conf = _fileConfidence.Compute(level, matches, telemetry);
            _logger.LogInformation("[Conf] {filePath}: nhánh {branch}, conf={conf:0.000} (domain đoán: {domain}) — shadow: {verdict} (ngưỡng {level} {threshold:0.000})",
                filePath, conf.Branch, conf.Confidence, conf.Domain,
                conf.KeepAtLayer1 ? "đủ tin giữ Lớp 1" : "lẽ ra gửi LLM", level, conf.KeepThreshold);
            return conf;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Conf] {filePath}: không tính được FileConfidence: {error}", filePath, ex.Message);
            return null;
        }
    }

    // [Sửa lỗi] Watcher bị GC thu hồi: dừng theo dõi CHỦ ĐỘNG khi service dừng (thay vì chờ GC), huỷ các file đang chờ debounce
    // (chưa bắt đầu đọc) để không xử lý tiếp lúc DB sắp đóng. File ĐANG xử lý dở thì để chạy nốt — ResultStore tự bỏ qua khi đã đóng.
    /// <summary>Dừng theo dõi thư mục và huỷ các lần xử lý đang chờ.</summary>
    public void Dispose()
    {
        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher != null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

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

    // [Thay đổi] Cache file_state: đọc cache từ DB. Không có DB hoặc lỗi DB → coi như không có cache, quét bình thường
    // (lỗi lưu trữ không được làm dừng luồng quét — kiến trúc mục 10)
    private FileStateInfo? TryGetCached(string filePath)
    {
        if (_store == null)
        {
            return null;
        }

        try
        {
            return _store.TryGetFileState(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DB] Không đọc được cache {filePath}: {error}", filePath, ex.Message);
            return null;
        }
    }

    // [Thay đổi] Cache file_state: cập nhật size/mtime khi nội dung không đổi. Lỗi chỉ cảnh báo (hậu quả duy nhất: lần sau
    // tầng 1 không bỏ qua được, phải trích text lại để so hash).
    private void TryMarkUnchanged(string filePath, long size, DateTime mtimeUtc)
    {
        try
        {
            _store?.MarkUnchanged(filePath, size, mtimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DB] Không cập nhật được file_state {filePath}: {error}", filePath, ex.Message);
        }
    }

    // [Thay đổi] Ghi finding + scan_result: ghi 1 file vào DB. Lỗi DB (đĩa đầy, DB khoá...) chỉ log cảnh báo — theo kiến
    // trúc mục 10, lỗi lưu trữ KHÔNG được làm dừng luồng quét. Log chỉ có đường dẫn + thông báo lỗi SQLite, không có giá trị.
    // [Thay đổi] Cache file_state: nhận size/mtime đã chụp TRƯỚC khi đọc file (xem ProcessFile), không tự đọc FileInfo nữa
    // [Thay đổi] Hàng đợi llm_job: thêm llmPriority (null = không cần LLM)
    // [Thay đổi] Hạn ngạch: trả kết quả hàng đợi (None nếu không có DB / lỗi DB)
    // [Thay đổi] FileConfidence (shadow): thêm conf (null = không tính được)
    private JobEnqueueOutcome TrySave(string filePath, long size, DateTime mtimeUtc, string hash, double s1, string status, bool needsReview,
        string? labelsJson, List<FindingRecord> findings, int? llmPriority = null, bool needsOcr = false, FileConfidenceResult? conf = null)
    {
        if (_store == null)
        {
            return JobEnqueueOutcome.None;
        }

        try
        {
            // [Thay đổi] Merger mục 7.4 "Lưu vết": rules_ver lấy từ scanner đang dùng, merge_policy_ver = phiên bản hiện tại
            // (Pass 1 chưa có LLM nên S_final=S1, nhưng vẫn ghi để biết công thức nào sẽ áp dụng nếu sau này có LLM merge vào)
            return _store.SaveScan(new ScanRecord(filePath, size, mtimeUtc,
                hash, s1, status, needsReview, labelsJson, findings, llmPriority, needsOcr,
                _scanner.RulesVer, MergePolicyVersion.Current,
                conf?.Confidence, conf?.Branch.ToString())); // [Thay đổi] FileConfidence (shadow)
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[DB] Không ghi được kết quả {filePath}: {error}", filePath, ex.Message);
            return JobEnqueueOutcome.None;
        }
    }

    // [Thay đổi] Eligibility + P1/P2/P3: từ khoá P2 chỉ xét phần đường dẫn TÍNH TỪ thư mục đang theo dõi (tên thư mục cha của
    // watch-folder không phải tín hiệu). Chưa StartWatching (_watchFolder rỗng) → chỉ xét tên file.
    private EscalationDecision Escalate(RouteZone zone, int textLength, string filePath, string hash, bool needsOcr = false)
    {
        string relative = _watchFolder.Length > 0 ? Path.GetRelativePath(_watchFolder, filePath) : Path.GetFileName(filePath);
        return EscalationPolicy.Decide(zone, textLength, filePath, relative, hash, _escalation, needsOcr);
    }

    // [Thay đổi] Hạn ngạch: log kết quả tạo job (không log gì khi không cần LLM / job đã có)
    private void LogJobOutcome(string filePath, EscalationDecision decision, JobEnqueueOutcome outcome)
    {
        switch (outcome)
        {
            case JobEnqueueOutcome.Created:
                _logger.LogInformation("[Job] {filePath}: tạo job {group} ({reason})", filePath, decision.Group, decision.Reason);
                break;
            case JobEnqueueOutcome.Deferred:
                _logger.LogInformation("[Job] {filePath}: tạo job {group} nhưng HOÃN sang giờ sau — vượt hạn ngạch P1/giờ", filePath, decision.Group);
                break;
            case JobEnqueueOutcome.DroppedQuota:
                _logger.LogWarning("[Job] {filePath}: bỏ job {group} ({reason}) — vượt hạn ngạch ngày", filePath, decision.Group, decision.Reason);
                break;
            case JobEnqueueOutcome.DroppedQueueFull:
                _logger.LogWarning("[Job] {filePath}: bỏ job {group} ({reason}) — hàng đợi đầy", filePath, decision.Group, decision.Reason);
                break;
        }
    }

    // [Thay đổi] Ghi finding + scan_result: gộp chỗ khớp theo rule thành finding. Điểm = điểm rule trong S1 (nếu có), không
    // thì điểm candidate cao nhất. hard_rule/validator_passed = rule có validator CHECKSUM. location = vị trí chỗ khớp
    // đầu tiên. KHÔNG đưa giá trị khớp vào finding.
    private static List<FindingRecord> BuildFindings(List<ScanMatch> matches, FileScore? s1)
    {
        var ruleScores = s1?.Rules.ToDictionary(r => r.RuleKey, r => r.Score) ?? [];
        return matches
            .GroupBy(m => m.RuleId ?? m.PatternName)
            .Select(g =>
            {
                bool checksum = g.Any(m => m.Validated);
                double score = ruleScores.TryGetValue(g.Key, out double s) ? s : g.Max(m => m.Score);
                return new FindingRecord(g.Key, score, checksum ? true : null, checksum, g.Count(), $"offset:{g.Min(m => m.Index)}");
            })
            .ToList();
    }

    // [Thay đổi] Ghi finding + scan_result: nhãn cuối dạng JSON, VD {"level":"Confidential","domains":["PII","Finance"]}
    private static string LabelsJson(SensitivityLevel level, IEnumerable<string> domains) =>
        JsonSerializer.Serialize(new { level = level.ToString(), domains = domains.Distinct().ToArray() });

    // [Thay đổi] Nhóm D - "bỏ qua quét trùng khi nội dung không đổi": tính hash nội dung để so sánh/lưu trong
    // Dictionary. [Thay đổi] mục 4.12: dùng chung Storage.ContentHash — Worker LLM (Pass 2) cũng phải băm y hệt cách
    // này để so với file_hash đã lưu, nếu không sẽ luôn báo "file đã đổi" dù thật ra không đổi gì.
    private static string ComputeContentHash(string content) => Storage.ContentHash.Compute(content);

    // [Thay đổi] Hàm phụ mới: che giá trị nhạy cảm trước khi log, ví dụ "012345678901" -> "012******901"
    private static string Mask(string value)
    {
        // Chuỗi quá ngắn (<= 6 ký tự) thì che hết
        if (value.Length <= 6)
        {
            return "***";
        }

        // Giữ 3 ký tự đầu và 3 ký tự cuối, phần giữa thay bằng dấu *
        return value[..3] + new string('*', value.Length - 6) + value[^3..];
    }
}
