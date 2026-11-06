// [Thay đổi] File mới (kiểm chứng nguyên nhân Tika tốn CPU): bấm giờ TÁCH TỪNG PHẦN khi đọc file bằng tika_native.dll.
// Cách dùng:
//   DlpTikaBench tika-profile --manifest <csv DlpBaseline> --dll <tika_native.dll> --label <tên-biến-thể>
//       [--rawdata D:\dlp-classifier\rawdata] [--files 4000] [--threads 1,16] [--out tools\perf\results] [--write-list <file>]
// Mỗi file đi qua ĐÚNG các bước TikaNativeLibrary + FileReaderService làm, nhưng bấm giờ riêng:
//   attach (gắn luồng vào isolate) → detect (tika_detect, ước lượng chi phí nhận dạng — H1) → extract (tika_open + tika_read: phân tích
//   trong Java + copy UTF-8 sang C#) → csharp (giải mã đã nằm trong extract; ở đây: tách dòng + ghép + NFC như FileReaderService — H4a)
//   → detach. Đo thêm thời gian CPU của cả tiến trình để tính tỉ lệ dùng CPU khi nhiều luồng (H6a).
// Công tắc thí nghiệm phía Java (H2/H3/H4b/H6b) bật bằng biến môi trường DLP_TIKA_EXP_* TRƯỚC khi chạy (đọc 1 lần lúc tika_init).
// Không ghi nội dung file ra đâu cả; chỉ ghi số đo + id file.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DlpService.Reading.Extractors;
using DlpService.Text;

namespace DlpTikaBench;

internal static class TikaProfile
{
    private sealed record Item(string Path, string Id, string Format);

