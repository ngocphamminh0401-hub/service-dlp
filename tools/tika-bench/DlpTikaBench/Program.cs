// [Thay đổi] File mới: benchmark so sánh trích text giữa extractor hiện tại của DlpService và Apache Tika Server.
// Với mỗi file mẫu: trích bằng 2 cách -> đo thời gian, đếm ký tự -> chạy RegexScanner trên cả 2 text -> so số chỗ khớp.
// Tiêu chí quan trọng nhất với DLP là "số chỗ khớp": Tika ra nhiều hơn = đọc được phần mà extractor cũ bỏ sót.
// KHÔNG in và KHÔNG ghi nội dung file ra đâu cả, chỉ ghi số liệu + tên rule (đúng quy tắc không log dữ liệu nhạy cảm).
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpTikaBench;

internal static class TikaBenchRunner
{
    // Cùng giới hạn với FileReaderService để so sánh công bằng (text dài hơn thì cả 2 bên cùng bị cắt)
    private const int MaxChars = 2_000_000;

    // Số file chạy "khởi động" cho MỖI bên trước khi đo: lần đầu Tika nạp lớp Java, .NET JIT + compile regex nên chậm bất thường
    private const int WarmupFiles = 3;

    // Kết quả 1 lần trích text của 1 bên (cũ hoặc Tika)
    private sealed record Extraction(bool Ok, string Text, double Ms, string? Error);

    // Kết quả đo 1 file (không chứa nội dung file)
    private sealed record Row(
        string File, string Ext, long Size,
        bool OldOk, double OldMs, int OldChars, int OldMatches, HashSet<string> OldRules, string? OldError,
        bool TikaOk, double TikaMs, int TikaChars, int TikaMatches, HashSet<string> TikaRules, string? TikaError);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // [Thay đổi] Chế độ benchmark có đáp án đúng (bộ file do DlpTestGen sinh) nằm ở CorpusBench.cs
        // [Thay đổi] Tích hợp Tika native: kiểm tra qua đúng FileReaderService của DlpService (ReaderNativeCheck.cs)
        // [Thay đổi] Benchmark thư mục đang theo dõi: so kết luận DLP giữa bộ đọc cũ và Tika native (FolderCompare.cs)
        // [Thay đổi] Đánh giá trên dữ liệu CÓ NHÃN (rawdata của DlpBaseline): bộ đọc cũ so với Tika native (LabelledCompare.cs)
        if (args.Length > 0 && args[0] == "labelled-compare")
        {
            return LabelledCompare.Run(args);
        }

        if (args.Length > 0 && args[0] == "folder-compare")
        {
            return FolderCompare.Run(args);
        }

        if (args.Length > 0 && args[0] == "reader-native")
        {
            return ReaderNativeCheck.Run(args);
        }

        // [Thay đổi] Kiểm chứng nguyên nhân Tika tốn CPU: bấm giờ tách từng phần (TikaProfile.cs)
        if (args.Length > 0 && args[0] == "tika-profile")
        {
            return TikaProfile.Run(args);
        }

        // [Thay đổi] Đo lại sau khi sửa 3 lỗi của Tika: corpus-extract nhận --extractor legacy|supplement|tika để chấm điểm
        // ĐÚNG đường FileReaderService ở từng chế độ (phải cấu hình trước khi CorpusBench tạo FileReaderService)
        if (args.Length > 0 && args[0] == "corpus-extract")
        {
            args = DlpTools.Shared.ExtractorSwitch.Apply(args);
            int code = await CorpusBench.Run(args);
            DlpTools.Shared.ExtractorSwitch.Shutdown();
            return code;
        }

        if (args.Length > 0 && args[0] is "corpus-all" or "corpus-list" or "corpus-score" or "corpus-extract-native" or "corpus-compare" or "corpus-detect")
        {
            return await CorpusBench.Run(args);
        }

