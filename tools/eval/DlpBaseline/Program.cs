// [Thay đổi] File mới: công cụ đo BASELINE của DlpService (regex-only).
//  - Độ chính xác: so mức engine trả về với nhãn trong rawdata/<thư mục>/_metadata.jsonl -> precision/recall.
//  - Tốc độ: bấm giờ riêng từng bước trích text / quét regex / phân loại trên máy đang chạy.
// Gọi thẳng pipeline của service (FileReaderService -> RegexScanner -> ClassificationEngine), giống ProcessFile trong
// FileWatcherService (trừ bước hash SHA256 chống quét trùng + ghi log). Không cần service chạy, không sửa DlpService/.
using System.Diagnostics;
using System.Text;
using DlpService.Classification;
using DlpService.Reading;
using DlpService.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpBaseline;

// Không dùng top-level statements: DlpService cũng có class Program tự sinh -> trùng tên (cảnh báo CS0436)
internal static class BaselineRunner
{
    // Số file chạy "khởi động" trước khi đo: lần đầu .NET phải JIT + compile regex nên chậm bất thường
    private const int WarmupFiles = 20;

    // Bao nhiêu file thì in tiến độ 1 lần
    private const int ProgressEvery = 1000;

    // Category của rule được tính là "phát hiện PII" (chỉ số C) — khớp trường "category" trong rules/pii/*.json
    private static readonly HashSet<string> PiiCategories = new(StringComparer.OrdinalIgnoreCase) { "pii" };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        DlpTools.Shared.PowerThrottling.Disable(); // [Sửa lỗi đo] buổi 15: chạy nền bị Windows hạ xung → thời gian/file bị thổi phồng

        // [Thay đổi] Đo lại với Tika native: chọn bộ đọc (--extractor legacy|tika) TRƯỚC khi tạo FileReaderService
        args = DlpTools.Shared.ExtractorSwitch.Apply(args);
        var options = Options.Parse(args);
        if (options == null)
        {
            return 1;
        }
        if (!Directory.Exists(options.RawdataFolder))
        {
            Console.Error.WriteLine($"Không thấy thư mục dữ liệu: {options.RawdataFolder}");
            return 1;
        }

        // [Thay đổi] Ghi lại giờ BẮT ĐẦU (trước đây RunInfo lấy DateTime.Now lúc đã đo xong → tên file/báo cáo mang giờ kết thúc)
        var startedAt = DateTime.Now;

        // ---- Đọc nhãn ----
        var loaded = SampleLoader.Load(options.RawdataFolder);
        var samples = options.Limit is int limit ? SampleLoader.TakeStratified(loaded.Samples, limit) : loaded.Samples;
        if (samples.Count == 0)
        {
            Console.Error.WriteLine("Không đọc được mẫu nào từ _metadata.jsonl.");
            return 1;
        }
        Console.WriteLine($"Đọc {loaded.Samples.Count:N0} mẫu ({loaded.SkippedLines} dòng bị bỏ qua), đo {samples.Count:N0} mẫu x [{string.Join(", ", options.Formats)}]");

        // ---- Khởi tạo pipeline (bấm giờ riêng: đây là chi phí 1 lần lúc service khởi động) ----
        var logger = NullLogger.Instance; // logger "câm": ghi log làm lệch thời gian đo
        var sw = Stopwatch.StartNew();
        var patterns = new RuleLoader(logger).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
        double ruleLoadMs = sw.Elapsed.TotalMilliseconds;
        if (patterns.Count == 0)
        {
            Console.Error.WriteLine($"Không nạp được rule nào từ {Path.Combine(AppContext.BaseDirectory, "rules")}.");
            return 1;
        }

        sw.Restart();
        var reader = new FileReaderService();
        var scanner = new RegexScanner(patterns, minConfidence: options.MinConfidence); // [Thay đổi] Công thức Go
        var engine = new ClassificationEngine(logger);
        double initMs = sw.Elapsed.TotalMilliseconds;