    private sealed record Sample(Item Item, int Status, int Chars, double AttachMs, double DetectMs, double ExtractMs, double CSharpMs, double DetachMs);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string[] args)
    {
        string? Opt(string name, string? def = null) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        string? manifest = Opt("--manifest");
        string? dll = Opt("--dll");
        if (manifest == null || dll == null)
        {
            Console.Error.WriteLine("Thiếu --manifest <csv DlpBaseline> hoặc --dll <tika_native.dll>");
            return 1;
        }
        string rawdata = Opt("--rawdata", @"D:\dlp-classifier\rawdata")!;
        int fileCount = int.Parse(Opt("--files", "4000")!, Inv);
        int[] levels = Opt("--threads", "1,16")!.Split(',').Select(s => int.Parse(s, Inv)).ToArray();
        string label = Opt("--label", "baseline")!;
        string outDir = Opt("--out", Path.Combine("tools", "perf", "results"))!;
        // [Thay đổi] Thí nghiệm 1a (pool isolate): K isolate, mỗi isolate có heap + GC RIÊNG → GC ở isolate này không dừng luồng của
        // isolate khác. Luồng thứ i luôn dùng isolate (i mod K).
        int isolates = Math.Max(1, int.Parse(Opt("--isolates", "1")!, Inv));

        var items = LoadItems(manifest, rawdata, fileCount);
        // Danh sách đường dẫn tương đối (so với rawdata) cho ExtractCli chạy trên JVM — cùng tập file, cùng thứ tự (H5)
        if (Opt("--write-list") is string listPath)
        {
            File.WriteAllLines(listPath, items.Select(i => Path.GetRelativePath(rawdata, i.Path)));
            Console.WriteLine($"Đã ghi danh sách {items.Count} file: {listPath}");
        }

        string flags = string.Join(" ", Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(k => k.StartsWith("DLP_TIKA_EXP_", StringComparison.Ordinal)).OrderBy(k => k)
            .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}"));
        Console.WriteLine($"[{label}] {items.Count} file, luồng [{string.Join(", ", levels)}], công tắc: {(flags.Length == 0 ? "(không)" : flags)}");

        // Đọc trước mọi file vào bộ đệm của Windows (khử thiên lệch lạnh/nóng)
        long bytes = items.Sum(i => (long)File.ReadAllBytes(i.Path).Length);
        Console.WriteLine($"Làm nóng bộ đệm file: {bytes / 1048576.0:N0} MB");

        // [Thay đổi] 1a: tạo K isolate (cùng 1 DLL đã nạp; mỗi NativeEngine gọi graal_create_isolate 1 lần)
        var engines = Enumerable.Range(0, isolates).Select(_ => new NativeEngine(dll, null, 2_000_000, 30_000)).ToArray();
        var engine = engines[0];
        Console.WriteLine($"DLL SHA-256 {engine.Sha256[..16]}…, {isolates} isolate");

        // Làm nóng: 1 lượt trọn 1 luồng (không tính) — giống DlpConcurrencyBench. [Thay đổi] 1a: làm nóng MỌI isolate
        // isolate đầu làm nóng TRỌN (để K=1 so được với các lượt trước); các isolate sau 1.500 file là đủ (native không có JIT)
        RunLevel([engines[0]], items, 1);
        foreach (var e in engines.Skip(1))
        {
            RunLevel([e], items.Take(1500).ToList(), 1);
        }

        var md = new StringBuilder();
        md.AppendLine($"# Tika profile — biến thể `{label}` — {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        md.AppendLine($"- {items.Count:N0} file rawdata (đã đọc trước vào bộ đệm), DLL `{engine.Sha256[..16]}…`, **{isolates} isolate**, công tắc: `{(flags.Length == 0 ? "không" : flags)}`.");
        md.AppendLine("- attach/detach = gắn/tháo luồng khỏi isolate (service làm mỗi lần đọc). detect = tika_detect (ước lượng phần nhận dạng mà parse cũng làm bên trong). extract = tika_open+tika_read (phân tích trong Java + NFC Java + copy UTF-8 sang C#). C# = tách dòng + ghép + NFC như FileReaderService.");
        md.AppendLine("- CPU dùng = thời gian CPU tiến trình ÷ (thời gian thực × số luồng): thấp nghĩa là luồng phải CHỜ (khoá, GC dừng mọi luồng, I/O).\n");
        md.AppendLine("| Luồng | Định dạng | Số file | attach TB | detect TB | extract TB / p50 / p95 | C# TB | detach TB | Tổng TB | Lỗi |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        // [Thay đổi] C4 (chỉnh GC): thêm RAM đỉnh của tiến trình (gồm heap Java của isolate) để thấy đánh đổi tốc độ ↔ RAM
        var summary = new StringBuilder("| Luồng | Thời gian (s) | File/s | CPU tiến trình (s) | CPU / file (ms) | CPU dùng | detect / tổng | C# / tổng | RAM đỉnh (MB) |\n|---|---|---|---|---|---|---|---|---|\n");
        var csv = new StringBuilder("label,threads,id,format,status,chars,attach_ms,detect_ms,extract_ms,csharp_ms,detach_ms\n");

        foreach (int level in levels)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            var samples = RunLevel(engines, items, level);
            double wall = sw.Elapsed.TotalSeconds;
            proc.Refresh();
            double cpuSec = (proc.TotalProcessorTime - cpu0).TotalSeconds;

            foreach (var g in samples.GroupBy(s => s.Item.Format).OrderBy(g => g.Key).Append(samples.GroupBy(_ => "tất cả").First()))
            {
                var x = g.ToList();
                var ex = x.Select(s => s.ExtractMs).OrderBy(v => v).ToList();
                md.AppendLine($"| {level} | {g.Key} | {x.Count} | {N(x.Average(s => s.AttachMs))} | {N(x.Average(s => s.DetectMs))} | " +
                    $"{N(ex.Average())} / {N(P(ex, 50))} / {N(P(ex, 95))} | {N(x.Average(s => s.CSharpMs))} | {N(x.Average(s => s.DetachMs))} | " +
                    $"{N(x.Average(Total))} | {x.Count(s => s.Status != 0 && s.Status != 5)} |");
            }
            double sumTotal = samples.Sum(Total);
            summary.AppendLine($"| {level} | {N(wall)} | {N(items.Count / wall, "0")} | {N(cpuSec)} | {N(cpuSec * 1000 / items.Count)} | " +
                $"{N(cpuSec / (wall * Math.Min(level, Environment.ProcessorCount)) * 100, "0")} % | " +
                $"{N(samples.Sum(s => s.DetectMs) / sumTotal * 100, "0")} % | {N(samples.Sum(s => s.CSharpMs) / sumTotal * 100, "0")} % | " +
                $"{proc.PeakWorkingSet64 / 1048576} |"); // đỉnh tính từ đầu tiến trình — chạy mỗi mức luồng trong 1 tiến trình riêng để tách
            foreach (var s in samples)
            {
                csv.AppendLine(string.Join(',', label, level, s.Item.Id, s.Item.Format, s.Status, s.Chars,
                    N(s.AttachMs, "0.###"), N(s.DetectMs, "0.###"), N(s.ExtractMs, "0.###"), N(s.CSharpMs, "0.###"), N(s.DetachMs, "0.###")));
            }
            Console.WriteLine($"  {level} luồng: {wall:N1} s, {items.Count / wall:N0} file/s, CPU {cpuSec:N1} s");
        }

        md.AppendLine("\n" + summary);
        Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
        string mdPath = Path.GetFullPath(Path.Combine(outDir, $"tikaprofile-{stamp}-{label}.md"));
        File.WriteAllText(mdPath, md.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.ChangeExtension(mdPath, ".csv"), csv.ToString(), new UTF8Encoding(true));
        Console.WriteLine(md);
        Console.WriteLine($"Báo cáo: {mdPath}");
        // Huỷ trên luồng chính (luồng đã tạo các isolate); tika_shutdown in [GCSTATS] của TỪNG isolate nếu bật
        foreach (var e in engines)
        {
            e.Dispose();
        }
        return 0;
    }

    private static double Total(Sample s) => s.AttachMs + s.DetectMs + s.ExtractMs + s.CSharpMs + s.DetachMs;

    // N luồng RIÊNG (không ThreadPool) cùng lấy file từ 1 hàng đợi. Mỗi file: attach → ... → detach trên CÙNG luồng (quy tắc an toàn
    // của isolate GraalVM, xem tools/tika-native/README.md).
    // [Thay đổi] 1a: nhận mảng isolate; luồng thứ i dùng engines[i % K]
    private static List<Sample> RunLevel(NativeEngine[] engines, List<Item> items, int level)
    {
        var queue = new System.Collections.Concurrent.ConcurrentQueue<Item>(items);
        var bag = new System.Collections.Concurrent.ConcurrentBag<Sample>();
        var threads = Enumerable.Range(0, level).Select(i => new Thread(() =>
        {
            var engine = engines[i % engines.Length];
            var t = Stopwatch.StartNew();
            while (queue.TryDequeue(out var item))
            {
                t.Restart();
                IntPtr th = engine.Attach();
                double attach = Lap(t);
                engine.Detect(th, item.Path);
                double detect = Lap(t);
                var (status, text) = engine.Extract(th, item.Path);
                double extract = Lap(t);
                // Phần C# sau khi có text: giống TikaNativeExtractor.ToBlocks + FileReaderService (ghép "\n" + NFC)
                var sb = new StringBuilder();
                foreach (var block in TikaNativeExtractor.ToBlocks(text))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(block.Text);
                }
                int chars = Normalizer.ToNfc(sb.ToString()).Length;
                double csharp = Lap(t);
                engine.Detach(th);
                double detach = Lap(t);
                bag.Add(new Sample(item, status, chars, attach, detect, extract, csharp, detach));
            }
        }) { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        return bag.ToList();
    }

    private static double Lap(Stopwatch sw) { double ms = sw.Elapsed.TotalMilliseconds; sw.Restart(); return ms; }

    // Cùng cách chọn file với DlpConcurrencyBench (16 lớp × 2 định dạng, seed cố định) để so được với số 5A
    private static List<Item> LoadItems(string manifest, string rawdata, int count)
    {
        var lines = File.ReadAllLines(manifest, Encoding.UTF8);
        var head = lines[0].Split(',');
        int Col(string n) => Array.IndexOf(head, n);
        int cId = Col("id"), cFmt = Col("format"), cDom = Col("domain"), cLab = Col("label"), cOk = Col("read_ok");
        var rows = lines.Skip(1).Select(l => l.Split(',')).Where(c => c.Length > cOk && c[cOk] == "1")
            .Select(c => new Item(Path.Combine(rawdata, $"{c[cDom]}__{c[cLab]}", $"{c[cId]}.{c[cFmt]}"), c[cId], c[cFmt])).ToList();
        var rnd = new Random(20261022);
        var groups = rows.GroupBy(r => Path.GetDirectoryName(r.Path) + r.Format).ToList();
        int per = (int)Math.Ceiling(count / (double)groups.Count);
        return groups.SelectMany(g => g.OrderBy(_ => rnd.Next()).Take(per)).OrderBy(_ => rnd.Next()).Take(count).ToList();
    }

    private static double P(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static string N(double v, string f = "0.00") => v.ToString(f, Inv);
}
