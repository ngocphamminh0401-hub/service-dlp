// [Thay đổi] File mới (hạng mục 3): ảnh hưởng của KÍCH THƯỚC và ĐỊNH DẠNG file tới thời gian, bộ nhớ và độ chính xác.
//   gen  <manifest.csv của DlpBaseline> <rawdata> <thư-mục-corpus> [--per-class 4]
//        Lấy mẫu có nhãn (docx) theo 16 lớp, trích text, chèn thêm VĂN BẢN ĐỆM TRUNG TÍNH (đã kiểm: không khớp rule nào, không
//        chứa từ khoá rule nào) để đạt các mốc số ký tự, ghi ra 7 định dạng. Nhãn giữ nguyên vì phần thêm vào không nhạy cảm.
//   eval <thư-mục-corpus> [--out <thư-mục>]
//        Chạy ĐÚNG pipeline của service (FileReaderService → RegexScanner → ClassificationEngine) trên từng file, đo thời gian
//        từng bước, bộ nhớ (cấp phát / working set), độ chính xác; ghi CSV từng file + báo cáo Markdown.
// Dữ liệu tổng hợp từ rawdata (đã là dữ liệu giả lập); không ghi giá trị khớp ra bất kỳ đâu.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DlpService.Classification;
using DlpService.Reading;
using DlpService.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpSizeBench;

/// <summary>1 bậc kích thước: tên, số ký tự mục tiêu (0 = giữ nguyên gốc), vị trí nội dung gốc, số mẫu mỗi lớp (null = --per-class).</summary>
internal sealed record Tier(string Name, int TargetChars, string Placement, int? PerClass);

