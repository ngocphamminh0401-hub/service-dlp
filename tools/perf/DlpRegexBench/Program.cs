// [Thay đổi] File mới (Benchmark T04): đo RegexScanner theo từng pattern — xác nhận/bác bỏ giả thuyết A1–A6 của Code Review.
//   DlpRegexBench rules <dataset> [--sets D-clean,D-kw] [--rawdata <dir> --raw-sample N] [--runs 5] [--warmup 30] [--max-scan-seconds 10] [--out <thư mục>]
//   DlpRegexBench fingerprint <dataset> [--sets ...] [--rawdata <dir> --raw-sample N] --out <file.csv>
// Cách tách chi phí (không sửa Core):
//   full_ms   = RegexScanner(mọi pattern).Scan(content)                       — đúng như service
//   fixed_ms  = RegexScanner([]).Scan(content)                                 — phần chạy với MỌI file dù không có pattern nào:
//               BuildTelemetry (DocContextProbe 10 từ khoá + CountUnstructuredNumericHits với 0 chỗ khớp + đếm byte UTF-8)
//   scan_ms   = RegexScanner([p]).Scan(content)  − fixed_ms                     — chi phí riêng của pattern p
//   regex_ms  = p.Regex.Count(content)                                          — riêng engine regex (Count không tạo object Match)
//   post_ms   = scan_ms − regex_ms                                              — lọc từ khoá, Substring, validator, telemetry của rule
// Xấp xỉ: CountUnstructuredNumericHits là O(N×M) theo số chỗ khớp M nên phần đó KHÔNG cộng tuyến tính giữa các pattern — vì vậy báo cáo
// so Σ(scan_ms) + fixed_ms với full_ms để thấy độ lệch (lệch lớn ở file nhiều số + nhiều chỗ khớp chính là dấu hiệu của A4).
// Nội dung file chỉ nằm trong RAM; CSV/báo cáo chỉ có id file, tên rule và số đo.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DlpService.Classification;
using DlpService.Reading;
using DlpService.Scanning;
using DlpTools.Perf;
using Microsoft.Extensions.Logging.Abstractions;
using static DlpTools.Perf.BenchCommon;

namespace DlpRegexBench;

internal static class Program
{
    private static TimeSpan maxScan = RegexScanner.DefaultMaxScanTime;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? mode = args.FirstOrDefault();
        string? dataset = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
        var files = SelectFiles(dataset, Arg(args, "--sets"), Arg(args, "--rawdata"), int.Parse(Arg(args, "--raw-sample") ?? "0", Inv));
        // [Thay đổi] P0 (giai đoạn 2): thêm 3 chế độ thí nghiệm biến thể regex (xem Variants.cs). overhead/startup không cần file.
        bool needsFiles = mode is "rules" or "fingerprint" or "variants";
        if (mode is not ("rules" or "fingerprint" or "variants" or "overhead" or "startup") || (needsFiles && files.Count == 0))
        {
            Console.Error.WriteLine("Cách dùng:\n  DlpRegexBench rules <dataset> [--sets ...] [--rawdata <dir> --raw-sample N] [--runs 5] [--out <thư mục>]\n" +
                                    "  DlpRegexBench fingerprint <dataset> [--sets ...] [--rawdata <dir> --raw-sample N] --out <file.csv>\n" +
                                    "  DlpRegexBench variants <dataset> [--sets ...] [--rawdata <dir> --raw-sample N] [--runs 5] [--warmup 1] [--out <thư mục>]\n" +
                                    "  DlpRegexBench overhead [--reps 2000] [--out <thư mục>]\n" +
                                    "  DlpRegexBench startup --options compiled|interpreted");
            return 1;
        }

