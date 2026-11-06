// [Thay đổi] File mới (kế hoạch tách lõi, bước 3): pipeline xử lý MỘT file — tra cache → đọc → băm → quét regex → phân loại →
// chấm S1 + Router + FileConfidence → ghi DB. Trước đây nằm trong FileWatcherService.ProcessFile cùng với FileSystemWatcher và debounce;
// chỉ phần theo dõi thư mục mới phụ thuộc hệ điều hành, còn pipeline này là logic thuần nên tách ra để dùng chung cho mọi nền tảng.
// QUAN TRỌNG: thân các hàm được DI CHUYỂN NGUYÊN VĂN từ FileWatcherService, không đổi logic (chỉ đổi chỗ gọi Mask và Escalate).
using DlpService.Classification;
using DlpService.Platform; // [Thay đổi] Tách lõi bước 4: IPathPolicy
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage; // Ghi finding + scan_result vào SQLite
using System.Collections.Concurrent;
using System.Text.Json;

namespace DlpService.Pipeline;

// [Thay đổi] Cache file_state: kết quả xử lý 1 file (để ScanExistingFiles đếm)
public enum ProcessOutcome
{
    /// <summary>Đã quét (và ghi DB nếu có).</summary>
    Scanned,
    /// <summary>Không đổi so với lần quét trước → bỏ qua.</summary>
    Unchanged,
    /// <summary>Đọc file thất bại.</summary>
    Failed
}

/// <summary>Xử lý 1 file: đọc, quét, phân loại và ghi kết quả. Gọi được từ nhiều luồng cùng lúc (việc giới hạn số luồng do bên gọi lo).</summary>
public sealed class FileProcessor
{
    private readonly FileReaderService _fileReader;
    private readonly RegexScanner _scanner; // scanner nhận từ ngoài, dùng để quét nội dung file
    private readonly ClassificationEngine _engine;
    private readonly ILogger _logger;
    private readonly RouterOptions _routerOptions; // ngưỡng định tuyến (đọc từ appsettings.json mục "Score" ở Worker)
    private readonly ResultStore? _store; // kho kết quả SQLite (null = không ghi)
    private readonly EscalationOptions _escalation; // eligibility + phân nhóm P1/P2/P3
    private readonly FileConfidenceScorer? _fileConfidence; // null = không nạp được model → bỏ qua

    // Đường dẫn của file TÍNH TỪ thư mục đang theo dõi (để xét từ khoá P2). Do FileWatcherService cung cấp vì chỉ nó biết các thư mục gốc.
    private readonly Func<string, string> _relativePathOf;

    // [Thay đổi] Tách lõi bước 4: quy tắc đường dẫn theo hệ điều hành (dùng khi xét đường dẫn loại trừ LLM)
    private readonly IPathPolicy _pathPolicy;

    // Hash nội dung lần quét gần nhất theo từng file: nhận ra nhiều sự kiện watcher rời rạc cùng trỏ tới 1 lần lưu file thực tế.
    // ConcurrentDictionary vì nhiều Task debounce và lượt quét khởi động có thể cùng xử lý.
    private readonly ConcurrentDictionary<string, string> _lastContentHash = new();

    public FileProcessor(FileReaderService fileReader, RegexScanner scanner, ClassificationEngine engine, ILogger logger,
        RouterOptions routerOptions, ResultStore? store, EscalationOptions escalation, FileConfidenceScorer? fileConfidence,
        Func<string, string> relativePathOf, IPathPolicy pathPolicy)
    {
        _pathPolicy = pathPolicy;
        _fileReader = fileReader;
        _scanner = scanner;
        _engine = engine;
        _logger = logger;
        _routerOptions = routerOptions;
        _store = store;
        _escalation = escalation;
        _fileConfidence = fileConfidence;
        _relativePathOf = relativePathOf;
    }

    // [Thay đổi] Tách ra từ ProcessAfterDebounceAsync: phần "đọc file -> kiểm tra trùng hash -> quét ->
    // phân loại -> log" không liên quan gì tới debounce/CancellationToken — tách riêng để dùng lại được ở cả
    // luồng sự kiện watcher (qua ProcessAfterDebounceAsync) LẪN ScanExistingFiles (quét khi khởi động, Nhóm D1),
    // tránh viết 2 bản logic dễ lệch nhau.
    // [Thay đổi] Cache file_state: trả ProcessOutcome thay cho void để ScanExistingFiles đếm được số file bỏ qua
    public ProcessOutcome ProcessFile(string filePath)
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
                // [Thay đổi] GĐ2 (R7): mức Information chỉ ghi TÊN rule + SỐ LẦN khớp (không vị trí, không giá trị dù đã che).
                // Chi tiết từng chỗ khớp xuống mức Debug — mặc định không bật khi chạy thật.
                var perRule = matches.GroupBy(m => m.PatternName).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}×{g.Count()}");
                _logger.LogInformation("[DLP] {filePath}: phát hiện {count} chỗ nhạy cảm: {rules}", filePath, matches.Count, string.Join(", ", perRule));
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    foreach (var m in matches)
                    {
                        // [Thay đổi] Bậc 3 #7: thêm điểm tin cậy ("—" nếu rule không khai confidence), để quan sát phân bố
                        // điểm thật mà chỉnh min_confidence của từng rule
                        _logger.LogDebug(" - {pattern} tại vị trí {index}: {masked} (tin cậy {confidence})",
                            m.PatternName, m.Index, LogMasking.Mask(m.Value), m.Confidence?.ToString("0.00") ?? "—");
                    }
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
    // watch-folder không phải tín hiệu). Chưa StartWatching → chỉ xét tên file.
    private EscalationDecision Escalate(RouteZone zone, int textLength, string filePath, string hash, bool needsOcr = false)
    {
        // [Thay đổi] Tách lõi bước 3: việc tìm thư mục gốc đang theo dõi thuộc FileWatcherService (nó giữ danh sách thư mục), nên nhận qua delegate
        string relative = _relativePathOf(filePath);
        return EscalationPolicy.Decide(zone, textLength, filePath, relative, hash, _escalation, needsOcr, _pathPolicy); // [Thay đổi] Tách lõi bước 4: + _pathPolicy
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
}
