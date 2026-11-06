// [Thay đổi] File mới (đánh giá xử lý đồng thời — phần A).
// Cách dùng: DlpConcurrencyBench --manifest <csv DlpBaseline> [--rawdata D:\dlp-classifier\rawdata] [--files 4000]
//            [--levels 1,2,4,6,8,10,12,16,32,64] [--cache-level 8] [--bigpdf <thư mục chứa pdf lớn>] [--out tools\perf\results]
// Mỗi file đi qua ĐÚNG các bước của FileWatcherService.ProcessFile (không có watcher/debounce): tra cache DB → trích text → băm →
// quét regex → phân loại + S1 + Router + FileConfidence → ghi DB. Mỗi mức song song: N luồng RIÊNG cùng lấy file từ 1 hàng đợi (mức
// song song chính xác, không phụ thuộc thread pool), các đối tượng pipeline DÙNG CHUNG giữa các luồng (như service), DB tạm mới.
// Không ghi giá trị khớp ra đâu cả; DB tạm xoá sau mỗi mức.
// [Thay đổi] Đo tài nguyên: mỗi mức ghi thêm CPU của tiến trình (TB + đỉnh theo cửa sổ 250 ms, quy ra % toàn máy và số lõi dùng),
// RAM (working set TB/đỉnh, private bytes đỉnh) và % thời gian GC dừng các luồng.
// [Thay đổi] Đo cache trong luồng (--cache-level N, 0 = bỏ qua): 3 lượt nối tiếp trên CÙNG 1 DB, đúng logic cache của ProcessFile:
//   lượt 1 "lạnh"   — DB trống → mọi file trích + quét + ghi DB;
//   lượt 2 "tầng 1" — size/mtime không đổi → bỏ qua, KHÔNG mở file;
//   lượt 3 "tầng 2" — giả lập mtime đổi (+1 giây, CHỈ trong bộ nhớ, KHÔNG sửa file rawdata) → trích text + băm, hash trùng → bỏ quét.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DlpService.Classification;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpConcurrencyBench;

internal sealed record Item(string Path, string Id, string Format, string Truth);

/// <summary>Kết quả 1 file ở 1 mức song song.</summary>
/// <param name="Cache">[Thay đổi] Đo cache: "tier1" / "tier2" nếu được cache bỏ qua, null nếu quét đủ.</param>
internal sealed record Outcome(Item Item, string? Error, string Predicted, double CacheMs, double ReadMs, double ScanMs, double ClassifyMs,
    double SaveMs, double TotalMs, string? Cache = null);

// [Thay đổi] Đo tài nguyên: số đo CPU/RAM/GC của 1 lượt chạy
/// <summary>Tài nguyên tiến trình dùng trong 1 lượt.</summary>
/// <param name="CpuAvgPct">CPU trung bình, % toàn máy (100 % = mọi luồng logic chạy kín).</param>
/// <param name="CpuPeakPct">CPU cao nhất trong 1 cửa sổ 250 ms, % toàn máy.</param>
/// <param name="CoresUsed">Số lõi dùng trung bình = thời gian CPU / thời gian thực.</param>
/// <param name="AvgWsMb">Working set trung bình (MB).</param>
/// <param name="PeakWsMb">Working set cao nhất (MB).</param>
/// <param name="PeakPrivateMb">Private bytes cao nhất (MB) — bộ nhớ riêng của tiến trình, không tính phần chia sẻ.</param>
/// <param name="PeakThreads">Số thread cao nhất.</param>
/// <param name="AllocMb">Tổng bộ nhớ cấp phát trên heap .NET (MB).</param>
/// <param name="GcPausePct">% thời gian thực mà GC dừng các luồng .NET.</param>
internal sealed record Resources(double CpuAvgPct, double CpuPeakPct, double CoresUsed, long AvgWsMb, long PeakWsMb, long PeakPrivateMb,
    int PeakThreads, double AllocMb, double GcPausePct);

/// <summary>Kết quả 1 mức song song.</summary>
internal sealed record LevelResult(int Level, double WallSec, List<Outcome> Outcomes, Resources Res, long DbRows, int Mismatch);