internal static class Bench
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Giới hạn của FileReaderService là 2.000.000 ký tự: 4 bậc đầu nằm trong giới hạn (nội dung gốc ở GIỮA file), bậc cuối vượt
    // giới hạn với nội dung gốc ở CUỐI — kiểm tra trường hợp phần nhạy cảm bị cắt mất.
    private static readonly Tier[] Tiers =
    [
        new("goc", 0, "nguyen-ban", null),
        new("20k", 20_000, "giua", null),
        new("200k", 200_000, "giua", null),
        new("1.5M", 1_500_000, "giua", 1),
        new("3M-cuoi", 3_000_000, "cuoi", 1)
    ];

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // [Thay đổi] Đo lại với Tika native: chọn bộ đọc (--extractor legacy|tika) TRƯỚC khi tạo FileReaderService
        args = DlpTools.Shared.ExtractorSwitch.Apply(args);
        int code = args.FirstOrDefault() switch
        {
            "gen" when args.Length >= 4 => Generate(args[1], args[2], args[3], ArgInt(args, "--per-class", 4)),
            "eval" when args.Length >= 2 => Evaluate(args[1], ArgStr(args, "--out") ?? Path.Combine(args[1], "results")),
            _ => Usage()
        };
        DlpTools.Shared.ExtractorSwitch.Shutdown();
        return code;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Cách dùng:\n  DlpSizeBench gen <manifest.csv> <rawdata> <thư-mục-corpus> [--per-class 4]\n  DlpSizeBench eval <thư-mục-corpus> [--out <thư-mục>]");
        return 1;
    }

    // ================================================================ GEN ================================================================

    // Câu đệm trung tính (phong cảnh, thời tiết, ẩm thực...). Câu nào khớp rule hoặc chứa từ khoá rule sẽ bị loại khi chạy.
    private static readonly string[] FillerCandidates =
    [
        "Buổi sáng trời trong xanh, những đám mây trắng trôi chậm qua dãy núi phía xa.",
        "Hàng cây ven đường đã bắt đầu thay lá, tạo nên một khung cảnh vàng rực rất đẹp.",
        "Con sông nhỏ chảy hiền hòa qua cánh đồng lúa đang vào mùa chín.",
        "Người dân trong làng thường dậy sớm để tập thể dục và đi dạo quanh hồ.",
        "Món phở bò nóng hổi là lựa chọn quen thuộc cho bữa sáng của nhiều gia đình.",
        "Cuối tuần, công viên thành phố đông vui với các hoạt động thả diều và đạp xe.",
        "Mùa hè năm nay nắng nhiều, các bãi biển miền Trung đón rất đông du khách.",
        "Những con đường lát đá cổ kính mang lại cảm giác bình yên cho người đi bộ.",
        "Vườn hoa trước sân nở rộ, thu hút ong bướm bay lượn suốt cả buổi chiều.",
        "Tiếng chim hót líu lo hòa cùng làn gió nhẹ thổi qua rặng tre đầu ngõ.",
        "Chợ quê họp từ tờ mờ sáng với đủ loại rau xanh, trái cây tươi và bánh trái.",
        "Bầu trời đêm đầy sao khiến ai ngắm nhìn cũng cảm thấy thư thái.",
        "Những cơn mưa rào mùa hạ đến nhanh rồi tạnh, để lại không khí mát mẻ dễ chịu.",
        "Đồi chè xanh mướt trải dài tít tắp, nối tiếp nhau đến tận chân trời.",
        "Các em nhỏ vui đùa bên bờ suối, tiếng cười trong trẻo vang khắp xóm.",
        "Bát canh chua cá lóc thơm mùi rau ngổ là món ăn gợi nhớ vùng sông nước.",
        "Ngọn hải đăng đứng sừng sững trên mỏm đá, soi sáng cho những con thuyền về bến.",
        "Mùa thu Hà Nội có hương hoa sữa nồng nàn lan tỏa trên từng con phố nhỏ.",
        "Thảm cỏ xanh trên đồi được tưới mát mỗi sớm, trông như một tấm thảm khổng lồ.",
        "Những chiếc thuyền nan nhỏ lặng lẽ trôi trên mặt hồ phẳng lặng như gương.",
        "Bánh xèo giòn rụm ăn kèm rau sống và nước chấm chua ngọt rất hấp dẫn.",
        "Đàn cò trắng bay về tổ khi hoàng hôn buông xuống trên cánh đồng rộng.",
        "Khu rừng nguyên sinh có nhiều loài cây cổ thụ cao lớn và tán lá rậm rạp.",
        "Mỗi dịp Tết đến, phố phường lại rực rỡ sắc đỏ của đèn lồng và câu đối.",
        "Những ngôi nhà mái ngói rêu phong nằm nép mình dưới bóng cây đa cổ thụ.",
        "Gió biển thổi lồng lộng, mang theo vị mặn mòi và tiếng sóng vỗ rì rào.",
        "Ly cà phê sữa đá buổi sáng giúp nhiều người khởi đầu ngày mới đầy năng lượng.",
        "Hoa sen nở rộ trong đầm, tỏa hương thơm dịu nhẹ suốt mùa hè.",
        "Con đường đèo quanh co uốn lượn giữa những vách núi đá vôi hùng vĩ.",
        "Ruộng bậc thang mùa nước đổ lấp lánh dưới ánh nắng như những tấm gương.",
        "Bên hiên nhà, chú mèo lười biếng nằm sưởi nắng và lim dim đôi mắt.",
        "Những quả xoài chín vàng treo lủng lẳng trên cành, tỏa mùi thơm ngọt ngào.",
        "Sương mù giăng kín thung lũng vào sáng sớm, tạo nên vẻ huyền ảo khó tả.",
        "Dòng thác trắng xóa đổ xuống từ độ cao lớn, bọt nước bắn tung tóe.",
        "Buổi chiều, người dân tụ tập bên bờ sông để câu cá và trò chuyện rôm rả.",
        "Chiếc cầu tre lắt lẻo bắc qua con kênh nhỏ là kỷ niệm tuổi thơ của nhiều người.",
        "Những hạt mưa phùn mùa xuân lất phất bay, làm cây cối đâm chồi nảy lộc.",
        "Bánh chưng xanh gói bằng lá dong là món ăn truyền thống không thể thiếu ngày Tết.",
        "Tiếng đàn bầu ngân nga trong đêm trăng làm lòng người thêm sâu lắng.",
        "Vịnh biển với hàng nghìn hòn đảo đá vôi là danh thắng nổi tiếng thế giới."
    ];

    private static int Generate(string manifestCsv, string rawdata, string outFolder, int perClass)
    {
        var sw = Stopwatch.StartNew();
        var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
        var scanner = new RegexScanner(patterns, minConfidence: 0); // kiểm tra CHẶT: kể cả chỗ khớp điểm thấp cũng loại
        var keywords = patterns.SelectMany(p => p.PrimaryKeywords.Concat(p.SecondaryKeywords).Concat(p.Keywords))
            .Where(k => k.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 1) Lọc câu đệm: không khớp rule, không chứa từ khoá rule, không chứa chữ số
        var filler = new List<string>();
        foreach (var s in FillerCandidates)
        {
            string? kw = keywords.FirstOrDefault(k => ContainsWord(s, k));
            int matches = scanner.Scan(s).Matches.Count;
            if (matches == 0 && kw == null && !s.Any(char.IsDigit)) filler.Add(s);
            else Console.WriteLine($"  loại câu đệm (khớp {matches}, từ khoá '{kw}'): {s[..Math.Min(40, s.Length)]}…");
        }
        if (filler.Count < 10) { Console.Error.WriteLine($"Chỉ còn {filler.Count} câu đệm hợp lệ — quá ít."); return 1; }
        var check = scanner.Scan(string.Join("\n", BuildFiller(filler, 60_000, new Random(1))));
        if (check.Matches.Count > 0) { Console.Error.WriteLine($"Khối đệm 60k ký tự vẫn có {check.Matches.Count} chỗ khớp — dừng."); return 1; }
        Console.WriteLine($"Câu đệm hợp lệ: {filler.Count}/{FillerCandidates.Length}; khối đệm 60.000 ký tự: 0 chỗ khớp.");

        // 2) Chọn mẫu docx theo 16 lớp (cố định seed để sinh lại được)
        var rows = ReadCsv(manifestCsv).Where(r => r["format"] == "docx" && r["read_ok"] == "1").ToList();
        var rand = new Random(20261022);
        var byClass = rows.GroupBy(r => (r["domain"], r["label"])).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.OrderBy(_ => rand.Next()).Take(perClass).ToList());
        var reader = new FileReaderService();

        Directory.CreateDirectory(outFolder);
        var manifest = new StringBuilder("path,id,domain,label,truth,format,tier,target_chars,placement,orig_chars,total_chars,size_bytes\n");
        int written = 0;
        foreach (var (cls, samples) in byClass)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                var r = samples[i];
                string src = Path.Combine(rawdata, $"{r["domain"]}__{r["label"]}", $"{r["id"]}.docx");
                var read = reader.TryReadFile(src);
                if (!read.Success) { Console.WriteLine($"  bỏ {src}: {read.ErrorMessage}"); continue; }
                var orig = (read.Content ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
                int origChars = orig.Sum(l => l.Length + 1);

                foreach (var tier in Tiers)
                {
                    if (i >= (tier.PerClass ?? perClass)) continue;
                    var lines = Compose(orig, origChars, tier, filler, new Random(r["id"].GetHashCode() ^ tier.TargetChars));
                    int total = lines.Sum(l => l.Length + 1);
                    foreach (var fmt in Writers.Formats)
                    {
                        string rel = Path.Combine(tier.Name, fmt, $"{r["id"]}.{fmt}");
                        string path = Path.Combine(outFolder, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        byte[] data = Writers.Write(fmt, lines, "Van ban " + r["id"]);
                        File.WriteAllBytes(path, data);
                        manifest.Append(string.Join(',', rel, r["id"], r["domain"], r["label"], r["truth"], fmt, tier.Name,
                            tier.TargetChars, tier.Placement, origChars, total, data.Length)).Append('\n');
                        written++;
                    }
                }
            }
            Console.WriteLine($"  {cls.Item1}__{cls.Item2}: xong ({written} file, {sw.Elapsed.TotalSeconds:N0} giây)");
        }
        File.WriteAllText(Path.Combine(outFolder, "manifest.csv"), manifest.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"Sinh {written} file trong {sw.Elapsed.TotalSeconds:N0} giây → {outFolder}");
        return 0;
    }

    // Ghép nội dung gốc + đệm để đạt số ký tự mục tiêu. "giua": nửa đệm trước, nửa sau; "cuoi": toàn bộ đệm trước, nội dung gốc ở cuối.
    private static List<string> Compose(List<string> orig, int origChars, Tier tier, List<string> filler, Random rnd)
    {
        if (tier.TargetChars <= origChars) return orig;
        int pad = tier.TargetChars - origChars;
        return tier.Placement == "cuoi"
            ? [.. BuildFiller(filler, pad, rnd), .. orig]
            : [.. BuildFiller(filler, pad / 2, rnd), .. orig, .. BuildFiller(filler, pad - pad / 2, rnd)];
    }

    // Đoạn đệm: mỗi đoạn 3–5 câu ngẫu nhiên, tới khi đủ số ký tự
    private static List<string> BuildFiller(List<string> filler, int chars, Random rnd)
    {
        var lines = new List<string>();
        int n = 0;
        while (n < chars)
        {
            string para = string.Join(" ", Enumerable.Range(0, rnd.Next(3, 6)).Select(_ => filler[rnd.Next(filler.Count)]));
            lines.Add(para);
            n += para.Length + 1;
        }
        return lines;
    }

    // Từ khoá xuất hiện như 1 "từ" (không phải 1 phần của từ khác) — tránh loại nhầm câu vì từ khoá quá ngắn (VD "TIN" trong "tinh")
    private static bool ContainsWord(string text, string kw)
    {
        int idx = 0;
        while ((idx = text.IndexOf(kw, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool left = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            bool right = idx + kw.Length >= text.Length || !char.IsLetterOrDigit(text[idx + kw.Length]);
            if (left && right) return true;
            idx++;
        }
        return false;
    }

    // ================================================================ EVAL ================================================================

    private sealed record Result(Dictionary<string, string> M, bool ReadOk, string? Error, string? Warning, int Chars, int Matches,
        SensitivityLevel Truth, SensitivityLevel Predicted, double ExtractMs, double ScanMs, double ClassifyMs, double AllocMb, double WsMb)
    {
        public double TotalMs => ExtractMs + ScanMs + ClassifyMs;
        public string Tier => M["tier"];
        public string Format => M["format"];
    }

    private static int Evaluate(string corpus, string outFolder)
    {
        var manifest = ReadCsv(Path.Combine(corpus, "manifest.csv"));
        var order = Tiers.Select(t => t.Name).ToList();
        manifest = manifest.OrderBy(m => order.IndexOf(m["tier"])).ThenBy(m => Array.IndexOf(Writers.Formats, m["format"])).ToList();

        var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
        var reader = new FileReaderService();
        var scanner = new RegexScanner(patterns);
        var engine = new ClassificationEngine(NullLogger.Instance);
        var proc = Process.GetCurrentProcess();

        // Làm nóng: 14 file nhỏ đầu tiên (JIT, biên dịch regex) — không tính
        foreach (var m in manifest.Where(m => m["tier"] == "goc").Take(14)) Run(m);

        var results = new List<Result>();
        var tierPeakWs = new Dictionary<string, double>();
        string? currentTier = null;
        var sw = Stopwatch.StartNew();
        foreach (var m in manifest)
        {
            if (m["tier"] != currentTier)
            {
                // Bắt đầu bậc mới: dọn bộ nhớ để working set đo được là của bậc này
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                currentTier = m["tier"];
                Console.WriteLine($"Bậc {currentTier}... ({sw.Elapsed.TotalSeconds:N0} giây)");
            }
            var r = Run(m);
            results.Add(r);
            tierPeakWs[r.Tier] = Math.Max(tierPeakWs.GetValueOrDefault(r.Tier), r.WsMb);
        }

        Directory.CreateDirectory(outFolder);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + DlpTools.Shared.ExtractorSwitch.Name; // [Thay đổi] + tên bộ đọc
        WriteCsv(Path.Combine(outFolder, $"sizebench-{stamp}.csv"), results);
        string md = Report(results, tierPeakWs, corpus, sw.Elapsed);
        string mdPath = Path.Combine(outFolder, $"sizebench-{stamp}.md");
        File.WriteAllText(mdPath, md, new UTF8Encoding(true));
        Console.WriteLine(md);
        Console.WriteLine($"Báo cáo: {Path.GetFullPath(mdPath)}");
        return 0;

        Result Run(Dictionary<string, string> m)
        {
            string path = Path.Combine(corpus, m["path"]);
            long alloc0 = GC.GetTotalAllocatedBytes(precise: false);
            var t = Stopwatch.StartNew();
            var read = reader.TryReadFile(path);
            double extract = t.Elapsed.TotalMilliseconds;
            var truth = Enum.Parse<SensitivityLevel>(m["truth"]);
            if (!read.Success)
            {
                proc.Refresh();
                return new Result(m, false, read.ErrorMessage, read.Warning, 0, 0, truth, SensitivityLevel.Public, extract, 0, 0,
                    (GC.GetTotalAllocatedBytes(false) - alloc0) / 1048576.0, proc.WorkingSet64 / 1048576.0);
            }
            string content = read.Content ?? "";
            t.Restart();
            var scan = scanner.Scan(content);
            double scanMs = t.Elapsed.TotalMilliseconds;
            var level = SensitivityLevel.Public;
            double cls = 0;
            if (scan.Matches.Count > 0)
            {
                t.Restart();
                level = engine.Classify(new ScanContext { FilePath = path, Content = content, Matches = scan.Matches }).Level;
                cls = t.Elapsed.TotalMilliseconds;
            }
            proc.Refresh();
            return new Result(m, true, null, read.Warning, content.Length, scan.Matches.Count, truth, level, extract, scanMs, cls,
                (GC.GetTotalAllocatedBytes(false) - alloc0) / 1048576.0, proc.WorkingSet64 / 1048576.0);
        }
    }

    private static string Report(List<Result> rs, Dictionary<string, double> peakWs, string corpus, TimeSpan elapsed)
    {
        var md = new StringBuilder();
        var tiers = Tiers.Select(t => t.Name).Where(t => rs.Any(r => r.Tier == t)).ToList();
        string Pct(double v) => (v * 100).ToString("0.0", Inv) + " %";
        string N1(double v) => v.ToString("0.0", Inv);
        double Avg(IEnumerable<double> v) { var a = v.ToList(); return a.Count == 0 ? double.NaN : a.Average(); }
        double P95(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? double.NaN : s[Math.Clamp((int)Math.Ceiling(0.95 * s.Count) - 1, 0, s.Count - 1)]; }
        bool Sens(SensitivityLevel l) => l >= SensitivityLevel.Confidential;
        double F1Sens(List<Result> g)
        {
            int tp = g.Count(r => Sens(r.Truth) && Sens(r.Predicted)), fp = g.Count(r => !Sens(r.Truth) && Sens(r.Predicted)), fn = g.Count(r => Sens(r.Truth) && !Sens(r.Predicted));
            return tp == 0 ? 0 : 2.0 * tp / (2 * tp + fp + fn);
        }

        md.AppendLine($"# Ảnh hưởng của kích thước và định dạng file — {DateTime.Now:yyyy-MM-dd HH:mm} — bộ đọc `{DlpTools.Shared.ExtractorSwitch.Name}`\n"); // [Thay đổi]
        md.AppendLine($"- Corpus: `{Path.GetFullPath(corpus)}` — {rs.Count:N0} file, {Writers.Formats.Length} định dạng × {tiers.Count} bậc; đo trong {elapsed.TotalMinutes:N1} phút (1 luồng, tuần tự).");
        md.AppendLine("- Pipeline giống service: FileReaderService → RegexScanner (MinConfidence 0,60) → ClassificationEngine. Nội dung gốc là mẫu rawdata có nhãn; phần thêm vào là văn bản trung tính (không khớp rule) → nhãn không đổi.");
        md.AppendLine("- Bậc: `goc` = nội dung gốc; `20k`/`200k`/`1.5M` = đệm tới số ký tự đó, nội dung gốc ở GIỮA; `3M-cuoi` = vượt giới hạn 2.000.000 ký tự của service, nội dung gốc ở CUỐI.\n");

        md.AppendLine("## 1. Bộ dữ liệu\n");
        md.AppendLine("| Bậc | Số file | Ký tự mục tiêu | Ký tự trích được TB | " + string.Join(" | ", Writers.Formats.Select(f => $"KB {f}")) + " |");
        md.AppendLine("|---|---|---|---|" + string.Concat(Writers.Formats.Select(_ => "---|")));
        foreach (var t in tiers)
        {
            var g = rs.Where(r => r.Tier == t).ToList();
            md.AppendLine($"| {t} | {g.Count} | {g[0].M["target_chars"]} | {Avg(g.Where(r => r.ReadOk).Select(r => (double)r.Chars)):N0} | " +
                string.Join(" | ", Writers.Formats.Select(f => N1(Avg(g.Where(r => r.Format == f).Select(r => double.Parse(r.M["size_bytes"], Inv) / 1024))))) + " |");
        }

        md.AppendLine("\n## 2. Thời gian xử lý (ms/file, TB — tổng trích + quét + phân loại)\n");
        md.AppendLine("| Bậc | " + string.Join(" | ", Writers.Formats) + " | Tất cả (p95) |");
        md.AppendLine("|---|" + string.Concat(Writers.Formats.Select(_ => "---|")) + "---|");
        foreach (var t in tiers)
        {
            var g = rs.Where(r => r.Tier == t && r.ReadOk).ToList();
            md.AppendLine($"| {t} | " + string.Join(" | ", Writers.Formats.Select(f => N1(Avg(g.Where(r => r.Format == f).Select(r => r.TotalMs))))) +
                $" | {N1(Avg(g.Select(r => r.TotalMs)))} ({N1(P95(g.Select(r => r.TotalMs)))}) |");
        }
        md.AppendLine("\nTách theo bước (TB, mọi định dạng):\n");
        md.AppendLine("| Bậc | Trích text | Quét regex | Phân loại | Trích: ms / 100k ký tự | Quét: ms / 100k ký tự |\n|---|---|---|---|---|---|");
        foreach (var t in tiers)
        {
            var g = rs.Where(r => r.Tier == t && r.ReadOk && r.Chars > 0).ToList();
            md.AppendLine($"| {t} | {N1(Avg(g.Select(r => r.ExtractMs)))} | {N1(Avg(g.Select(r => r.ScanMs)))} | {N1(Avg(g.Select(r => r.ClassifyMs)))} | " +
                $"{N1(Avg(g.Select(r => r.ExtractMs / r.Chars * 100_000)))} | {N1(Avg(g.Select(r => r.ScanMs / r.Chars * 100_000)))} |");
        }

        md.AppendLine("\n## 3. Độ chính xác\n");
        md.AppendLine("Accuracy 4 mức theo bậc × định dạng:\n");
        md.AppendLine("| Bậc | " + string.Join(" | ", Writers.Formats) + " | Tất cả | F1 \"nhạy cảm\" | Đoán thấp hơn | Đoán cao hơn |");
        md.AppendLine("|---|" + string.Concat(Writers.Formats.Select(_ => "---|")) + "---|---|---|---|");
        foreach (var t in tiers)
        {
            var g = rs.Where(r => r.Tier == t).ToList();
            md.AppendLine($"| {t} | " + string.Join(" | ", Writers.Formats.Select(f => { var x = g.Where(r => r.Format == f).ToList(); return Pct(x.Count(r => r.Truth == r.Predicted) / (double)x.Count); })) +
                $" | **{Pct(g.Count(r => r.Truth == r.Predicted) / (double)g.Count)}** | {Pct(F1Sens(g))} | {Pct(g.Count(r => r.Predicted < r.Truth) / (double)g.Count)} | {Pct(g.Count(r => r.Predicted > r.Truth) / (double)g.Count)} |");
        }
        // Cùng mẫu ở bậc gốc vs bậc lớn: mức có đổi không (chỉ xét mẫu có mặt ở cả 2 bậc)
        md.AppendLine("\nCùng 1 mẫu + cùng định dạng, so với bậc `goc`: % file đổi mức khi file lớn lên\n");
        md.AppendLine("| Bậc | Số cặp | Đổi mức | Trong đó: hạ mức | nâng mức |\n|---|---|---|---|---|");
        var baseMap = rs.Where(r => r.Tier == "goc").ToDictionary(r => (r.M["id"], r.Format), r => r.Predicted);
        foreach (var t in tiers.Skip(1))
        {
            var pairs = rs.Where(r => r.Tier == t && baseMap.ContainsKey((r.M["id"], r.Format))).Select(r => (Base: baseMap[(r.M["id"], r.Format)], Now: r.Predicted)).ToList();
            if (pairs.Count == 0) continue;
            md.AppendLine($"| {t} | {pairs.Count} | {Pct(pairs.Count(p => p.Base != p.Now) / (double)pairs.Count)} | {Pct(pairs.Count(p => p.Now < p.Base) / (double)pairs.Count)} | {Pct(pairs.Count(p => p.Now > p.Base) / (double)pairs.Count)} |");
        }

        md.AppendLine("\n## 4. Bộ nhớ\n");
        md.AppendLine("| Bậc | Working set cao nhất (MB) | Cấp phát TB / file (MB) | Cấp phát max / file (MB) | Định dạng cấp phát nhiều nhất |\n|---|---|---|---|---|");
        foreach (var t in tiers)
        {
            var g = rs.Where(r => r.Tier == t).ToList();
            var worst = g.GroupBy(r => r.Format).Select(x => (x.Key, Avg(x.Select(r => r.AllocMb)))).OrderByDescending(x => x.Item2).First();
            md.AppendLine($"| {t} | {N1(peakWs[t])} | {N1(Avg(g.Select(r => r.AllocMb)))} | {N1(g.Max(r => r.AllocMb))} | {worst.Key} ({N1(worst.Item2)} MB) |");
        }

        md.AppendLine("\n## 5. Lỗi đọc / cắt nội dung\n");
        md.AppendLine("| Bậc | Định dạng | Đọc lỗi | Bị cắt (cảnh báo) | Ví dụ thông điệp |\n|---|---|---|---|---|");
        foreach (var t in tiers)
        {
            foreach (var f in Writers.Formats)
            {
                var g = rs.Where(r => r.Tier == t && r.Format == f).ToList();
                int err = g.Count(r => !r.ReadOk), warn = g.Count(r => r.Warning != null);
                if (err + warn == 0) continue;
                string ex = (g.FirstOrDefault(r => !r.ReadOk)?.Error ?? g.First(r => r.Warning != null).Warning ?? "").Replace("|", "/");
                md.AppendLine($"| {t} | {f} | {err} | {warn} | {ex[..Math.Min(90, ex.Length)]} |");
            }
        }
        return md.ToString();
    }

    private static void WriteCsv(string path, List<Result> rs)
    {
        var sb = new StringBuilder("path,id,domain,label,format,tier,target_chars,size_bytes,read_ok,warning,chars,matches,truth,predicted,extract_ms,scan_ms,classify_ms,total_ms,alloc_mb,ws_mb\n");
        foreach (var r in rs)
        {
            sb.AppendLine(string.Join(',', r.M["path"], r.M["id"], r.M["domain"], r.M["label"], r.Format, r.Tier, r.M["target_chars"], r.M["size_bytes"],
                r.ReadOk ? 1 : 0, r.Warning == null ? 0 : 1, r.Chars, r.Matches, r.Truth, r.Predicted,
                r.ExtractMs.ToString("0.###", Inv), r.ScanMs.ToString("0.###", Inv), r.ClassifyMs.ToString("0.###", Inv), r.TotalMs.ToString("0.###", Inv),
                r.AllocMb.ToString("0.##", Inv), r.WsMb.ToString("0.#", Inv)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    // CSV đơn giản: các cột dùng ở đây không chứa dấu phẩy/ngoặc kép (id, domain, label, format, số...)
    private static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var head = lines[0].Split(',');
        var list = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        {
            var cells = SplitCsv(line);
            var d = new Dictionary<string, string>();
            for (int i = 0; i < head.Length && i < cells.Count; i++) d[head[i]] = cells[i];
            list.Add(d);
        }
        return list;
    }

    // Tách 1 dòng CSV có hỗ trợ ngoặc kép (manifest của DlpBaseline có cột rules_fired/read_error có thể chứa dấu phẩy)
    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') q = false;
                else cur.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells;
    }

    private static int ArgInt(string[] a, string n, int d) { int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out int v) ? v : d; }
    private static string? ArgStr(string[] a, string n) { int i = Array.IndexOf(a, n); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
