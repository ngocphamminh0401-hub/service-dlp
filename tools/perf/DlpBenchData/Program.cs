// [Thay đổi] File mới (Benchmark T02): sinh dataset tổng hợp, tất định (seed cố định), kèm manifest + sha256 để Before/After dùng ĐÚNG cùng dữ liệu.
//   DlpBenchData gen [--out <thư mục>] [--profile smoke|full] [--seed 20261118] [--font <ttf>] [--rawdata <thư mục>] [--no-verify]
//     smoke: bộ nhỏ để kiểm script trên máy dev (vài chục MB) · full: đúng kích thước trong kế hoạch, CHỈ chạy trên máy bench (vài GB)
//     --rawdata: thêm danh sách D-burst (đường dẫn tương đối, xáo theo seed) lấy từ rawdata có nhãn — không chép file
//     sau khi sinh, mỗi file được đọc lại bằng FileReaderService + quét RegexScanner (MinConfidence 0,60) → ghi số ký tự / số chỗ khớp
//     vào manifest để biết bộ dữ liệu đúng mục đích (VD D-clean phải 0 chỗ khớp). --no-verify bỏ bước này.
// Kết quả: <out>/<bộ>/<file>, <out>/manifest.csv, <out>/dataset.md. Không ghi giá trị khớp ra đâu cả.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DlpService.Reading;
using DlpService.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpBenchData;

/// <summary>1 file cần sinh: bộ (D-*), tên file, định dạng, mô tả tham số, hàm tạo byte (nhận Random đã seed riêng cho file).</summary>
internal sealed record FileSpec(string Set, string File, string Format, string Param, Func<Random, byte[]> Make);

internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8 = new(false);

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // cần cho bản CP1258 của D-txt
        if (args.FirstOrDefault() != "gen")
        {
            Console.Error.WriteLine("Cách dùng: DlpBenchData gen [--out <thư mục>] [--profile smoke|full] [--seed N] [--font <ttf>] [--rawdata <thư mục>] [--no-verify]");
            return 1;
        }

        string profile = Arg(args, "--profile") ?? "smoke";
        if (profile is not ("smoke" or "full"))
        {
            Console.Error.WriteLine("--profile chỉ nhận smoke hoặc full");
            return 1;
        }
        int seed = int.Parse(Arg(args, "--seed") ?? "20261118", Inv);
        string outDir = Path.GetFullPath(Arg(args, "--out")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dlp-benchdata", profile));
        string? fontPath = Arg(args, "--font");
        string? rawdata = Arg(args, "--rawdata");
        bool verify = !args.Contains("--no-verify");

        // Câu đệm + từ khoá: lọc bằng CHÍNH bộ rule của service (giống DlpSizeBench.Generate)
        var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
        var keywords = patterns.SelectMany(p => p.Keywords.Concat(p.PrimaryKeywords).Concat(p.SecondaryKeywords))
            .Where(k => k.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var filler = Synth.FilterFiller(new RegexScanner(patterns, minConfidence: 0), keywords);
        if (filler.Count < 10)
        {
            Console.Error.WriteLine($"Chỉ còn {filler.Count} câu đệm hợp lệ — quá ít, dừng.");
            return 1;
        }
        Console.WriteLine($"Câu đệm hợp lệ: {filler.Count} · từ khoá: {keywords.Count} · profile {profile} · seed {seed} · ra {outDir}");

        var pdf = new PdfWriter(fontPath != null ? File.ReadAllBytes(fontPath) : null);
        var specs = BuildSpecs(profile, filler, keywords, pdf);

        Directory.CreateDirectory(outDir);
        var reader = verify ? new FileReaderService() : null;
        var scanner = verify ? new RegexScanner(patterns) : null;
        var manifest = new StringBuilder("set,file,format,param,size_bytes,sha256,read_chars,incomplete_reason,match_count,scan_complete\n");
        long totalBytes = 0;
        for (int i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            // Seed RIÊNG cho từng file = seed chung + số thứ tự (không dùng string.GetHashCode — giá trị đó đổi mỗi lần chạy)
            byte[] bytes = spec.Make(new Random(seed + i * 7919));
            string rel = Path.Combine(spec.Set, spec.File);
            string full = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            totalBytes += bytes.Length;

            string chars = "", reason = "", matches = "", complete = "";
            if (verify)
            {
                var read = reader!.TryReadFile(full);
                chars = (read.Content?.Length ?? 0).ToString(Inv);
                reason = read.Success ? read.IncompleteReason ?? "" : "read_failed";
                if (read.Success && !string.IsNullOrEmpty(read.Content))
                {
                    var scan = scanner!.Scan(read.Content);
                    matches = scan.Matches.Count.ToString(Inv);
                    complete = scan.IsComplete ? "1" : "0";
                }
            }
            manifest.Append(string.Create(Inv, $"{spec.Set},{rel.Replace('\\', '/')},{spec.Format},{spec.Param},{bytes.Length},{Convert.ToHexString(SHA256.HashData(bytes))},{chars},{reason},{matches},{complete}\n"));
            Console.WriteLine(string.Create(Inv, $"  [{i + 1}/{specs.Count}] {rel}: {bytes.Length / 1048576.0:0.00} MB, ký tự đọc {chars}, khớp {matches} {reason}"));
        }

        string manifestPath = Path.Combine(outDir, "manifest.csv");
        File.WriteAllText(manifestPath, manifest.ToString(), Utf8);
        int burstLists = rawdata != null ? WriteBurstLists(rawdata, outDir, seed) : 0;
        string manifestSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)));

        var md = new StringBuilder();
        md.AppendLine("# Dataset benchmark (T02)");
        md.AppendLine();
        md.AppendLine($"- Sinh lúc: {DateTime.Now:yyyy-MM-dd HH:mm} · công cụ `tools/perf/DlpBenchData gen --profile {profile} --seed {seed}`");
        md.AppendLine($"- Thư mục: `{outDir}` · {specs.Count} file · {totalBytes / 1048576.0:0.0} MB");
        md.AppendLine($"- **sha256(manifest.csv): `{manifestSha}`** — Before/After phải trùng giá trị này");
        md.AppendLine($"- PDF: {(pdf.KeepsDiacritics ? $"font TTF `{Path.GetFileName(fontPath)}`, giữ dấu" : "Helvetica chuẩn, **chữ đã bỏ dấu** (máy không có font TTF; truyền --font để giữ dấu)")}");
        md.AppendLine($"- Câu đệm hợp lệ {filler.Count}, từ khoá {keywords.Count} (lấy từ bộ rule đang nạp, rules_ver `{RuleLoader.ComputeRulesVersion(Path.Combine(AppContext.BaseDirectory, "rules"))[..12]}`)");
        if (burstLists > 0) md.AppendLine($"- D-burst: {burstLists} danh sách trong `burst/` (đường dẫn tương đối tới `{rawdata}`)");
        md.AppendLine("- Dữ liệu hoàn toàn giả lập (câu phong cảnh, số CCCD/SĐT ngẫu nhiên đúng định dạng), không có dữ liệu thật.");
        md.AppendLine();
        md.AppendLine("| Bộ | Số file | MB | Mục đích |");
        md.AppendLine("|---|---|---|---|");
        foreach (var g in specs.GroupBy(s => s.Set))
        {
            long mb = g.Sum(s => new FileInfo(Path.Combine(outDir, s.Set, s.File)).Length);
            md.AppendLine(string.Create(Inv, $"| {g.Key} | {g.Count()} | {mb / 1048576.0:0.0} | {Purpose(g.Key)} |"));
        }
        File.WriteAllText(Path.Combine(outDir, "dataset.md"), md.ToString(), Utf8);
        Console.WriteLine($"Xong: {specs.Count} file, {totalBytes / 1048576.0:0.0} MB · sha256(manifest) {manifestSha}");
        return 0;
    }

    private static string Purpose(string set) => set switch
    {
        "D-clean" => "R1: văn bản trung tính, không từ khoá — chi phí regex không cần thiết",
        "D-kw" => "R2: dày từ khoá, gần như không có giá trị khớp — chi phí dò từ khoá",
        "D-csv" => "R3/F2: CSV nhiều dòng số + CCCD/SĐT — O(N×M), Split/JoinRow",
        "D-match" => "R5: rất nhiều chỗ khớp hợp lệ — cấp phát Match/Substring",
        "D-txt" => "F1: TXT lớn UTF-8 / CP1258 / vượt 10 MB",
        "D-office" => "F3/F4: DOCX XML lớn (DOM), PPTX nhiều slide, DOCX vượt ngân sách ZIP",
        "D-pdf" => "F5/F6: PDF chữ nhiều trang, PDF scan toàn ảnh, PDF trộn (scan đứng trước)",
        _ => ""
    };

    // ======================================================== Danh sách file theo profile ========================================================

    private static List<FileSpec> BuildSpecs(string profile, List<string> filler, List<string> keywords, PdfWriter pdf)
    {
        bool full = profile == "full";
        var specs = new List<FileSpec>();

        // D-clean: văn bản trung tính (txt + docx)
        foreach (int chars in full ? new[] { 200_000, 2_000_000 } : [20_000, 200_000])
        {
            foreach (var fmt in new[] { "txt", "docx" })
            {
                specs.Add(new("D-clean", $"clean-{Tag(chars)}.{fmt}", fmt, $"chars={chars}",
                    r => Write(fmt, Synth.FillerLines(filler, chars, r))));
            }
        }

        // D-kw: mỗi dòng 1 câu đệm + 4 từ khoá ngẫu nhiên (không chữ số → gần như không có giá trị cho regex khớp)
        foreach (int chars in full ? new[] { 200_000, 2_000_000 } : [20_000])
        {
            foreach (var fmt in full ? new[] { "txt", "docx" } : ["txt"])
            {
                specs.Add(new("D-kw", $"kw-{Tag(chars)}.{fmt}", fmt, $"chars={chars}", r => Write(fmt, KeywordLines(filler, keywords, chars, r))));
            }
        }

        // D-csv: N dòng; mỗi dòng 1 CCCD + 1 SĐT hợp lệ + 1 mã giao dịch 14 số ("số lạ") — nhãn chỉ ở dòng tiêu đề (chế độ bảng)
        // [Thay đổi] 10k/20k/30k (trước: 10k/50k/100k): ~66 ký tự / dòng → 50k và 100k đều bị cắt ở 2.000.000 ký tự (~30k dòng) nên Scan nhận
        // CÙNG đầu vào; mọi bậc phải nằm dưới trần mới đo được độ dốc theo số dòng (R3).
        foreach (int rows in full ? new[] { 10_000, 20_000, 30_000 } : [1_000, 5_000])
        {
            specs.Add(new("D-csv", $"csv-{Tag(rows)}rows.csv", "csv", $"rows={rows}", r => Utf8.GetBytes(CsvTable(rows, r))));
        }

        // D-match: tổng ~chars ký tự, values giá trị khớp (CCCD/SĐT có nhãn ngay cạnh) rải đều giữa câu đệm
        foreach (var (chars, values) in full ? new[] { (2_000_000, 10_000), (2_000_000, 50_000) } : [(200_000, 1_000)])
        {
            specs.Add(new("D-match", $"match-{Tag(chars)}-{Tag(values)}v.txt", "txt", $"chars={chars};values={values}",
                r => Utf8.GetBytes(string.Join("\n", MatchLines(filler, chars, values, r)))));
        }

        // D-txt: TXT theo SỐ BYTE (giới hạn TxtExtractor là 10 MB, so bằng ">" nên 9,5 MB vẫn đọc; 10,5 MB → too_large)
        foreach (double mb in full ? new[] { 1.0, 5.0, 9.5 } : [1.0])
        {
            specs.Add(new("D-txt", $"txt-{mb.ToString("0.#", Inv)}mb-utf8.txt", "txt", $"bytes={mb}MB;utf8",
                r => Utf8.GetBytes(string.Join("\n", LinesForBytes(filler, (long)(mb * 1048576), r, Utf8)))));
        }
        double cpMb = full ? 5.0 : 1.0;
        specs.Add(new("D-txt", $"txt-{cpMb.ToString("0.#", Inv)}mb-cp1258.txt", "txt", $"bytes={cpMb}MB;cp1258", r =>
        {
            var cp = Encoding.GetEncoding(1258);
            return cp.GetBytes(string.Join("\n", LinesForBytes(filler, (long)(cpMb * 1048576), r, cp)));
        }));
        if (full)
        {
            specs.Add(new("D-txt", "txt-10.5mb-toolarge.txt", "txt", "bytes=10.5MB;utf8",
                r => Utf8.GetBytes(string.Join("\n", LinesForBytes(filler, (long)(10.5 * 1048576), r, Utf8)))));
        }

        // D-office: DOCX theo kích thước document.xml (~340 byte XML / dòng đệm); ngân sách ZIP DomMaxEntryMB = 24 → bản 30 MB phải ra limit_exceeded
        foreach (int xmlMb in full ? new[] { 10, 20, 30 } : [2])
        {
            int lines = xmlMb * 1048576 / 340;
            specs.Add(new("D-office", $"docx-xml{xmlMb}mb.docx", "docx", $"xml≈{xmlMb}MB;lines={lines}",
                r => Write("docx", Synth.FillerLines(filler, lines * 230, r))));
        }
        // PPTX: Writers.Pptx đặt 20 dòng / slide
        foreach (int slides in full ? new[] { 200, 500 } : [20])
        {
            specs.Add(new("D-office", $"pptx-{slides}slides.pptx", "pptx", $"slides={slides}",
                r => Write("pptx", Synth.FillerLines(filler, slides * 20 * 230, r).Take(slides * 20).ToList())));
        }

        // D-pdf: chữ / scan / trộn
        foreach (int pages in full ? new[] { 100, 500, 1000 } : [10])
        {
            specs.Add(new("D-pdf", $"pdf-text-{pages}p.pdf", "pdf", $"text_pages={pages}",
                r => pdf.Build(Synth.FillerLines(filler, PdfWriter.LinesForPages(pages) * 230, r).Take(PdfWriter.LinesForPages(pages)).ToList())));
        }
        foreach (int pages in full ? new[] { 50, 200 } : [5])
        {
            specs.Add(new("D-pdf", $"pdf-scan-{pages}p.pdf", "pdf", $"scan_pages={pages}",
                r => pdf.Build([], scanPages: pages, png: Synth.NoisePng(ScanW, ScanH, r))));
        }
        var (scanFirst, textAfter) = full ? (20, 80) : (2, 8);
        specs.Add(new("D-pdf", $"pdf-mixed-{scanFirst}s{textAfter}t.pdf", "pdf", $"scan_pages={scanFirst};text_pages={textAfter}",
            r => pdf.Build(Synth.FillerLines(filler, PdfWriter.LinesForPages(textAfter) * 230, r).Take(PdfWriter.LinesForPages(textAfter)).ToList(),
                scanPages: scanFirst, png: Synth.NoisePng(ScanW, ScanH, r))));

        return specs;
    }

    // docx/pptx qua Writers (DlpSizeBench) rồi chuẩn hoá ZIP cho tất định; txt ghi thẳng UTF-8
    private static byte[] Write(string format, List<string> lines) => format switch
    {
        "txt" => Utf8.GetBytes(string.Join("\n", lines)),
        "docx" or "pptx" or "xlsx" => Synth.Deterministic(DlpSizeBench.Writers.Write(format, lines, "bench")),
        _ => DlpSizeBench.Writers.Write(format, lines, "bench")
    };

    private static List<string> KeywordLines(List<string> filler, List<string> keywords, int chars, Random r)
    {
        var lines = new List<string>();
        int total = 0;
        while (total < chars)
        {
            string line = filler[r.Next(filler.Count)] + " — " + string.Join(", ", Enumerable.Range(0, 4).Select(_ => keywords[r.Next(keywords.Count)]));
            lines.Add(line);
            total += line.Length + 1;
        }
        return lines;
    }

    private static string CsvTable(int rows, Random r)
    {
        var sb = new StringBuilder("STT,Họ và tên,Số CCCD,Số điện thoại,Mã giao dịch\n");
        for (int i = 1; i <= rows; i++)
        {
            sb.Append(i.ToString(Inv)).Append(',').Append(Synth.Name(r)).Append(',').Append(Synth.Cccd(r)).Append(',')
              .Append(Synth.Phone(r)).Append(',').Append(Synth.Digits(r, 14)).Append('\n');
        }
        return sb.ToString();
    }

    private static List<string> MatchLines(List<string> filler, int chars, int values, Random r)
    {
        // Mỗi giá trị 1 dòng có nhãn; giữa 2 giá trị chèn số câu đệm đủ để tổng đạt ~chars
        var valueLines = Enumerable.Range(0, values)
            .Select(i => i % 2 == 0 ? $"Số CCCD của khách hàng: {Synth.Cccd(r)}" : $"Điện thoại liên hệ: {Synth.Phone(r)}").ToList();
        int valueChars = valueLines.Sum(l => l.Length + 1);
        int fillerPerGap = Math.Max(0, (chars - valueChars) / Math.Max(1, values) / 80); // ~80 ký tự / câu đệm
        var lines = new List<string>();
        foreach (var v in valueLines)
        {
            for (int k = 0; k < fillerPerGap; k++) lines.Add(filler[r.Next(filler.Count)]);
            lines.Add(v);
        }
        return lines;
    }

    // Sinh dòng đệm đến khi số BYTE (theo bảng mã đích) đạt target — dùng cho mốc kích thước file TXT
    private static List<string> LinesForBytes(List<string> filler, long targetBytes, Random r, Encoding enc)
    {
        var lines = new List<string>();
        long total = 0;
        while (true)
        {
            string line = string.Join(' ', Enumerable.Range(0, 3).Select(_ => filler[r.Next(filler.Count)]));
            long add = enc.GetByteCount(line) + 1;
            if (total + add > targetBytes) break;
            lines.Add(line);
            total += add;
        }
        return lines;
    }

    // D-burst: danh sách đường dẫn tương đối (xáo theo seed) — script E2E tự chép/rename, ở đây không chép để khỏi tốn đĩa
    private static int WriteBurstLists(string rawdata, string outDir, int seed)
    {
        if (!Directory.Exists(rawdata))
        {
            Console.Error.WriteLine($"Không thấy rawdata {rawdata} — bỏ qua D-burst");
            return 0;
        }
        var files = Directory.EnumerateFiles(rawdata, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(rawdata, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        var rand = new Random(seed);
        var shuffled = files.OrderBy(_ => rand.Next()).ToList();
        Directory.CreateDirectory(Path.Combine(outDir, "burst"));
        int written = 0;
        foreach (int n in new[] { 100, 1_000, 5_000, 10_000, 15_000 })
        {
            if (n > shuffled.Count) break; // rawdata ~25k file nên đủ cho mọi mốc
            File.WriteAllLines(Path.Combine(outDir, "burst", $"burst-{n}.txt"), shuffled.Take(n), Utf8);
            written++;
        }
        return written;
    }

    // [Thay đổi] Ảnh trang scan: 600×800 nhiễu dày ~420 KB/trang → PDF 200 trang 84 MB vượt giới hạn 50 MB của PdfExtractor (ra too_large, không đo
    // được F6). 500×700 + nhiễu thưa (xem Synth.NoisePng) ≈ 100–150 KB/trang — cỡ ảnh scan JPEG thật.
    private const int ScanW = 500, ScanH = 700;

    private static string Tag(int n) => n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.#", Inv) + "M" : n >= 1_000 ? $"{n / 1000}k" : n.ToString(Inv);

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