        // ---- Đọc tham số: <thư mục mẫu> [--per-ext N] [--tika URL] [--exts .docx,.pdf] ----
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("Cách dùng: DlpTikaBench <thư-mục-mẫu> [--per-ext 30] [--tika http://127.0.0.1:9998] [--exts .docx,.pdf]");
            return 1;
        }
        string folder = args[0];
        int perExt = 30;
        string tikaUrl = "http://127.0.0.1:9998";
        HashSet<string>? onlyExts = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--per-ext": perExt = int.Parse(args[++i]); break;
                case "--tika": tikaUrl = args[++i].TrimEnd('/'); break;
                case "--exts": onlyExts = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim().ToLowerInvariant()).ToHashSet(); break;
            }
        }
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"Không thấy thư mục mẫu: {folder}");
            return 1;
        }

        // ---- Khởi tạo giống Worker: nạp rule, tạo scanner, tạo reader (reader tự nạp mọi extractor) ----
        var logger = NullLogger.Instance; // logger "câm": ghi log làm lệch thời gian đo
        var patterns = new RuleLoader(logger).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
        if (patterns.Count == 0)
        {
            Console.Error.WriteLine("Không nạp được rule nào, benchmark sẽ vô nghĩa.");
            return 1;
        }
        var scanner = new RegexScanner(patterns);
        var reader = new FileReaderService();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        // Kiểm tra Tika Server sống trước khi đo, báo lỗi rõ ràng thay vì để mọi file đều "lỗi"
        try
        {
            string hello = await http.GetStringAsync(tikaUrl + "/tika");
            Console.WriteLine($"Tika: {hello.Trim()}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Không gọi được Tika Server ở {tikaUrl} ({ex.GetType().Name}). Server còn chạy không?");
            return 1;
        }

        // ---- Chọn file: tối đa perExt file mỗi đuôi, thứ tự cố định để lần chạy sau so sánh được ----
        // Mặc định chỉ lấy đuôi mà extractor cũ hỗ trợ; muốn thử đuôi khác (.doc, .rtf...) thì truyền --exts
        var wantedExts = onlyExts ?? reader.SupportedExtensions.Select(e => e.ToLowerInvariant()).ToHashSet();
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => wantedExts.Contains(Path.GetExtension(f).ToLowerInvariant()) && !Path.GetFileName(f).StartsWith("~$"))
            .GroupBy(f => Path.GetExtension(f).ToLowerInvariant())
            .SelectMany(g => g.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(perExt))
            .ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("Không có file nào khớp đuôi cần đo.");
            return 1;
        }
        Console.WriteLine($"Đo {files.Count} file: {string.Join(", ", files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).Select(g => $"{g.Key}={g.Count()}"))}");

        // ---- Khởi động (không tính số liệu) ----
        foreach (var f in files.Take(WarmupFiles))
        {
            ExtractOld(reader, f);
            await ExtractTikaAsync(http, tikaUrl, f);
        }
        Console.WriteLine($"Đã khởi động {Math.Min(WarmupFiles, files.Count)} file mỗi bên.");

        // ---- Đo thật ----
        var rows = new List<Row>();
        int done = 0;
        foreach (var f in files)
        {
            var old = ExtractOld(reader, f);
            var tika = await ExtractTikaAsync(http, tikaUrl, f);

            var (oldMatches, oldRules) = Scan(scanner, old);
            var (tikaMatches, tikaRules) = Scan(scanner, tika);

            rows.Add(new Row(Path.GetFileName(f), Path.GetExtension(f).ToLowerInvariant(), new FileInfo(f).Length,
                old.Ok, old.Ms, old.Text.Length, oldMatches, oldRules, old.Error,
                tika.Ok, tika.Ms, tika.Text.Length, tikaMatches, tikaRules, tika.Error));

            if (++done % 50 == 0)
            {
                Console.WriteLine($"  ... {done}/{files.Count}");
            }
        }

        PrintSummary(rows);
        string csv = WriteCsv(rows);
        Console.WriteLine($"\nChi tiết từng file (không có nội dung): {csv}");
        return 0;
    }

    // Cách CŨ: đúng đường FileReaderService.TryReadFile mà service đang dùng (đã gồm NFC + cắt 2 triệu ký tự)
    private static Extraction ExtractOld(FileReaderService reader, string path)
    {
        var sw = Stopwatch.StartNew();
        var r = reader.TryReadFile(path);
        sw.Stop();
        return new Extraction(r.Success, r.Content ?? string.Empty, sw.Elapsed.TotalMilliseconds, r.ErrorMessage);
    }

    // Cách TIKA: PUT file lên /tika, xin text/plain. Tika tự nhận dạng loại file bằng NỘI DUNG, không dựa vào đuôi.
    // Áp NFC + cắt 2 triệu ký tự giống bên cũ để so công bằng.
    private static async Task<Extraction> ExtractTikaAsync(HttpClient http, string baseUrl, string path)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var stream = File.OpenRead(path);
            using var request = new HttpRequestMessage(HttpMethod.Put, baseUrl + "/tika") { Content = new StreamContent(stream) };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            using var response = await http.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                // 422 = Tika không xử lý được (file hỏng, có mật khẩu...); chỉ ghi mã trạng thái, không ghi thân phản hồi
                sw.Stop();
                return new Extraction(false, string.Empty, sw.Elapsed.TotalMilliseconds, $"HTTP {(int)response.StatusCode}");
            }
            string text = Normalizer.ToNfc(await response.Content.ReadAsStringAsync());
            if (text.Length > MaxChars)
            {
                text = text[..MaxChars];
            }
            sw.Stop();
            return new Extraction(true, text, sw.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Extraction(false, string.Empty, sw.Elapsed.TotalMilliseconds, ex.GetType().Name);
        }
    }

    // Quét regex trên text đã trích; trả (số chỗ khớp, tập mã rule có khớp). Trích thất bại → 0 chỗ khớp.
    private static (int Matches, HashSet<string> Rules) Scan(RegexScanner scanner, Extraction e)
    {
        if (!e.Ok || e.Text.Length == 0)
        {
            return (0, []);
        }
        var scan = scanner.Scan(e.Text);
        return (scan.Matches.Count, scan.Matches.Select(m => m.RuleId ?? m.PatternName).ToHashSet());
    }

    private static void PrintSummary(List<Row> rows)
    {
        Console.WriteLine("\n===== TỔNG HỢP THEO ĐỊNH DẠNG =====");
        Console.WriteLine($"{"đuôi",-6} {"file",5} | {"cũ OK",5} {"Tika OK",7} | {"cũ ms(median)",13} {"Tika ms(median)",15} | {"cũ khớp",8} {"Tika khớp",9} | {"Tika>cũ",7} {"Tika<cũ",7} {"chỉ cũ đọc",10} {"chỉ Tika đọc",12}");
        foreach (var g in rows.GroupBy(r => r.Ext).OrderBy(g => g.Key))
        {
            var list = g.ToList();
            // So số chỗ khớp CHỈ trên file cả 2 bên cùng đọc được (nếu không so sánh sẽ lệch)
            var both = list.Where(r => r.OldOk && r.TikaOk).ToList();
            Console.WriteLine($"{g.Key,-6} {list.Count,5} | {list.Count(r => r.OldOk),5} {list.Count(r => r.TikaOk),7} | " +
                $"{Median(list.Where(r => r.OldOk).Select(r => r.OldMs)),13:N1} {Median(list.Where(r => r.TikaOk).Select(r => r.TikaMs)),15:N1} | " +
                $"{both.Sum(r => r.OldMatches),8} {both.Sum(r => r.TikaMatches),9} | " +
                $"{both.Count(r => r.TikaMatches > r.OldMatches),7} {both.Count(r => r.TikaMatches < r.OldMatches),7} " +
                $"{list.Count(r => r.OldOk && !r.TikaOk),10} {list.Count(r => !r.OldOk && r.TikaOk),12}");
        }

        // Nguyên nhân thất bại, gom theo thông báo ngắn
        var errors = rows.Where(r => !r.OldOk || !r.TikaOk).ToList();
        if (errors.Count > 0)
        {
            Console.WriteLine("\n===== LỖI =====");
            foreach (var grp in rows.Where(r => !r.OldOk).GroupBy(r => $"cũ  {r.Ext}: {r.OldError}"))
                Console.WriteLine($"  {grp.Key} x{grp.Count()}");
            foreach (var grp in rows.Where(r => !r.TikaOk).GroupBy(r => $"Tika {r.Ext}: {r.TikaError}"))
                Console.WriteLine($"  {grp.Key} x{grp.Count()}");
        }

        // Rule mà 1 bên khớp còn bên kia thì không (cả 2 đọc được) — chỉ in tên rule + số file, không in giá trị
        var onlyTika = new Dictionary<string, int>();
        var onlyOld = new Dictionary<string, int>();
        foreach (var r in rows.Where(r => r.OldOk && r.TikaOk))
        {
            foreach (var rule in r.TikaRules.Except(r.OldRules)) onlyTika[rule] = onlyTika.GetValueOrDefault(rule) + 1;
            foreach (var rule in r.OldRules.Except(r.TikaRules)) onlyOld[rule] = onlyOld.GetValueOrDefault(rule) + 1;
        }
        Console.WriteLine("\n===== RULE CHỈ MỘT BÊN KHỚP (số file) =====");
        Console.WriteLine("  Chỉ Tika thấy : " + (onlyTika.Count == 0 ? "(không có)" : string.Join(", ", onlyTika.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}={k.Value}"))));
        Console.WriteLine("  Chỉ cũ thấy   : " + (onlyOld.Count == 0 ? "(không có)" : string.Join(", ", onlyOld.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}={k.Value}"))));

        Console.WriteLine("\nLưu ý: thời gian của Tika gồm cả truyền file qua HTTP localhost; RAM chưa đo (Tika chạy ở tiến trình Java riêng).");
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    // Ghi CSV từng file: chỉ tên file + số liệu, TUYỆT ĐỐI không có nội dung
    private static string WriteCsv(List<Row> rows)
    {
        string path = Path.Combine(Directory.GetCurrentDirectory(), $"tika-bench-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder("file,ext,size,old_ok,old_ms,old_chars,old_matches,old_error,tika_ok,tika_ms,tika_chars,tika_matches,tika_error\n");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",", Csv(r.File), r.Ext, r.Size, r.OldOk, r.OldMs.ToString("F1"), r.OldChars, r.OldMatches, Csv(r.OldError),
                r.TikaOk, r.TikaMs.ToString("F1"), r.TikaChars, r.TikaMatches, Csv(r.TikaError)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static string Csv(string? s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
}