        string rulesDir = Path.Combine(AppContext.BaseDirectory, "rules");
        var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(rulesDir);
        var reader = new FileReaderService();
        // [Thay đổi] --max-scan-seconds: ngân sách thời gian quét 1 file (mặc định = service, 10 s). Đặt lớn (VD 600) để đo chi phí THẬT
        // của file chạm trần — với trần 10 s, CSV lớn chỉ cho ra "≈ 10 s", không đo được độ dốc theo số dòng (R3).
        maxScan = TimeSpan.FromSeconds(double.Parse(Arg(args, "--max-scan-seconds") ?? RegexScanner.DefaultMaxScanTime.TotalSeconds.ToString(Inv), Inv));
        if (mode == "variants")
        {
            return VariantsBench.Run(files, patterns, reader, int.Parse(Arg(args, "--runs") ?? "5", Inv), int.Parse(Arg(args, "--warmup") ?? "1", Inv), Arg(args, "--out") ?? Path.Combine("tools", "perf", "baseline", "variants"));
        }
        if (mode == "overhead")
        {
            return VariantsBench.Overhead(patterns, int.Parse(Arg(args, "--reps") ?? "2000", Inv), Arg(args, "--out") ?? Path.Combine("tools", "perf", "baseline", "variants"));
        }
        if (mode == "startup")
        {
            return VariantsBench.Startup(patterns, Arg(args, "--options") ?? "compiled");
        }
        return mode == "rules"
            ? Rules(files, patterns, reader, int.Parse(Arg(args, "--runs") ?? "5", Inv), int.Parse(Arg(args, "--warmup") ?? "30", Inv), Arg(args, "--out") ?? Path.Combine("tools", "perf", "baseline", "regex"))
            : Fingerprint(files, patterns, reader, rulesDir, Arg(args, "--out") ?? "fingerprint.csv");
    }

    // ============================================================ rules ============================================================

    private sealed record FileRow(BenchFile File, int Chars, double FullMs, double FullCv, double FullCpuMs, long FullAlloc, int Gen0, int Gen1, int Gen2,
        double FixedMs, double SumScanMs, double AvoidableMs, int Matches, bool Complete);

    private sealed record RuleRow(BenchFile File, int Chars, string RuleKey, SensitivePattern First, double RegexMs, double ScanMs, int RawMatches,
        int Survivors, long Alloc, int AvoidablePatterns, int PatternCount);

    private sealed record PatternRow(BenchFile File, int Chars, SensitivePattern Pattern, double RegexMs, double ScanMs, int RawMatches, int Survivors,
        long Alloc, bool HasKeyword, bool Avoidable);

    private static int Rules(List<BenchFile> files, List<SensitivePattern> patterns, FileReaderService reader, int runs, int warmup, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var full = new RegexScanner(patterns, maxScanTime: maxScan);
        var empty = new RegexScanner([], maxScanTime: maxScan);
        // Mỗi rule (khoá RuleId ?? Name, như BuildTelemetry) 1 scanner riêng
        var rules = patterns.GroupBy(p => p.RuleId ?? p.Name).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Patterns: g.ToList(), Scanner: new RegexScanner(g.ToList(), maxScanTime: maxScan))).ToList();
        var fileRows = new List<FileRow>();
        var patternRows = new List<PatternRow>();
        var ruleRows = new List<RuleRow>();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
        using var perRun = new StreamWriter(Path.Combine(outDir, $"regex_benchmark-{stamp}.csv"), false, new UTF8Encoding(false));
        perRun.WriteLine("dataset,file_id,chars,rule_id,pattern_idx,regex_ms,post_ms,raw_matches,survivors,alloc_bytes,run");

        // Làm nóng toàn cục: JIT + regex compiled chạy lần đầu chậm bất thường
        full.Scan("khởi động 012345678901 CCCD");

        foreach (var (file, fi) in files.Select((f, i) => (f, i)))
        {
            var read = reader.TryReadFile(file.Path);
            string content = read.Content ?? "";
            if (content.Length == 0)
            {
                Console.WriteLine($"[{fi + 1}/{files.Count}] {file.Id}: không có nội dung ({read.IncompleteReason ?? read.ErrorMessage}) — bỏ qua");
                continue;
            }

            // Toàn bộ scanner: 1 lần làm nóng + runs lần đo
            ScanResult? lastFull = null;
            // [Thay đổi] File đầu tiên: làm nóng nhiều lần để JIT phân tầng (tier-0 → tier-1, ~30 lần gọi) xong TRƯỚC khi đo — trước đây file
            // đầu chậm hơn file cùng cỡ ~50 %. Các file sau chỉ cần 1 lần (nạp nội dung vào cache CPU).
            for (int w = 0; w < (fi == 0 ? warmup : 1); w++)
            {
                full.Scan(content);
                if (fi == 0) foreach (var r in rules) r.Scanner.Scan(content);
            }
            var fullSamples = Enumerable.Range(0, runs).Select(_ => Measure(() => lastFull = full.Scan(content))).ToList();
            empty.Scan(content);
            double fixedMs = Median(Enumerable.Range(0, runs).Select(_ => Measure(() => empty.Scan(content)).WallMs));

            double sumScan = 0, avoidable = 0;
            // [Thay đổi] Đo Scan theo RULE (1 scanner gồm mọi pattern con của rule): BuildTelemetry dò từ khoá primary 1 lần / RULE, nếu đo
            // từng pattern riêng thì phần đó bị đếm lặp theo số pattern con và Σ vượt xa full. Regex.Count vẫn đo theo từng PATTERN.
            foreach (var (ruleKey, rulePatterns, ruleScanner) in rules)
            {
                var prs = new List<PatternRow>();
                foreach (var p in rulePatterns)
                {
                    // Rule có từ khoá ở đâu đó trong file không (hợp mọi nhóm từ khoá — cách hiểu RỘNG nhất → "có thể bỏ" là cận DƯỚI)
                    bool hasKw = p.Keywords.Concat(p.PrimaryKeywords).Concat(p.SecondaryKeywords)
                        .Any(k => k.Length > 0 && content.Contains(k, StringComparison.OrdinalIgnoreCase));
                    // A1: pattern context_required mà cả file không có từ khoá → không chỗ khớp nào sống sót được, regex chạy "phí"
                    bool avoid = p.ContextRequired && !hasKw;
                    int raw = SafeCount(p.Regex, content); // cũng là lần làm nóng
                    double regexMs = Median(Enumerable.Range(0, runs).Select(_ => Measure(() => SafeCount(p.Regex, content)).WallMs));
                    prs.Add(new PatternRow(file, content.Length, p, regexMs, 0, raw, 0, 0, hasKw, avoid));
                    perRun.WriteLine(string.Create(Inv, $"{file.Set},{file.Id},{content.Length},{ruleKey},{p.PatternIdx},{F(regexMs)},,{raw},,,median"));
                    if (avoid) avoidable += regexMs;
                }

                ruleScanner.Scan(content); // làm nóng cho cặp (file, rule)
                int survivors = 0;
                var scanMs = new List<double>();
                var allocs = new List<double>();
                double regexSum = prs.Sum(x => x.RegexMs);
                for (int run = 0; run < runs; run++)
                {
                    var smp = Measure(() => survivors = ruleScanner.Scan(content).Matches.Count);
                    double net = Math.Max(0, smp.WallMs - fixedMs);
                    scanMs.Add(net);
                    allocs.Add(smp.AllocBytes);
                    perRun.WriteLine(string.Create(Inv,
                        $"{file.Set},{file.Id},{content.Length},{ruleKey},*,{F(regexSum)},{F(Math.Max(0, net - regexSum))},{prs.Sum(x => Math.Max(0, x.RawMatches))},{survivors},{smp.AllocBytes},{run + 1}"));
                }
                double ruleScan = Median(scanMs);
                ruleRows.Add(new RuleRow(file, content.Length, ruleKey, rulePatterns[0], regexSum, ruleScan, prs.Sum(x => Math.Max(0, x.RawMatches)), survivors,
                    (long)Median(allocs), prs.Count(x => x.Avoidable), prs.Count));
                patternRows.AddRange(prs);
                sumScan += ruleScan;
            }

            var walls = fullSamples.Select(s => s.WallMs).ToList();
            var fr = new FileRow(file, content.Length, Median(walls), Cv(walls), Median(fullSamples.Select(s => s.CpuMs)),
                (long)Median(fullSamples.Select(s => (double)s.AllocBytes)), fullSamples.Sum(s => s.Gen0), fullSamples.Sum(s => s.Gen1), fullSamples.Sum(s => s.Gen2),
                fixedMs, sumScan, avoidable, lastFull!.Matches.Count, lastFull.IsComplete);
            fileRows.Add(fr);
            Console.WriteLine(string.Create(Inv,
                $"[{fi + 1}/{files.Count}] {file.Id}: {content.Length:N0} ký tự · full {fr.FullMs:0.0} ms (CV {fr.FullCv:P0}) · fixed {fixedMs:0.0} · Σrule {sumScan:0.0} · phí (A1) {avoidable:0.0} · alloc {fr.FullAlloc / 1048576.0:0.0} MB · {fr.Matches} khớp"));
        }

        WriteFileCsv(Path.Combine(outDir, $"regex_files-{stamp}.csv"), fileRows);
        string report = Report(fileRows, ruleRows, patternRows, runs);
        string mdPath = Path.Combine(outDir, $"regex-report-{stamp}.md");
        File.WriteAllText(mdPath, report, new UTF8Encoding(false));
        Console.WriteLine($"Đã ghi {outDir}/regex_benchmark-{stamp}.csv, regex_files-{stamp}.csv, regex-report-{stamp}.md");
        return 0;
    }

    // Regex quá thời gian (1 s / lần tìm) → -1, không làm hỏng cả lượt đo
    private static int SafeCount(Regex regex, string content)
    {
        try
        {
            return regex.Count(content);
        }
        catch (RegexMatchTimeoutException)
        {
            return -1;
        }
    }

    private static void WriteFileCsv(string path, List<FileRow> rows)
    {
        var sb = new StringBuilder("dataset,file_id,chars,full_ms,full_cv,full_cpu_ms,full_alloc_bytes,gen0,gen1,gen2,fixed_ms,sum_pattern_ms,reconstructed_ms,reconstruct_err,avoidable_a1_ms,matches,scan_complete\n");
        foreach (var r in rows)
        {
            double rec = r.SumScanMs + r.FixedMs;
            sb.Append(string.Create(Inv,
                $"{r.File.Set},{r.File.Id},{r.Chars},{F(r.FullMs)},{F(r.FullCv, "0.0000")},{F(r.FullCpuMs)},{r.FullAlloc},{r.Gen0},{r.Gen1},{r.Gen2},{F(r.FixedMs)},{F(r.SumScanMs)},{F(rec)},{F(r.FullMs > 0 ? (rec - r.FullMs) / r.FullMs : 0, "0.0000")},{F(r.AvoidableMs)},{r.Matches},{(r.Complete ? 1 : 0)}\n"));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Report(List<FileRow> files, List<RuleRow> rules, List<PatternRow> patterns, int runs)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Benchmark RegexScanner theo rule / pattern (T04) — {DateTime.Now:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine("| Mục | Giá trị |\n|---|---|");
        md.Append(Environment());
        md.AppendLine($"| Số lần đo / phép đo | {runs} (+1 làm nóng), báo cáo median |");
        md.AppendLine($"| Rule / pattern | {rules.Select(r => r.RuleKey).Distinct().Count()} / {patterns.Select(p => p.Pattern).Distinct().Count()} |");
        md.AppendLine();
        md.AppendLine("## 1. Theo file");
        md.AppendLine();
        md.AppendLine("Các cột: full = `Scan` đủ rule; fixed = scanner rỗng (telemetry cố định); Σrule = tổng chi phí riêng của từng rule; lệch = (Σrule + fixed − full) / full " +
                      "(AC T04: ±15 %); regex thuần = Σ `Regex.Count` / full; A1 = Σ regex của pattern `context_required` mà cả file không có từ khoá của rule / full.");
        md.AppendLine();
        md.AppendLine("| Bộ | File | Ký tự | full ms | CV | ms / 100k ký tự | fixed ms | Σrule ms | lệch | regex thuần % | A1 % | alloc MB | Gen0/1/2 | khớp |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var f in files)
        {
            double regexSum = patterns.Where(p => p.File == f.File).Sum(p => p.RegexMs);
            double rec = f.SumScanMs + f.FixedMs;
            md.AppendLine(string.Create(Inv,
                $"| {f.File.Set} | {f.File.Id} | {f.Chars:N0} | {f.FullMs:0.0} | {f.FullCv:P0} | {f.FullMs * 100_000 / Math.Max(1, f.Chars):0.0} | {f.FixedMs:0.0} | {f.SumScanMs:0.0} | {(rec - f.FullMs) / Math.Max(1e-9, f.FullMs):P0} | {regexSum / Math.Max(1e-9, f.FullMs):P0} | {f.AvoidableMs / Math.Max(1e-9, f.FullMs):P0} | {f.FullAlloc / 1048576.0:0.0} | {f.Gen0}/{f.Gen1}/{f.Gen2} | {f.Matches}{(f.Complete ? "" : " ⚠ chưa trọn")} |"));
        }
        md.AppendLine();
        md.AppendLine("## 2. Top 20 rule tốn nhất (tổng chi phí riêng trên mọi file)");
        md.AppendLine();
        md.AppendLine("| # | Rule | Pattern | Tổng ms | regex thuần ms | hậu xử lý ms | ms / 100k ký tự (TB) | Khớp thô | Sống sót | alloc MB | pattern \"phí\" (A1) |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        int rank = 0;
        foreach (var t in rules.GroupBy(r => r.RuleKey).Select(g => new
                 {
                     Key = g.Key, Patterns = g.First().PatternCount, Total = g.Sum(x => x.ScanMs), Regex = g.Sum(x => Math.Min(x.RegexMs, x.ScanMs)),
                     PerChar = g.Average(x => x.ScanMs * 100_000 / Math.Max(1, x.Chars)), Raw = g.Sum(x => x.RawMatches), Surv = g.Sum(x => x.Survivors),
                     Alloc = g.Sum(x => x.Alloc), Avoid = g.Sum(x => x.AvoidablePatterns)
                 }).OrderByDescending(x => x.Total).Take(20))
        {
            md.AppendLine(string.Create(Inv,
                $"| {++rank} | {t.Key} | {t.Patterns} | {t.Total:0.0} | {t.Regex:0.0} | {t.Total - t.Regex:0.0} | {t.PerChar:0.00} | {t.Raw} | {t.Surv} | {t.Alloc / 1048576.0:0.0} | {t.Avoid} |"));
        }
        md.AppendLine();
        md.AppendLine("## 3. Top 20 pattern theo thời gian engine regex (`Regex.Count`)");
        md.AppendLine();
        md.AppendLine("| # | Rule | Pattern idx | regex ms (tổng) | ms / 100k ký tự (TB) | Khớp thô | context_required | số file \"phí\" (A1) | Regex (60 ký tự đầu) |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        rank = 0;
        foreach (var t in patterns.GroupBy(p => p.Pattern).Select(g => new
                 {
                     P = g.Key, Total = g.Sum(x => x.RegexMs), PerChar = g.Average(x => x.RegexMs * 100_000 / Math.Max(1, x.Chars)),
                     Raw = g.Sum(x => Math.Max(0, x.RawMatches)), Timeouts = g.Count(x => x.RawMatches < 0), Avoid = g.Count(x => x.Avoidable)
                 }).OrderByDescending(x => x.Total).Take(20))
        {
            string rx = t.P.Regex.ToString();
            rx = (rx.Length > 60 ? rx[..60] + "…" : rx).Replace("|", "\\|").Replace("`", "'");
            md.AppendLine(string.Create(Inv,
                $"| {++rank} | {t.P.RuleId ?? t.P.Name} | {t.P.PatternIdx} | {t.Total:0.0} | {t.PerChar:0.00} | {t.Raw}{(t.Timeouts > 0 ? $" (timeout {t.Timeouts})" : "")} | {(t.P.ContextRequired ? "có" : "")} | {t.Avoid}/{files.Count} | `{rx}` |"));
        }
        md.AppendLine();
        md.AppendLine("## 4. Đọc kết quả (theo kế hoạch §7)");
        md.AppendLine();
        md.AppendLine("- **A1** (regex chạy khi không có từ khoá): cột A1 % ≥ 10 % ở bộ D-clean/D-raw → xác nhận; < 3 % → bác bỏ. Lưu ý: bỏ regex của pattern này sẽ làm đổi " +
                      "`validatorRejectedSpans` → có thể đổi `tel_unstructured_numeric_hits` → guard T03 phải bằng tuyệt đối.");
        md.AppendLine("- **A2/A3** (dò từ khoá, Substring): hậu xử lý ms lớn so với regex thuần ở D-kw/D-match. Tách đúng hàm cần trace (T11).");
        md.AppendLine("- **A4** (O(N×M)): ở D-csv, so ms / 100k ký tự giữa các bậc số dòng — tăng theo số dòng (thay vì gần như hằng) là dấu hiệu siêu tuyến tính; tách hàm bằng trace (T11).");
        md.AppendLine("- **A5** (Match giữ trong bộ nhớ): alloc MB và Gen0/1 tăng theo số chỗ khớp ở D-match.");
        md.AppendLine("- **A6** (timeout): cột khớp có \"⚠ chưa trọn\", hoặc pattern có \"timeout\" ở bảng 3.");
        md.AppendLine("- Đây là số đo 1 luồng trên máy đang chạy; chỉ số trên máy bench (kế hoạch §4) mới dùng làm baseline.");
        return md.ToString();
    }

    // ============================================================ fingerprint ============================================================

    // Guard phân loại cho dataset tổng hợp (DlpBaseline chỉ đọc được rawdata có nhãn): mọi thứ ảnh hưởng tới kết luận của service cho từng file.
    private static int Fingerprint(List<BenchFile> files, List<SensitivePattern> patterns, FileReaderService reader, string rulesDir, string outFile)
    {
        var scanner = new RegexScanner(patterns, maxScanTime: maxScan);
        var engine = new ClassificationEngine(NullLogger.Instance, rulesDir);
        var sb = new StringBuilder("file,dataset,chars,read_incomplete,level,match_count,rules_fired,scan_complete,s1_internal,s1_confidential,s1_restricted," +
                                   "tel_proximity_reject,tel_level_gate_downgrade,tel_kw_no_match_primary,tel_unstructured_numeric_hits,tel_doc_context,tel_identifier_volume_near_gate\n");
        foreach (var file in files)
        {
            var read = reader.TryReadFile(file.Path);
            string content = read.Content ?? "";
            var scan = scanner.Scan(content);
            var level = scan.Matches.Count > 0
                ? engine.Classify(new ScanContext { FilePath = file.Path, Content = content, Matches = scan.Matches }).Level
                : SensitivityLevel.Public;
            var s1 = FileScorer.Compute(scan.Matches, content.Length);
            string rules = string.Join(";", scan.Matches.GroupBy(m => m.RuleId ?? m.PatternName).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}"));
            var t = scan.Telemetry;
            sb.Append(string.Create(Inv,
                $"{file.Id},{file.Set},{content.Length},{read.IncompleteReason ?? (read.Success ? "" : "read_failed")},{level},{scan.Matches.Count},{rules},{(scan.IsComplete ? 1 : 0)},{s1.Internal:0.0000},{s1.Confidential:0.0000},{s1.Restricted:0.0000}," +
                $"{t.ProximityRejectCount},{t.LevelGateDowngradeCount},{t.KeywordHitNoMatchPrimaryRules},{t.UnstructuredNumericHits},{(t.DocContextDiscount ? 1 : 0)},{t.IdentifierVolumeNearGate:0.0000}\n"));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"Đã ghi fingerprint {files.Count} file → {outFile}");
        return 0;
    }
}
