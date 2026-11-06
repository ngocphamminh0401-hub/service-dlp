// [Thay đổi] Thêm namespace theo folder Watching/ và using để dùng FileReaderService (nay nằm ở namespace Reading)
using DlpService.Classification;
using DlpService.Pipeline; // [Thay đổi] Tách lõi bước 3: FileProcessor, ProcessOutcome
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
    // Danh sách này vẫn là tham chiếu mạnh nối liền Worker → FileWatcherService → các FileSystemWatcher (xem lưu ý ở trên).
    private readonly List<FileSystemWatcher> _watchers = [];

    // [Thay đổi] shared-core: các thư mục gốc đang theo dõi — OnWatcherError biết thư mục nào cần quét bù khi tràn buffer,
    // Escalate biết tính đường dẫn tương đối theo thư mục gốc nào. Mọi truy cập đều giữ khoá _watchersLock.
    private readonly List<string> _watchFolders = [];
    private readonly Lock _watchersLock = new();

    // [Thay đổi] shared-core: so sánh đường dẫn theo hệ điều hành — Windows không phân biệt hoa/thường, Linux có
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
        _logger = logger;
        // [Thay đổi] Tách lõi bước 3: các giá trị mặc định (Router ba vùng, eligibility) giữ nguyên như cũ; relativePathOf = hàm tìm thư mục gốc ở dưới
        _processor = new FileProcessor(_fileReader, scanner, engine, logger,
            routerOptions ?? new RouterOptions(), store, escalation ?? new EscalationOptions(), fileConfidence,
            RelativeToWatchRoot);
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

    // [Thay đổi] Tách lõi bước 3: đường dẫn của file TÍNH TỪ thư mục gốc đang theo dõi, để xét từ khoá P2. Chuyển từ thân Escalate cũ sang
    // đây vì chỉ lớp này giữ danh sách thư mục gốc. Nhiều thư mục gốc → lấy thư mục DÀI NHẤT chứa file (thư mục lồng nhau thì chọn cái sâu
    // nhất); không thuộc thư mục nào (hoặc chưa StartWatching) → chỉ lấy tên file.
    private string RelativeToWatchRoot(string filePath)
    {
        string? root = null;
        lock (_watchersLock)
        {
            foreach (var folder in _watchFolders)
            {
                string prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
                if (filePath.StartsWith(prefix, PathComparison) && (root == null || folder.Length > root.Length))
                {
                    root = folder;
                }
            }
        }
        return root != null ? Path.GetRelativePath(root, filePath) : Path.GetFileName(filePath);
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

        // [Thay đổi] Nhóm D3: lưu lại thư mục và dùng field thay cho biến cục bộ "var watcher"
        var watcher = new FileSystemWatcher(folderPath)
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
            watcher.Filters.Add("*" + extension);
        }
        watcher.Created += OnFileChanged;
        watcher.Changed += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        // [Thay đổi] Nhóm D3: nghe sự kiện Error — nếu không đăng ký, lỗi tràn buffer/mất thư mục trôi đi âm thầm
        watcher.Error += OnWatcherError;

        // [Thay đổi] shared-core: đăng ký watcher + thư mục vào danh sách TRƯỚC khi bật sự kiện, để Escalate/OnWatcherError
        // (chạy trên luồng khác ngay khi có sự kiện) đã thấy thư mục này
        lock (_watchersLock)
        {
            _watchers.Add(watcher);
            _watchFolders.Add(folderPath);
        }
        watcher.EnableRaisingEvents = true;
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
                outcome = _processor.ProcessFile(filePath);
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

        // [Thay đổi] shared-core: sender chính là watcher bị lỗi → chỉ quét bù đúng thư mục của nó (không quét lại thư mục khác)
        string watchFolder = (sender as FileSystemWatcher)?.Path ?? string.Empty;

        if (ex is InternalBufferOverflowException)
        {
            _logger.LogWarning("[FileWatcher] Buffer tràn, có thể đã mất sự kiện — quét lại toàn thư mục {folder}", watchFolder);

            // [Thay đổi] Bọc try/catch quanh lượt quét bù: OnWatcherError chạy trên luồng ThreadPool, exception
            // không được bắt ở đây sẽ làm SẬP cả service. Bắt lại -> ghi log -> service vẫn tiếp tục theo dõi.
            try
            {
                ScanExistingFiles(watchFolder);
            }
            catch (Exception scanEx)
            {
                _logger.LogError(scanEx, "[FileWatcher] Quét bù sau khi tràn buffer thất bại {folder}", watchFolder);
            }
        }
        else
        {
            _logger.LogError(ex, "[FileWatcher] Watcher gặp lỗi, có thể đã ngừng theo dõi {folder}", watchFolder);
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
                _processor.ProcessFile(filePath);
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

    // [Sửa lỗi] Watcher bị GC thu hồi: dừng theo dõi CHỦ ĐỘNG khi service dừng (thay vì chờ GC), huỷ các file đang chờ debounce
    // (chưa bắt đầu đọc) để không xử lý tiếp lúc DB sắp đóng. File ĐANG xử lý dở thì để chạy nốt — ResultStore tự bỏ qua khi đã đóng.
    /// <summary>Dừng theo dõi thư mục và huỷ các lần xử lý đang chờ.</summary>
    public void Dispose()
    {
        // [Thay đổi] shared-core: tắt TẤT CẢ watcher (mỗi thư mục 1 cái)
        FileSystemWatcher[] toDispose;
        lock (_watchersLock)
        {
            toDispose = [.. _watchers];
            _watchers.Clear();
        }
        foreach (var watcher in toDispose)
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

}
