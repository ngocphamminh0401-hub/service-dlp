// [Thay đổi] File mới (Benchmark T05): đo FileReaderService.TryReadFile tách từng bước — xác nhận/bác bỏ giả thuyết B1–B7 của Code Review.
//   DlpExtractorBench stages <dataset> [--sets D-txt,D-pdf] [--rawdata <dir> --raw-sample N] [--runs 5] [--out <thư mục>]
//   DlpExtractorBench once <file>        — chỉ gọi TryReadFile 1 lần (dùng với tools/perf/count-opens.sh để đếm open/stat bằng strace)
// Các bước đo (giống thứ tự trong TryReadFile, xem FileReaderService.cs):
//   sniff    = FileTypeSniffer.Detect(path)                        (mở file lần 1, đọc byte đầu, với ZIP: đọc danh mục)
//   extract  = duyệt hết extractor.Extract(path) + nối block vào StringBuilder có trần 2.000.000 ký tự như TryReadFile (mở file lần 2)
//   tostring = StringBuilder.ToString()
//   nfc      = Normalizer.ToNfc(content)
//   hash     = ContentHash.Compute(content)                        (FileProcessor gọi SAU TryReadFile — tính riêng)
//   tryread  = TryReadFile(path) trọn gói                           → so Σ(sniff..nfc) với tryread (AC T05: ±15 %)
// Mỗi bước: wall ms, CPU ms, byte cấp phát, số lần GC Gen0/1/2. Đỉnh RSS từng file: reset bằng /proc/self/clear_refs ("5") trước lượt
// tryread cuối, đọc VmHWM sau đó (chỉ Linux; nơi khác để trống).
using System.Text;
using DlpService.Reading;
using DlpService.Storage;
using DlpService.Text;
using DlpTools.Perf;
using static DlpTools.Perf.BenchCommon;

namespace DlpExtractorBench;

internal static class Program
{
    // Giống FileReaderService.MaxExtractedChars (private const)
    private const int MaxExtractedChars = 2_000_000;

    private static readonly string[] Stages = ["sniff", "extract", "tostring", "nfc", "hash", "tryread"];

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? mode = args.FirstOrDefault();
        if (mode == "once" && args.Length >= 2)
        {
            var r = new FileReaderService().TryReadFile(args[1]);
            Console.WriteLine($"Success={r.Success} chars={r.Content?.Length ?? 0} incomplete={r.IncompleteReason}");
            return 0;
        }