        // [Thay đổi] FileConfidence 2 nhánh (shadow): cùng file hệ số service dùng; không nạp được → bỏ qua mục 5 của báo cáo
        FileConfidenceScorer? confidence = null;
        string confPath = Path.Combine(AppContext.BaseDirectory, "confidence", "file-confidence-v1.json");
        try
        {
            confidence = new FileConfidenceScorer(FileConfidenceModel.Load(confPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Không nạp được model FileConfidence ({confPath}): {ex.Message} — bỏ qua mục FileConfidence.");
        }

        // ---- Khởi động (không tính số liệu) ----
        sw.Restart();
        foreach (var (sample, format, path) in EnumerateFiles(samples, options.Formats).Take(WarmupFiles))
        {
            Evaluate(sample, format, path, reader, scanner, engine, confidence); // [Thay đổi] + FileConfidence
        }
        double warmupMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"Nạp {patterns.Count} pattern: {ruleLoadMs:N0} ms, khởi tạo: {initMs:N0} ms, khởi động {WarmupFiles} file: {warmupMs:N0} ms");

        // ---- Đo thật ----
        int expected = samples.Count * options.Formats.Count;
        int missing = expected - EnumerateFiles(samples, options.Formats).Count();
        var results = new List<FileResult>(expected);

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var loopSw = Stopwatch.StartNew();
        foreach (var (sample, format, path) in EnumerateFiles(samples, options.Formats))
        {
            results.Add(Evaluate(sample, format, path, reader, scanner, engine, confidence)); // [Thay đổi] + FileConfidence

            if (results.Count % ProgressEvery == 0)
            {
                var elapsed = loopSw.Elapsed;
                var remaining = elapsed / results.Count * (expected - missing - results.Count);
                // [Thay đổi] hh:mm:ss thay cho mm:ss — định dạng cũ bỏ mất phần giờ khi chạy lâu (VD máy ngủ giữa chừng)
                Console.WriteLine($"  {results.Count:N0}/{expected - missing:N0} file — đã chạy {elapsed:hh\\:mm\\:ss}, còn ~{remaining:hh\\:mm\\:ss}");
            }
        }
        double wallMs = loopSw.Elapsed.TotalMilliseconds;
        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (results.Count == 0)
        {
            Console.Error.WriteLine("Không có file nào để đo (kiểm tra docx_path/pdf_path trong _metadata.jsonl).");
            return 1;
        }

        // ---- Báo cáo ----
        var run = new RunInfo(
            startedAt, options.RawdataFolder, loaded.Samples.Count, loaded.SkippedLines, samples.Count, options.Formats,
            missing, patterns.Count, ruleLoadMs, initMs, warmupMs, wallMs, allocatedBytes,
            Process.GetCurrentProcess().PeakWorkingSet64, scanner.MinConfidence); // [Thay đổi] Công thức Go: + ngưỡng

        Directory.CreateDirectory(options.OutFolder);
        // [Thay đổi] Đo lại với Tika native: thêm tên bộ đọc vào tên file kết quả để không lẫn 2 lượt
        string stamp = run.StartedAt.ToString("yyyyMMdd-HHmmss") + "-" + DlpTools.Shared.ExtractorSwitch.Name;
        string csvPath = Path.GetFullPath(Path.Combine(options.OutFolder, $"baseline-{stamp}.csv"));
        string mdPath = Path.GetFullPath(Path.Combine(options.OutFolder, $"baseline-{stamp}.md"));

        Report.WriteCsv(csvPath, results);

        // [Thay đổi] S1 giai đoạn 2: chọn ngưỡng trên train, kiểm tra trên test; lưu ngưỡng ra JSON cho giai đoạn 3
        var tuning = S1Tuning.Tune(results);
        string thresholdPath = Path.GetFullPath(Path.Combine(options.OutFolder, $"s1-thresholds-{stamp}.json"));
        File.WriteAllText(thresholdPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            internal_threshold = tuning.Thresholds.Internal,
            confidential_threshold = tuning.Thresholds.Confidential,
            restricted_threshold = tuning.Thresholds.Restricted,
            met_recall_constraint = tuning.MetRecallConstraint,
            required_recall = tuning.RequiredRecall,
            train_files = tuning.TrainFiles,
            source_csv = csvPath
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        // [Thay đổi] Router ba vùng: thêm mục 4 của báo cáo
        string markdown = Report.BuildMarkdown(run, results, csvPath) + Report.BuildS1Section(tuning, thresholdPath)
            + RouterEval.BuildSection(results)
            + ConfidenceEval.BuildSection(results, confidence?.Version ?? ""); // [Thay đổi] FileConfidence 2 nhánh (shadow)
        File.WriteAllText(mdPath, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Console.WriteLine();
        Console.WriteLine(markdown);
        Console.WriteLine($"Báo cáo: {mdPath}");
        DlpTools.Shared.ExtractorSwitch.Shutdown(); // [Thay đổi] huỷ isolate Tika trên luồng chính
        return 0;
    }

    // Liệt kê các file cần đo (mẫu x định dạng), bỏ qua file không tồn tại (VD hr__internal thiếu 1 số pdf)
    private static IEnumerable<(Sample Sample, string Format, string Path)> EnumerateFiles(List<Sample> samples, List<string> formats)
    {
        foreach (var sample in samples)
        {
            foreach (var format in formats)
            {
                string? path = format == "docx" ? sample.DocxPath : sample.PdfPath;
                if (path != null && File.Exists(path))
                {
                    yield return (sample, format, path);
                }
            }
        }
    }

    // Chạy 1 file qua đúng các bước của FileWatcherService.ProcessFile, bấm giờ từng bước
    // [Thay đổi] FileConfidence: nhận thêm scorer (null = không tính)
    private static FileResult Evaluate(Sample sample, string format, string path, FileReaderService reader, RegexScanner scanner, ClassificationEngine engine,
        FileConfidenceScorer? confidence)
    {
        long size = new FileInfo(path).Length;

        // Bước 1: trích text
        var sw = Stopwatch.StartNew();
        var read = reader.TryReadFile(path);
        double extractMs = sw.Elapsed.TotalMilliseconds;

        if (!read.Success)
        {
            // Service gặp file đọc lỗi thì chỉ ghi cảnh báo, không phân loại -> coi như Public
            return new FileResult(sample, format, size, false, read.ErrorMessage, SensitivityLevel.Public, false, 0, 0, "", extractMs, 0, 0);
        }
        string content = read.Content ?? string.Empty;

        // Bước 2: quét regex
        sw.Restart();
        var scan = scanner.Scan(content); // [Thay đổi] Log timeout: Scan() trả ScanResult
        var matches = scan.Matches;
        double scanMs = sw.Elapsed.TotalMilliseconds;

        // Bước 3: phân loại — giống ProcessFile: không có chỗ khớp nào thì "sạch" (Public), không gọi engine
        var level = SensitivityLevel.Public;
        double classifyMs = 0;
        if (matches.Count > 0)
        {
            sw.Restart();
            level = engine.Classify(new ScanContext { FilePath = path, Content = content, Matches = matches }).Level;
            classifyMs = sw.Elapsed.TotalMilliseconds;
        }

        bool piiHit = matches.Any(m => m.Category != null && PiiCategories.Contains(m.Category));

        // Chỉ lưu MÃ rule + số lần khớp, không lưu giá trị khớp (CCCD, số tài khoản...)
        string rulesFired = string.Join(';', matches
            .GroupBy(m => m.RuleId ?? m.PatternName)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}:{g.Count()}"));

        // [Thay đổi] Log timeout: ghi tên rule bị timeout. Mức dự đoán vẫn tính từ các chỗ khớp tìm được (để đo), nhưng
        // báo cáo đếm riêng số file quét chưa trọn — service thật sẽ KHÔNG gắn mức cho những file này.
        string timedOutRules = string.Join(';', scan.TimedOutPatterns);

        // [Thay đổi] S1 giai đoạn 2: tính S1 bằng ĐÚNG code của service (FileScorer), không tính vào thời gian phân loại
        var s1 = FileScorer.Compute(matches, content.Length);

        // [Thay đổi] FileConfidence 2 nhánh: ĐÚNG như service (domain tự đoán) + bản dùng domain thật để so — không tính vào thời gian
        var conf = confidence?.Compute(level, matches, scan.Telemetry);
        var confTrueDomain = confidence?.Compute(level, matches, scan.Telemetry, sample.Domain);
        return new FileResult(sample, format, size, true, null, level, piiHit, content.Length, matches.Count, rulesFired, extractMs, scanMs, classifyMs, timedOutRules, s1,
            conf, confTrueDomain, scan.Telemetry); // [Thay đổi] Chẩn đoán nhánh B: + counter
    }
}