// [Thay đổi] Đo cache: kết quả 1 lượt của phần đo cache
internal sealed record CachePass(string Name, string Description, double WallSec, List<Outcome> Outcomes, Resources Res);

internal static class Bench
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        DlpTools.Shared.PowerThrottling.Disable(); // [Sửa lỗi đo] xem chú thích trong tools/shared/PowerThrottling.cs
        // [Thay đổi] Đo lại với Tika native: chọn bộ đọc (--extractor legacy|tika) TRƯỚC khi tạo FileReaderService
        args = DlpTools.Shared.ExtractorSwitch.Apply(args);
        string? manifest = Arg(args, "--manifest");
        if (manifest == null) { Console.Error.WriteLine("Thiếu --manifest <csv DlpBaseline>"); return 1; }
        string rawdata = Arg(args, "--rawdata") ?? @"D:\dlp-classifier\rawdata";
        int fileCount = int.Parse(Arg(args, "--files") ?? "4000", Inv);
        // [Thay đổi] Đo tài nguyên: thêm các mức 6, 10, 12 để tìm điểm tối ưu chi tiết hơn quanh vùng 4–16
        int[] levels = (Arg(args, "--levels") ?? "1,2,4,6,8,10,12,16,32,64").Split(',').Select(s => int.Parse(s, Inv)).ToArray();
        int cacheLevel = int.Parse(Arg(args, "--cache-level") ?? "8", Inv); // [Thay đổi] Đo cache
        string? bigPdf = Arg(args, "--bigpdf");
        string outDir = Arg(args, "--out") ?? Path.Combine("tools", "perf", "results");
        string tag = Arg(args, "--tag") ?? ""; // [Thay đổi] nhãn phân biệt bản build khi đo A-B-B-A (ghi vào tên file kết quả)

        // Pipeline dùng chung (giống Worker tạo 1 lần cho cả service)
        var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
        var pipe = new Pipeline(
            new FileReaderService(), new RegexScanner(patterns), new ClassificationEngine(NullLogger.Instance),
            new FileConfidenceScorer(FileConfidenceModel.Load(Path.Combine(AppContext.BaseDirectory, "confidence", "file-confidence-v1.json"))));

        var items = LoadItems(manifest, rawdata, fileCount);
        Console.WriteLine($"{items.Count:N0} file, mức song song [{string.Join(", ", levels)}], {Environment.ProcessorCount} luồng logic");

        // [Sửa lỗi đo] Đọc trước mọi file 1 lần để chúng nằm sẵn trong bộ đệm file của Windows. Lượt chạy đầu (1 luồng) trước đây phải
        // đọc từ đĩa lần đầu còn các mức sau đọc từ bộ đệm → mức 1 chậm giả tạo (trích 12,4 ms so với 2,7 ms), tăng tốc bị thổi phồng.
        var warm = Stopwatch.StartNew();
        long bytes = items.Sum(i => (long)File.ReadAllBytes(i.Path).Length);
        if (bigPdf != null && Directory.Exists(bigPdf)) bytes += Directory.GetFiles(bigPdf, "*.pdf").Sum(f => (long)File.ReadAllBytes(f).Length);
        Console.WriteLine($"Làm nóng bộ đệm file: {bytes / 1048576.0:N0} MB trong {warm.Elapsed.TotalSeconds:N1} giây");

        // Làm nóng: JIT + biên dịch regex (không tính).
        // [Sửa lỗi đo] Chạy TRỌN 1 lượt 1 luồng thay cho 40 file: .NET tối ưu mã dần (tiered JIT) trong vài nghìn file đầu — chạy thử thứ tự
        // 1→2→1→1 cho 21,6 / 9,3 / 15,3 / 15,9 giây: lần 1 luồng đầu tiên chậm hơn ~40 % chỉ vì chạy trước.
        var warmup = Stopwatch.StartNew();
        RunLevel(pipe, items, 1, null);
        Console.WriteLine($"Làm nóng (1 lượt trọn, không tính): {warmup.Elapsed.TotalSeconds:N1} giây");

        var results = new List<LevelResult>();
        Dictionary<string, string>? reference = null;
        foreach (int level in levels)
        {
            var r = RunLevel(pipe, items, level, reference);
            reference ??= r.Outcomes.ToDictionary(o => o.Item.Path, o => o.Predicted); // mức đầu tiên (1) làm chuẩn so sánh
            results.Add(r);
            Console.WriteLine($"  mức {level,2}: {r.WallSec,6:N1} giây, {items.Count / r.WallSec,6:N0} file/giây, lỗi {r.Outcomes.Count(o => o.Error != null)}, " +
                $"lệch kết quả {r.Mismatch}, DB {r.DbRows}/{items.Count}, CPU TB {r.Res.CpuAvgPct:N0} % (đỉnh {r.Res.CpuPeakPct:N0} %), " +
                $"RAM TB/đỉnh {r.Res.AvgWsMb}/{r.Res.PeakWsMb} MB, GC dừng {r.Res.GcPausePct:N1} %");
        }

        // [Thay đổi] Đo cache trong luồng
        List<CachePass>? cachePasses = null;
        if (cacheLevel > 0)
        {
            cachePasses = RunCachePasses(pipe, items, cacheLevel);
        }

        List<LevelResult>? bigResults = null;
        if (bigPdf != null && Directory.Exists(bigPdf))
        {
            var bigItems = Directory.GetFiles(bigPdf, "*.pdf").OrderBy(p => p).Select(p => new Item(p, Path.GetFileNameWithoutExtension(p), "pdf", "?")).ToList();
            Console.WriteLine($"PDF lớn: {bigItems.Count} file từ {bigPdf}");
            bigResults = [];
            Dictionary<string, string>? bigRef = null;
            // [Thay đổi] buổi 15: --bigpdf-levels (VD "6" để so 2 bộ đọc đúng ở mức service dùng); mặc định 1, 4, cả lô như trước
            int[] bigLevels = Arg(args, "--bigpdf-levels")?.Split(',').Select(s => int.Parse(s, Inv)).ToArray() ?? [1, 4, bigItems.Count];
            foreach (int level in bigLevels)
            {
                var r = RunLevel(pipe, bigItems, level, bigRef);
                bigRef ??= r.Outcomes.ToDictionary(o => o.Item.Path, o => o.Predicted);
                bigResults.Add(r);
                Console.WriteLine($"  PDF lớn mức {level,2}: {r.WallSec:N1} giây, CPU TB {r.Res.CpuAvgPct:N0} %, RAM đỉnh {r.Res.PeakWsMb} MB, lỗi {r.Outcomes.Count(o => o.Error != null)}");
            }
        }

        Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + "-" + DlpTools.Shared.ExtractorSwitch.Name // [Thay đổi] + tên bộ đọc
            + (tag.Length > 0 ? "-" + tag : "");
        string md = Report(results, cachePasses, cacheLevel, bigResults, items.Count);
        string mdPath = Path.GetFullPath(Path.Combine(outDir, $"concurrency-{stamp}.md"));
        File.WriteAllText(mdPath, md, new UTF8Encoding(true));
        WriteCsv(Path.Combine(outDir, $"concurrency-{stamp}.csv"), results);
        WriteResourceCsv(Path.Combine(outDir, $"concurrency-{stamp}-resources.csv"), results); // [Thay đổi] Đo tài nguyên
        Console.WriteLine(md);
        Console.WriteLine($"Báo cáo: {mdPath}");
        DlpTools.Shared.ExtractorSwitch.Shutdown(); // [Thay đổi] huỷ isolate Tika trên luồng chính
        return 0;
    }

    // Chọn file chia đều theo 16 lớp × 2 định dạng (cố định seed) từ CSV DlpBaseline
    private static List<Item> LoadItems(string manifest, string rawdata, int count)
    {
        var lines = File.ReadAllLines(manifest, Encoding.UTF8);
        var head = lines[0].Split(',');
        int Col(string n) => Array.IndexOf(head, n);
        int cId = Col("id"), cFmt = Col("format"), cDom = Col("domain"), cLab = Col("label"), cTruth = Col("truth"), cOk = Col("read_ok");
        var rows = lines.Skip(1).Select(l => l.Split(',')).Where(c => c.Length > cOk && c[cOk] == "1")
            .Select(c => new Item(Path.Combine(rawdata, $"{c[cDom]}__{c[cLab]}", $"{c[cId]}.{c[cFmt]}"), c[cId], c[cFmt], c[cTruth])).ToList();
        var rnd = new Random(20261022);
        var groups = rows.GroupBy(r => Path.GetDirectoryName(r.Path) + r.Format).ToList();
        int per = (int)Math.Ceiling(count / (double)groups.Count);
        return groups.SelectMany(g => g.OrderBy(_ => rnd.Next()).Take(per)).OrderBy(_ => rnd.Next()).Take(count).ToList();
    }

    private static LevelResult RunLevel(Pipeline pipe, List<Item> items, int level, Dictionary<string, string>? reference)
    {
        string dbFolder = Path.Combine(Path.GetTempPath(), "dlp-concbench-" + Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dbFolder, "bench.db");
        try
        {
            using var store = new ResultStore(dbPath);
            // [Thay đổi] Đo tài nguyên: phần chạy N luồng + lấy mẫu tách ra Measure() để dùng chung với phần đo cache
            var (wall, list, res) = Measure(items, level, item => pipe.Process(item, store));

            // An toàn đa luồng: (1) kết quả trùng mức tuần tự; (2) DB có đủ bản ghi
            int mismatch = reference == null ? 0 : list.Count(o => reference.TryGetValue(o.Item.Path, out var p) && p != o.Predicted);
            store.Dispose();
            long dbRows = CountRows(dbPath);
            return new LevelResult(level, wall, list, res, dbRows, mismatch);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dbFolder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // [Thay đổi] Đo cache trong luồng: 3 lượt nối tiếp trên CÙNG 1 DB (xem chú thích đầu file)
    private static List<CachePass> RunCachePasses(Pipeline pipe, List<Item> items, int level)
    {
        string dbFolder = Path.Combine(Path.GetTempPath(), "dlp-concbench-cache-" + Guid.NewGuid().ToString("N"));
        var passes = new List<CachePass>();
        try
        {
            using var store = new ResultStore(Path.Combine(dbFolder, "bench.db"));
            (string Name, string Desc, long Shift)[] plan =
            [
                ("lạnh", "DB trống: trích + băm + quét + ghi DB", 0),
                ("tầng 1", "size/mtime không đổi → bỏ qua, không mở file", 0),
                ("tầng 2", "mtime đổi (giả lập), text không đổi → trích + băm, bỏ quét", TimeSpan.TicksPerSecond),
            ];
            foreach (var (name, desc, shift) in plan)
            {
                var (wall, list, res) = Measure(items, level, item => pipe.Process(item, store, useCache: true, mtimeShiftTicks: shift));
                passes.Add(new CachePass(name, desc, wall, list, res));
                Console.WriteLine($"  cache {name,-7} ({level} luồng): {wall,6:N2} giây, {items.Count / wall,7:N0} file/giây, " +
                    $"tầng 1 {list.Count(o => o.Cache == "tier1")}, tầng 2 {list.Count(o => o.Cache == "tier2")}, quét {list.Count(o => o.Cache == null)}, " +
                    $"CPU TB {res.CpuAvgPct:N0} %, RAM đỉnh {res.PeakWsMb} MB");
            }
            return passes;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dbFolder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // [Thay đổi] Đo tài nguyên: chạy N luồng riêng cùng lấy file từ 1 hàng đợi, đồng thời lấy mẫu CPU/RAM/thread của tiến trình mỗi 50 ms.
    // CPU đỉnh tính theo cửa sổ 250 ms (5 mẫu) — cửa sổ 50 ms quá ngắn, nhiễu do lịch chạy của Windows (độ phân giải ~15,6 ms).
    private static (double Wall, List<Outcome> Outcomes, Resources Res) Measure(List<Item> items, int level, Func<Item, Outcome> work)
    {
        var outcomes = new ConcurrentBag<Outcome>();
        var queue = new ConcurrentQueue<Item>(items);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long alloc0 = GC.GetTotalAllocatedBytes(precise: false);
        TimeSpan gcPause0 = GC.GetTotalPauseDuration();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        TimeSpan cpu0 = proc.TotalProcessorTime;

        long peakWs = 0, peakPrivate = 0, sumWs = 0, samples = 0;
        int peakThreads = 0;
        double peakCpu = 0;
        using var stopSampler = new CancellationTokenSource();
        var sampler = Task.Run(() =>
        {
            var p = Process.GetCurrentProcess();
            var window = Stopwatch.StartNew();
            TimeSpan windowCpu = p.TotalProcessorTime;
            while (!stopSampler.IsCancellationRequested)
            {
                p.Refresh();
                peakWs = Math.Max(peakWs, p.WorkingSet64);
                peakPrivate = Math.Max(peakPrivate, p.PrivateMemorySize64);
                peakThreads = Math.Max(peakThreads, p.Threads.Count);
                sumWs += p.WorkingSet64;
                samples++;
                if (window.Elapsed.TotalMilliseconds >= 250)
                {
                    TimeSpan nowCpu = p.TotalProcessorTime;
                    double pct = (nowCpu - windowCpu).TotalMilliseconds / window.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                    peakCpu = Math.Max(peakCpu, pct);
                    windowCpu = nowCpu;
                    window.Restart();
                }
                Thread.Sleep(50);
            }
        });

        var sw = Stopwatch.StartNew();
        var threads = Enumerable.Range(0, level).Select(_ => new Thread(() =>
        {
            while (queue.TryDequeue(out var item))
            {
                outcomes.Add(work(item));
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        double wall = sw.Elapsed.TotalSeconds;

        stopSampler.Cancel();
        sampler.Wait();
        proc.Refresh();
        double cpuSec = (proc.TotalProcessorTime - cpu0).TotalSeconds;
        double allocMb = (GC.GetTotalAllocatedBytes(false) - alloc0) / 1048576.0;
        double gcPausePct = (GC.GetTotalPauseDuration() - gcPause0).TotalSeconds / wall * 100;
        double cores = cpuSec / wall;
        var res = new Resources(
            CpuAvgPct: cores / Environment.ProcessorCount * 100,
            CpuPeakPct: Math.Min(100, Math.Max(peakCpu, cores / Environment.ProcessorCount * 100)), // lượt quá ngắn (< 250 ms) → lấy TB
            CoresUsed: cores,
            AvgWsMb: samples == 0 ? peakWs / 1048576 : sumWs / samples / 1048576,
            PeakWsMb: peakWs / 1048576,
            PeakPrivateMb: peakPrivate / 1048576,
            PeakThreads: peakThreads,
            AllocMb: allocMb,
            GcPausePct: gcPausePct);
        return (wall, outcomes.ToList(), res);
    }

    private static long CountRows(string dbPath)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM scan_result;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string Report(List<LevelResult> rs, List<CachePass>? cache, int cacheLevel, List<LevelResult>? big, int fileCount)
    {
        var md = new StringBuilder();
        string N(double v, string f = "0.0") => v.ToString(f, Inv);
        double P(IEnumerable<double> v, double p) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[Math.Clamp((int)Math.Ceiling(p / 100 * s.Count) - 1, 0, s.Count - 1)]; }
        double baseTput = fileCount / rs[0].WallSec;

        md.AppendLine($"# Xử lý đồng thời trong tiến trình — {DateTime.Now:yyyy-MM-dd HH:mm} — bộ đọc `{DlpTools.Shared.ExtractorSwitch.Name}`\n"); // [Thay đổi]
        md.AppendLine($"- {fileCount:N0} file rawdata (16 lớp × docx/pdf), {Environment.ProcessorCount} luồng logic. Pipeline = FileWatcherService.ProcessFile (tra cache → trích → băm → quét → phân loại + S1 + Router + FileConfidence → ghi DB), không có watcher/debounce.");
        md.AppendLine("- Mỗi mức: N luồng riêng dùng chung 1 bộ đối tượng pipeline + 1 DB tạm mới. \"Lệch kết quả\" = số file có mức KHÁC lần chạy 1 luồng (kiểm tra an toàn đa luồng).\n");
        md.AppendLine("## Thông lượng và độ trễ\n");
        md.AppendLine("| Song song | Thời gian (giây) | File/giây | Tăng tốc | Hiệu suất/luồng | Độ trễ p50 / p95 / p99 / max (ms) | Trích TB | Quét TB | Ghi DB TB | Lỗi | Lệch kết quả | Bản ghi DB |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rs)
        {
            var o = r.Outcomes;
            double tput = fileCount / r.WallSec;
            md.AppendLine($"| {r.Level} | {N(r.WallSec)} | {N(tput, "0")} | ×{N(tput / baseTput, "0.00")} | {N(tput / baseTput / r.Level * 100, "0")} % | " +
                $"{N(P(o.Select(x => x.TotalMs), 50))} / {N(P(o.Select(x => x.TotalMs), 95))} / {N(P(o.Select(x => x.TotalMs), 99))} / {N(o.Max(x => x.TotalMs))} | " +
                $"{N(o.Average(x => x.ReadMs))} | {N(o.Average(x => x.ScanMs))} | {N(o.Average(x => x.SaveMs))} | {o.Count(x => x.Error != null)} | {r.Mismatch} | {r.DbRows}/{fileCount} |");
        }

        // [Thay đổi] Đo tài nguyên: bảng CPU/RAM riêng (gộp chung 1 bảng quá rộng, khó đọc)
        md.AppendLine("\n## Tài nguyên (CPU / RAM)\n");
        md.AppendLine("- CPU tính theo % TOÀN MÁY (100 % = cả " + Environment.ProcessorCount + " luồng logic chạy kín). \"Lõi dùng\" = thời gian CPU / thời gian thực. \"CPU/file\" = thời gian CPU trung bình cho 1 file — tăng theo số luồng nghĩa là CPU bị phí vào chờ khoá, GC, chuyển ngữ cảnh.\n");
        md.AppendLine("| Song song | CPU TB | CPU đỉnh (250 ms) | Lõi dùng | CPU/file (ms) | RAM TB (MB) | RAM đỉnh (MB) | Private đỉnh (MB) | Thread đỉnh | Cấp phát (MB) | GC dừng |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rs)
        {
            var x = r.Res;
            md.AppendLine($"| {r.Level} | {N(x.CpuAvgPct, "0")} % | {N(x.CpuPeakPct, "0")} % | {N(x.CoresUsed, "0.0")} | {N(x.CoresUsed * r.WallSec * 1000 / fileCount, "0.0")} | " +
                $"{x.AvgWsMb} | {x.PeakWsMb} | {x.PeakPrivateMb} | {x.PeakThreads} | {N(x.AllocMb, "0")} | {N(x.GcPausePct, "0.0")} % |");
        }

        var errs = rs.SelectMany(r => r.Outcomes.Where(o => o.Error != null).Select(o => (r.Level, o.Error))).GroupBy(x => x.Error).ToList();
        if (errs.Count > 0)
        {
            md.AppendLine("\nLỗi theo loại:\n\n| Lỗi | Số lần | Ở mức song song |\n|---|---|---|");
            foreach (var g in errs) md.AppendLine($"| {g.Key} | {g.Count()} | {string.Join(", ", g.Select(x => x.Level).Distinct())} |");
        }

        // [Thay đổi] Đo cache trong luồng
        if (cache != null)
        {
            md.AppendLine($"\n## Cache trong luồng ({cacheLevel} luồng, cùng 1 DB, 3 lượt nối tiếp)\n");
            md.AppendLine("| Lượt | Mô tả | Thời gian (giây) | File/giây | Độ trễ p50 / p95 (ms) | Tra cache TB (ms) | Trích TB (ms) | Bỏ qua tầng 1 / tầng 2 / quét đủ | CPU TB | CPU/file (ms) | RAM đỉnh (MB) | Cấp phát (MB) |");
            md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var c in cache)
            {
                var o = c.Outcomes;
                md.AppendLine($"| {c.Name} | {c.Description} | {N(c.WallSec, "0.00")} | {N(fileCount / c.WallSec, "0")} | " +
                    $"{N(P(o.Select(x => x.TotalMs), 50), "0.00")} / {N(P(o.Select(x => x.TotalMs), 95), "0.00")} | {N(o.Average(x => x.CacheMs), "0.00")} | {N(o.Average(x => x.ReadMs), "0.00")} | " +
                    $"{o.Count(x => x.Cache == "tier1")} / {o.Count(x => x.Cache == "tier2")} / {o.Count(x => x.Cache == null)} | {N(c.Res.CpuAvgPct, "0")} % | " +
                    $"{N(c.Res.CoresUsed * c.WallSec * 1000 / fileCount, "0.00")} | {c.Res.PeakWsMb} | {N(c.Res.AllocMb, "0")} |");
            }
        }

        if (big != null)
        {
            md.AppendLine("\n## PDF lớn (1,5 triệu ký tự, ~1,4 MB, ~500 trang/file) chạy song song\n");
            md.AppendLine("| Song song | Số file | Thời gian (giây) | Độ trễ TB / max (ms) | CPU TB | RAM đỉnh (MB) | Private đỉnh (MB) | Cấp phát (MB) | GC dừng | Lỗi | Lệch kết quả |\n|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var r in big)
            {
                md.AppendLine($"| {r.Level} | {r.Outcomes.Count} | {N(r.WallSec)} | {N(r.Outcomes.Average(x => x.TotalMs), "0")} / {N(r.Outcomes.Max(x => x.TotalMs), "0")} | " +
                    $"{N(r.Res.CpuAvgPct, "0")} % | {r.Res.PeakWsMb} | {r.Res.PeakPrivateMb} | {N(r.Res.AllocMb, "0")} | {N(r.Res.GcPausePct, "0.0")} % | {r.Outcomes.Count(x => x.Error != null)} | {r.Mismatch} |");
            }
        }
        return md.ToString();
    }

    private static void WriteCsv(string path, List<LevelResult> rs)
    {
        var sb = new StringBuilder("level,id,format,truth,predicted,error,cache_ms,read_ms,scan_ms,classify_ms,save_ms,total_ms\n");
        foreach (var r in rs)
        {
            foreach (var o in r.Outcomes)
            {
                sb.AppendLine(string.Join(',', r.Level, o.Item.Id, o.Item.Format, o.Item.Truth, o.Predicted, (o.Error ?? "").Replace(',', ';'),
                    N(o.CacheMs), N(o.ReadMs), N(o.ScanMs), N(o.ClassifyMs), N(o.SaveMs), N(o.TotalMs)));
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // [Thay đổi] Đo tài nguyên: 1 dòng/mức để vẽ biểu đồ thông lượng – CPU – RAM
    private static void WriteResourceCsv(string path, List<LevelResult> rs)
    {
        var sb = new StringBuilder("level,wall_sec,files_per_sec,cpu_avg_pct,cpu_peak_pct,cores_used,ws_avg_mb,ws_peak_mb,private_peak_mb,threads_peak,alloc_mb,gc_pause_pct\n");
        foreach (var r in rs)
        {
            var x = r.Res;
            sb.AppendLine(string.Join(',', r.Level, N(r.WallSec), N(r.Outcomes.Count / r.WallSec), N(x.CpuAvgPct), N(x.CpuPeakPct), N(x.CoresUsed),
                x.AvgWsMb, x.PeakWsMb, x.PeakPrivateMb, x.PeakThreads, N(x.AllocMb), N(x.GcPausePct)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string? Arg(string[] a, string n) { int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}

/// <summary>Các bước của FileWatcherService.ProcessFile (bản rút gọn: không log, không job LLM), dùng chung giữa các luồng.</summary>
internal sealed class Pipeline(FileReaderService reader, RegexScanner scanner, ClassificationEngine engine, FileConfidenceScorer confidence)
{
    private static readonly RouterOptions Router = new() { THigh = 0.80, TLow = 0.10 }; // giống appsettings.json

    /// <param name="useCache">[Thay đổi] Đo cache: true = áp dụng đúng 2 tầng cache của ProcessFile (tầng 1 size/mtime, tầng 2 file_hash).</param>
    /// <param name="mtimeShiftTicks">[Thay đổi] Đo cache: cộng vào mtime đọc được — giả lập "file vừa được lưu lại" mà KHÔNG sửa file thật.</param>
    public Outcome Process(Item item, ResultStore store, bool useCache = false, long mtimeShiftTicks = 0)
    {
        var total = Stopwatch.StartNew();
        var t = Stopwatch.StartNew();
        double cache = 0, read = 0, scan = 0, cls = 0, save = 0;
        try
        {
            var info = new FileInfo(item.Path);
            DateTime mtime = info.LastWriteTimeUtc.AddTicks(mtimeShiftTicks); // [Thay đổi] Đo cache
            var cached = store.TryGetFileState(item.Path);
            cache = Lap(t);

            // [Thay đổi] Đo cache tầng 1 — giống ProcessFile: size + mtime giống hệt và lần trước đã kết luận → không mở file
            if (useCache && cached is { HasConclusion: true } && cached.SameSizeAndMtime(info.Length, mtime))
            {
                return new Outcome(item, null, "Cached", cache, 0, 0, 0, 0, total.Elapsed.TotalMilliseconds, "tier1");
            }

            var r = reader.TryReadFile(item.Path);
            read = Lap(t);
            if (!r.Success)
            {
                return new Outcome(item, "đọc lỗi: " + r.ErrorMessage, "ReadError", cache, read, 0, 0, 0, total.Elapsed.TotalMilliseconds);
            }
            string content = r.Content ?? "";
            string hash = ContentHash.Compute(content);

            // [Thay đổi] Đo cache tầng 2 — giống ProcessFile: text trích ra y hệt → chỉ cập nhật size/mtime, không quét
            if (useCache && cached is { HasConclusion: true } && cached.FileHash == hash)
            {
                store.MarkUnchanged(item.Path, info.Length, mtime);
                save = Lap(t);
                return new Outcome(item, null, "Cached", cache, read, 0, 0, save, total.Elapsed.TotalMilliseconds, "tier2");
            }

            var result = scanner.Scan(content);
            scan = Lap(t);
            if (!result.IsComplete)
            {
                return new Outcome(item, "quét chưa trọn (timeout)", "Unconcluded", cache, read, scan, 0, 0, total.Elapsed.TotalMilliseconds);
            }

            var level = SensitivityLevel.Public;
            var domains = new List<string>();
            if (result.Matches.Count > 0)
            {
                var (lv, res) = engine.Classify(new ScanContext { FilePath = item.Path, Content = content, Matches = result.Matches });
                level = lv;
                domains = res.Select(x => x.Domain).ToList();
            }
            var s1 = FileScorer.Compute(result.Matches, content.Length);
            var route = ScoreRouter.Route(level, s1, result.Matches.Count, Router);
            var conf = confidence.Compute(level, result.Matches, result.Telemetry);
            var findings = result.Matches.GroupBy(m => m.RuleId ?? m.PatternName).Select(g =>
                new FindingRecord(g.Key, g.Max(m => m.Score), g.Any(m => m.Validated) ? true : null, g.Any(m => m.Validated), g.Count(), $"offset:{g.Min(m => m.Index)}")).ToList();
            cls = Lap(t);

            store.SaveScan(new ScanRecord(item.Path, info.Length, mtime, hash, route.Confidence, // [Thay đổi] Đo cache: mtime (có thể đã lệch)
                route.NeedsLlm ? ResultStore.StatusProvisional : ResultStore.StatusFinal, route.NeedsLlm,
                JsonSerializer.Serialize(new { level = level.ToString(), domains }), findings,
                route.NeedsLlm ? LlmJobPriority.GreyZone : null, RulesVer: "bench", MergePolicyVer: "bench",
                FileConf: conf.Confidence, ConfBranch: conf.Branch.ToString()));
            save = Lap(t);
            return new Outcome(item, null, level.ToString(), cache, read, scan, cls, save, total.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            // Bất kỳ exception nào lọt ra = lỗi đa luồng/tài nguyên cần biết — chỉ ghi TÊN loại lỗi
            return new Outcome(item, "exception: " + ex.GetType().Name, "Exception", cache, read, scan, cls, save, total.Elapsed.TotalMilliseconds);
        }
    }

    private static double Lap(Stopwatch sw) { double ms = sw.Elapsed.TotalMilliseconds; sw.Restart(); return ms; }
}