        string? dataset = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
        var files = SelectFiles(dataset, Arg(args, "--sets"), Arg(args, "--rawdata"), int.Parse(Arg(args, "--raw-sample") ?? "0", Inv));
        if (mode != "stages" || files.Count == 0)
        {
            Console.Error.WriteLine("Cách dùng:\n  DlpExtractorBench stages <dataset> [--sets ...] [--rawdata <dir> --raw-sample N] [--runs 5] [--out <thư mục>]\n  DlpExtractorBench once <file>");
            return 1;
        }
        return RunStages(files, int.Parse(Arg(args, "--runs") ?? "5", Inv), Arg(args, "--out") ?? Path.Combine("tools", "perf", "baseline", "extractor"));
    }

    private sealed record StageStat(string Stage, double WallMs, double CpuMs, double AllocBytes, int Gen0, int Gen1, int Gen2, double Cv);

    private sealed record FileStat(BenchFile File, long SizeBytes, int Chars, string? Incomplete, string Extractor, int Blocks, List<StageStat> Stages, double PeakRssMb);

    private static int RunStages(List<BenchFile> files, int runs, string outDir)
    {
        Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
        var reader = new FileReaderService();
        var results = new List<FileStat>();
        using var csv = new StreamWriter(Path.Combine(outDir, $"extractor_benchmark-{stamp}.csv"), false, new UTF8Encoding(false));
        csv.WriteLine("dataset,file_id,format,size_bytes,chars,stage,wall_ms,cpu_ms,alloc_bytes,gen0,gen1,gen2,peak_ws_mb,open_count,run");

        // Làm nóng: mỗi định dạng 1 lần trên file đầu tiên của định dạng đó (JIT của extractor + thư viện OpenXml/PdfPig)
        foreach (var f in files.GroupBy(f => f.Format).Select(g => g.First()))
        {
            for (int w = 0; w < 3; w++) reader.TryReadFile(f.Path);
        }

        foreach (var (file, fi) in files.Select((f, i) => (f, i)))
        {
            var extractor = reader.FindExtractor(file.Path);
            if (extractor == null)
            {
                Console.WriteLine($"[{fi + 1}/{files.Count}] {file.Id}: không có extractor — bỏ qua");
                continue;
            }

            var perStage = Stages.ToDictionary(s => s, _ => new List<Sample>());
            int chars = 0, blocks = 0;
            string? incomplete = null;
            double peakRss = double.NaN;
            for (int run = 0; run <= runs; run++) // run 0 = làm nóng file này (đưa vào page cache), không ghi
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var sniff = Measure(() => FileTypeSniffer.Detect(file.Path));

                var sb = new StringBuilder();
                int nBlocks = 0;
                Sample extract;
                try
                {
                    extract = Measure(() => nBlocks = AppendBounded(extractor, file.Path, sb));
                }
                catch (Exception ex)
                {
                    // Hỏng / có mật khẩu / vượt ngân sách ZIP: TryReadFile vẫn đo được bình thường (ghi incomplete) — ở đây chỉ ghi tên lỗi
                    Console.WriteLine($"  {file.Id}: Extract ném {ex.GetType().Name} — chỉ đo sniff + tryread");
                    extract = default;
                }
                string content = "";
                var toString = Measure(() => content = sb.ToString());
                string normalized = "";
                var nfc = Measure(() => normalized = Normalizer.ToNfc(content));
                var hash = Measure(() => ContentHash.Compute(normalized));

                bool last = run == runs;
                if (last) ResetPeakRss();
                FileReadResult? read = null;
                var tryRead = Measure(() => read = reader.TryReadFile(file.Path));
                if (last) peakRss = ReadPeakRssMb();

                if (run == 0) continue;
                chars = read!.Content?.Length ?? 0;
                incomplete = read.Success ? read.IncompleteReason : "read_failed:" + read.ErrorMessage;
                blocks = nBlocks;
                foreach (var (stage, s) in new[] { ("sniff", sniff), ("extract", extract), ("tostring", toString), ("nfc", nfc), ("hash", hash), ("tryread", tryRead) })
                {
                    perStage[stage].Add(s);
                    csv.WriteLine(string.Create(Inv,
                        $"{file.Set},{file.Id},{file.Format},{new FileInfo(file.Path).Length},{chars},{stage},{F(s.WallMs)},{F(s.CpuMs)},{s.AllocBytes},{s.Gen0},{s.Gen1},{s.Gen2},{(stage == "tryread" && last ? F(peakRss, "0.0") : "")},,{run}"));
                }
            }

            var stats = Stages.Select(st =>
            {
                var l = perStage[st];
                return new StageStat(st, Median(l.Select(x => x.WallMs)), Median(l.Select(x => x.CpuMs)), Median(l.Select(x => (double)x.AllocBytes)),
                    l.Sum(x => x.Gen0), l.Sum(x => x.Gen1), l.Sum(x => x.Gen2), Cv(l.Select(x => x.WallMs)));
            }).ToList();
            var fsx = new FileStat(file, new FileInfo(file.Path).Length, chars, incomplete, extractor.GetType().Name, blocks, stats, peakRss);
            results.Add(fsx);
            var tr = stats.Single(s => s.Stage == "tryread");
            Console.WriteLine(string.Create(Inv,
                $"[{fi + 1}/{files.Count}] {file.Id}: {fsx.SizeBytes / 1048576.0:0.00} MB → {chars:N0} ký tự · tryread {tr.WallMs:0.0} ms (CV {tr.Cv:P0}) · alloc {tr.AllocBytes / 1048576.0:0.0} MB ({tr.AllocBytes / Math.Max(1, fsx.SizeBytes):0.0}× byte file) · đỉnh RSS {peakRss:0} MB {incomplete}"));
        }

        File.WriteAllText(Path.Combine(outDir, $"extractor-report-{stamp}.md"), Report(results, runs), new UTF8Encoding(false));
        Console.WriteLine($"Đã ghi {outDir}/extractor_benchmark-{stamp}.csv và extractor-report-{stamp}.md");
        return 0;
    }

    // Đúng cách TryReadFile nối block: bỏ khối Unreadable, '\n' giữa các khối, dừng khi đủ MaxExtractedChars
    private static int AppendBounded(IContentExtractor extractor, string path, StringBuilder sb)
    {
        int n = 0;
        foreach (var block in extractor.Extract(path))
        {
            n++;
            if (block.Kind == BlockKind.Unreadable) continue;
            int separator = sb.Length > 0 ? 1 : 0;
            int remaining = MaxExtractedChars - sb.Length - separator;
            if (remaining <= 0) break;
            if (separator == 1) sb.Append('\n');
            if (block.Text.Length > remaining)
            {
                sb.Append(block.Text, 0, remaining);
                break;
            }
            sb.Append(block.Text);
        }
        return n;
    }

    // Linux: ghi "5" vào /proc/self/clear_refs → kernel đặt lại đỉnh RSS (VmHWM) của CHÍNH tiến trình này về RSS hiện tại
    private static void ResetPeakRss()
    {
        try
        {
            File.WriteAllText("/proc/self/clear_refs", "5");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static double ReadPeakRssMb()
    {
        try
        {
            string? line = File.ReadLines("/proc/self/status").FirstOrDefault(l => l.StartsWith("VmHWM:", StringComparison.Ordinal));
            return line == null ? double.NaN : long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], Inv) / 1024.0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return double.NaN;
        }
    }

    private static string Report(List<FileStat> files, int runs)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Benchmark File Reader / Extractor theo bước (T05) — {DateTime.Now:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine("| Mục | Giá trị |\n|---|---|");
        md.Append(Environment());
        md.AppendLine($"| Số lần đo | {runs} (+1 làm nóng / file, page cache ấm), báo cáo median |");
        md.AppendLine();
        md.AppendLine("## 1. Thời gian (ms, median) theo bước");
        md.AppendLine();
        md.AppendLine("Σ = sniff + extract + tostring + nfc (= phần việc của TryReadFile); lệch = Σ / tryread − 1 (AC ±15 %). hash là bước của FileProcessor (sau TryReadFile).");
        md.AppendLine();
        md.AppendLine("| Bộ | File | MB | Ký tự | Extractor | Block | sniff | extract | tostring | nfc | hash | **tryread** | CV | Σ lệch | CPU/wall tryread | incomplete |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var f in files)
        {
            var s = f.Stages.ToDictionary(x => x.Stage);
            double sum = s["sniff"].WallMs + s["extract"].WallMs + s["tostring"].WallMs + s["nfc"].WallMs;
            md.AppendLine(string.Create(Inv,
                $"| {f.File.Set} | {f.File.Id} | {f.SizeBytes / 1048576.0:0.00} | {f.Chars:N0} | {f.Extractor} | {f.Blocks:N0} | {s["sniff"].WallMs:0.0} | {s["extract"].WallMs:0.0} | {s["tostring"].WallMs:0.0} | {s["nfc"].WallMs:0.0} | {s["hash"].WallMs:0.0} | **{s["tryread"].WallMs:0.0}** | {s["tryread"].Cv:P0} | {sum / Math.Max(1e-9, s["tryread"].WallMs) - 1:P0} | {s["tryread"].CpuMs / Math.Max(1e-9, s["tryread"].WallMs):0.00} | {f.Incomplete} |"));
        }
        md.AppendLine();
        md.AppendLine("## 2. Cấp phát (MB, median) và đỉnh RSS");
        md.AppendLine();
        md.AppendLine("Hệ số = cấp phát của tryread / kích thước file. Đỉnh RSS = VmHWM sau lượt tryread cuối (đã reset ngay trước), gồm cả nền của tiến trình đo (~50–80 MB).");
        md.AppendLine();
        md.AppendLine("| Bộ | File | MB file | sniff | extract | tostring | nfc | hash | **tryread** | hệ số | Gen0/1/2 (tryread, tổng các lượt) | đỉnh RSS MB |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var f in files)
        {
            var s = f.Stages.ToDictionary(x => x.Stage);
            string Mb(string st) => (s[st].AllocBytes / 1048576.0).ToString("0.00", Inv);
            md.AppendLine(string.Create(Inv,
                $"| {f.File.Set} | {f.File.Id} | {f.SizeBytes / 1048576.0:0.00} | {Mb("sniff")} | {Mb("extract")} | {Mb("tostring")} | {Mb("nfc")} | {Mb("hash")} | **{Mb("tryread")}** | {s["tryread"].AllocBytes / Math.Max(1, f.SizeBytes):0.0}× | {s["tryread"].Gen0}/{s["tryread"].Gen1}/{s["tryread"].Gen2} | {F(f.PeakRssMb, "0")} |"));
        }
        md.AppendLine();
        md.AppendLine("## 3. Đọc kết quả (theo kế hoạch §7)");
        md.AppendLine();
        md.AppendLine("- **B5** (TXT/CSV nhiều bản sao): hệ số cấp phát của D-txt/D-csv ≥ ~6× → xác nhận; xem extract so với tostring/nfc.");
        md.AppendLine("- **B6** (bản sao ở FileReaderService): tostring + nfc + hash so với extract.");
        md.AppendLine("- **B3** (DOM): đỉnh RSS / cấp phát của D-office theo kích thước XML.");
        md.AppendLine("- **B4** (PDF): ms / trang và cấp phát / trang ở D-pdf; PDF scan: thời gian tới `needs_ocr`.");
        md.AppendLine("- **B1** (mở nhiều lần): đếm bằng `tools/perf/count-opens.sh` (strace), không đo ở bảng này.");
        md.AppendLine("- CPU/wall ≈ 1 → CPU-bound; ≪ 1 → chờ I/O (page cache ấm nên hiếm); > 1 → có luồng GC nền chạy song song.");
        return md.ToString();
    }
}
