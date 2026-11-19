// [Thay đổi] Giai đoạn 2 / P0 (19/11/2026): thí nghiệm BIẾN THỂ regex — chỉ công cụ đo, không đụng Core. Trả lời các câu hỏi còn mở của đợt benchmark điểm nóng regex:
//   - chi phí gọi regex (InitializeTimeout ~15% Scan?) khác nhau thế nào giữa Matches / Count / EnumerateMatches / timeout vô hạn (N9, H7);
//   - CultureInvariant (H8), NonBacktracking (H9), không Compiled (N6) nhanh/chậm bao nhiêu và có cho CÙNG kết quả khớp không;
//   - chi phí khởi tạo + RAM của 222 regex Compiled so với interpreter (N6).
// Chế độ:
//   DlpRegexBench variants <dataset> [--sets ...] [--rawdata <dir> --raw-sample N] [--runs 5] [--warmup 1] [--out <thư mục>]
//   DlpRegexBench overhead [--reps 2000] [--out <thư mục>]            — chi phí MỖI LẦN GỌI trên chuỗi ngắn (ns/lần), mọi pattern
//   DlpRegexBench startup --options compiled|interpreted               — thời gian + RAM khi tạo lại 222 regex (chạy 2 tiến trình riêng để so)
// Nội dung file chỉ nằm trong RAM; CSV/báo cáo chỉ có id file, tên rule và số đo.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DlpService.Reading;
using DlpService.Scanning;
using DlpTools.Perf;
using static DlpTools.Perf.BenchCommon;

namespace DlpRegexBench;

internal static class VariantsBench
{
    private readonly record struct Sig(long Count, ulong Hash, bool Timeout);

    private sealed record Variant(string Name, bool ComparesValues, Func<SensitivePattern, Regex?> Build, Func<Regex, string, Sig> Run);

    private static ulong Fnv(ulong h, ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            h = (h ^ c) * 1099511628211UL;
        }
        return h;
    }

    private static Sig RunMatches(Regex rx, string c)
    {
        long n = 0;
        ulong h = 14695981039346656037UL;
        try
        {
            foreach (Match m in rx.Matches(c))
            {
                n++;
                h = Fnv(h ^ (ulong)m.Index, m.ValueSpan);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new Sig(n, h, true);
        }
        return new Sig(n, h, false);
    }

    private static Sig RunCount(Regex rx, string c)
    {
        try
        {
            return new Sig(rx.Count(c), 0, false);
        }
        catch (RegexMatchTimeoutException)
        {
            return new Sig(0, 0, true);
        }
    }

    private static Sig RunEnumerate(Regex rx, string c)
    {
        long n = 0;
        ulong h = 14695981039346656037UL;
        try
        {
            foreach (ValueMatch vm in rx.EnumerateMatches(c))
            {
                n++;
                h = Fnv(h ^ (ulong)vm.Index, c.AsSpan(vm.Index, vm.Length));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new Sig(n, h, true);
        }
        return new Sig(n, h, false);
    }

    private static Regex? TryBuild(SensitivePattern p, Func<RegexOptions, RegexOptions> options, TimeSpan? timeout = null)
    {
        try
        {
            return new Regex(p.Regex.ToString(), options(p.Regex.Options), timeout ?? p.Regex.MatchTimeout);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return null; // VD NonBacktracking không hỗ trợ lookaround/backreference
        }
    }

    private static bool UsesIgnoreCase(SensitivePattern p) =>
        (p.Regex.Options & RegexOptions.IgnoreCase) != 0 || p.Regex.ToString().Contains("(?i", StringComparison.Ordinal);

    private static List<Variant> Variants() =>
    [
        new("matches (hiện tại)", true, p => p.Regex, RunMatches),
        new("count", false, p => p.Regex, RunCount),
        new("enumerate", true, p => p.Regex, RunEnumerate),
        new("matches+timeout vô hạn", true, p => TryBuild(p, o => o, Regex.InfiniteMatchTimeout), RunMatches),
        new("matches+CultureInvariant", true, p => UsesIgnoreCase(p) ? TryBuild(p, o => o | RegexOptions.CultureInvariant) : null, RunMatches),
        new("matches+interpreter", true, p => TryBuild(p, o => o & ~RegexOptions.Compiled), RunMatches),
        new("matches+NonBacktracking", true, p => TryBuild(p, o => (o & ~RegexOptions.Compiled) | RegexOptions.NonBacktracking), RunMatches),
    ];

    // ============================================================ variants ============================================================

    public static int Run(List<BenchFile> files, List<SensitivePattern> patterns, FileReaderService reader, int runs, int warmup, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var contents = new List<string>();
        foreach (var f in files)
        {
            var read = reader.TryReadFile(f.Path);
            if (read.Content is { Length: > 0 } content)
            {
                contents.Add(content);
            }
        }
        long totalChars = contents.Sum(c => (long)c.Length);
        Console.WriteLine($"{contents.Count} file có nội dung · {totalChars:N0} ký tự · {patterns.Count} pattern · {runs} lượt (+{warmup} làm nóng)");

        var variants = Variants();
        var csv = new StringBuilder("rule_id,pattern_idx,variant,ms_median,cv,matches,supported,same_as_matches\n");
        var totals = variants.ToDictionary(v => v.Name, _ => 0.0);
        var supported = variants.ToDictionary(v => v.Name, _ => 0);
        var notSame = variants.ToDictionary(v => v.Name, _ => new List<string>());
        var perPattern = new List<(string Key, Dictionary<string, double> Ms)>();

        for (int pi = 0; pi < patterns.Count; pi++)
        {
            var p = patterns[pi];
            string key = $"{p.RuleId ?? p.Name}#{p.PatternIdx}";
            Sig baseline = default;
            var msByVariant = new Dictionary<string, double>();
            // [Thay đổi] Sửa thiên lệch thứ tự: biến thể đo đầu tiên của mỗi pattern gánh chi phí JIT/làm nóng (bank_account_001#3: 37 ms so với 0,4 ms ở biến thể sau).
            // Nay: dựng mọi biến thể, làm nóng TẤT CẢ (warmup lượt), rồi đo XOAY VÒNG thứ tự các biến thể qua từng lượt.
            var built = variants.Select(v => (V: v, Rx: v.Build(p))).ToList();
            foreach (var (v, rx) in built)
            {
                if (rx is null)
                {
                    csv.Append(p.RuleId ?? p.Name).Append(',').Append(p.PatternIdx).Append(',').Append(v.Name).Append(",,,,0,\n");
                }
                else
                {
                    supported[v.Name]++;
                }
            }
            var live = built.Where(b => b.Rx is not null).ToList();
            var sigs = new Dictionary<string, Sig>();
            var timesBy = live.ToDictionary(b => b.V.Name, _ => new List<double>());
            for (int w = 0; w < warmup; w++)
            {
                foreach (var (v, rx) in live)
                {
                    foreach (var c in contents) v.Run(rx!, c);
                }
            }
            for (int r = 0; r < runs; r++)
            {
                for (int k = 0; k < live.Count; k++)
                {
                    var (v, rx) = live[(k + r) % live.Count]; // xoay vòng thứ tự
                    long n = 0;
                    ulong h = 14695981039346656037UL;
                    bool to = false;
                    var sw = Stopwatch.StartNew();
                    foreach (var c in contents)
                    {
                        var s = v.Run(rx!, c);
                        n += s.Count;
                        h = (h ^ s.Hash) * 1099511628211UL;
                        to |= s.Timeout;
                    }
                    sw.Stop();
                    timesBy[v.Name].Add(sw.Elapsed.TotalMilliseconds);
                    sigs[v.Name] = new Sig(n, h, to);
                }
            }
            foreach (var (v, _) in live)
            {
                var agg = sigs[v.Name];
                double med = Median(timesBy[v.Name]);
                msByVariant[v.Name] = med;
                totals[v.Name] += med;
                bool isBase = v.Name.StartsWith("matches (hiện tại)", StringComparison.Ordinal);
                if (isBase)
                {
                    baseline = agg;
                }
                bool same = isBase || (agg.Count == baseline.Count && (!v.ComparesValues || agg.Hash == baseline.Hash) && !agg.Timeout);
                if (!same)
                {
                    notSame[v.Name].Add($"{key} (đếm {agg.Count} so với {baseline.Count})");
                }
                csv.Append(p.RuleId ?? p.Name).Append(',').Append(p.PatternIdx).Append(',').Append(v.Name).Append(',').Append(F(med)).Append(',')
                   .Append(F(Cv(timesBy[v.Name]), "0.00")).Append(',').Append(agg.Count).Append(",1,").Append(same ? 1 : 0).Append('\n');
            }
            perPattern.Add((key, msByVariant));
            if ((pi + 1) % 20 == 0)
            {
                Console.WriteLine($"  {pi + 1}/{patterns.Count} pattern");
            }
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
        File.WriteAllText(Path.Combine(outDir, $"variants-{stamp}.csv"), csv.ToString(), new UTF8Encoding(true));

        var md = new StringBuilder();
        md.AppendLine($"# Thí nghiệm biến thể regex (P0) — {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
        md.AppendLine($"- Dữ liệu: {contents.Count} file · {totalChars:N0} ký tự · {patterns.Count} pattern · {runs} lượt đo, median; mỗi số là TỔNG thời gian chạy 1 pattern trên mọi file").AppendLine();
        md.AppendLine("## 1. Tổng thời gian theo biến thể (cộng mọi pattern)").AppendLine();
        md.AppendLine("| Biến thể | Pattern chạy được | Σ ms | so với matches hiện tại | Pattern cho kết quả KHÁC |");
        md.AppendLine("|---|---|---|---|---|");
        double baseTotal = totals["matches (hiện tại)"];
        foreach (var v in variants)
        {
            md.AppendLine($"| {v.Name} | {supported[v.Name]} | {F(totals[v.Name], "0.0")} | {F(totals[v.Name] / baseTotal, "0.00")}× | {notSame[v.Name].Count} |");
        }
        md.AppendLine().AppendLine("(So sánh Σ ms giữa các biến thể chỉ công bằng khi cùng tập pattern; CultureInvariant chỉ áp cho pattern có (?i), NonBacktracking chỉ cho pattern hỗ trợ — xem cột \"Pattern chạy được\". Dùng bảng 2 để so đúng tập.)").AppendLine();

        md.AppendLine("## 2. Chỉ trên tập pattern mà biến thể chạy được (so cùng tập với matches hiện tại)").AppendLine();
        md.AppendLine("| Biến thể | Số pattern | Σ ms biến thể | Σ ms matches hiện tại (cùng tập) | Tỉ lệ |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var v in variants.Skip(1))
        {
            var sub = perPattern.Where(x => x.Ms.ContainsKey(v.Name)).ToList();
            double a = sub.Sum(x => x.Ms[v.Name]), b = sub.Sum(x => x.Ms["matches (hiện tại)"]);
            md.AppendLine($"| {v.Name} | {sub.Count} | {F(a, "0.0")} | {F(b, "0.0")} | {F(a / Math.Max(b, 1e-9), "0.00")}× |");
        }

        md.AppendLine().AppendLine("## 3. Top 25 pattern theo thời gian (matches hiện tại) và các biến thể (ms, tổng mọi file)").AppendLine();
        md.AppendLine("| Pattern | " + string.Join(" | ", variants.Select(v => v.Name)) + " |");
        md.AppendLine("|---|" + string.Join("|", variants.Select(_ => "---")) + "|");
        foreach (var x in perPattern.OrderByDescending(x => x.Ms["matches (hiện tại)"]).Take(25))
        {
            md.AppendLine($"| {x.Key} | " + string.Join(" | ", variants.Select(v => x.Ms.TryGetValue(v.Name, out double ms) ? F(ms, "0.0") : "—")) + " |");
        }

        md.AppendLine().AppendLine("## 4. Pattern cho kết quả KHÁC matches hiện tại (không được dùng biến thể đó cho pattern này)").AppendLine();
        foreach (var v in variants.Skip(1))
        {
            md.AppendLine($"- **{v.Name}**: {(notSame[v.Name].Count == 0 ? "không có" : string.Join("; ", notSame[v.Name].Take(15)) + (notSame[v.Name].Count > 15 ? $" … (+{notSame[v.Name].Count - 15})" : ""))}");
        }
        File.WriteAllText(Path.Combine(outDir, $"variants-report-{stamp}.md"), md.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"Đã ghi {outDir}/variants-{stamp}.csv, variants-report-{stamp}.md");
        return 0;
    }

    // ============================================================ overhead ============================================================

    public static int Overhead(List<SensitivePattern> patterns, int reps, string outDir)
    {
        Directory.CreateDirectory(outDir);
        const string tiny = "abcdefghij 0123456789 klmnop qrstuv";   // chuỗi ngắn, không khớp pattern nào → đo chi phí GỌI, không phải chi phí quét
        var variants = Variants().Where(v => v.Name is "matches (hiện tại)" or "count" or "enumerate" or "matches+timeout vô hạn").ToList();
        var md = new StringBuilder();
        md.AppendLine($"# Chi phí mỗi lần gọi regex trên chuỗi {tiny.Length} ký tự (không khớp) — {patterns.Count} pattern × {reps} lần").AppendLine();
        md.AppendLine("| Biến thể | ns / lần gọi | µs cho 1 lượt đủ 222 pattern |");
        md.AppendLine("|---|---|---|");
        foreach (var v in variants)
        {
            var built = patterns.Select(p => v.Build(p)).Where(r => r is not null).Select(r => r!).ToList();
            for (int w = 0; w < 200; w++)
            {
                foreach (var rx in built) v.Run(rx, tiny);
            }
            var times = new List<double>();
            for (int run = 0; run < 7; run++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < reps; i++)
                {
                    foreach (var rx in built) v.Run(rx, tiny);
                }
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds * 1e6 / ((double)reps * built.Count));
            }
            double ns = Median(times);
            md.AppendLine($"| {v.Name} | {F(ns, "0")} | {F(ns * built.Count / 1000.0, "0.0")} |");
        }
        File.WriteAllText(Path.Combine(outDir, $"overhead-report-{DateTime.Now:yyyyMMdd-HHmmss}.md"), md.ToString(), new UTF8Encoding(true));
        Console.WriteLine(md.ToString());
        return 0;
    }

    // ============================================================ startup ============================================================

    public static int Startup(List<SensitivePattern> patterns, string options)
    {
        bool compiled = options == "compiled";
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long heap0 = GC.GetTotalMemory(true);
        long ws0 = Process.GetCurrentProcess().WorkingSet64;
        var keep = new List<Regex>(patterns.Count);
        var sw = Stopwatch.StartNew();
        foreach (var p in patterns)
        {
            var opts = compiled ? p.Regex.Options | RegexOptions.Compiled : p.Regex.Options & ~RegexOptions.Compiled;
            var rx = new Regex(p.Regex.ToString(), opts, p.Regex.MatchTimeout);
            rx.IsMatch("abc 123 @"); // buộc biên dịch/khởi tạo runner thật sự
            keep.Add(rx);
        }
        sw.Stop();
        GC.Collect();
        long heap1 = GC.GetTotalMemory(true);
        long ws1 = Process.GetCurrentProcess().WorkingSet64;
        Console.WriteLine($"startup {options}: {patterns.Count} regex tạo + chạy thử 1 lần: {sw.Elapsed.TotalMilliseconds:F0} ms · heap +{(heap1 - heap0) / 1048576.0:F1} MB · working set +{(ws1 - ws0) / 1048576.0:F1} MB (nền đã có bản Compiled do RuleLoader nạp)");
        GC.KeepAlive(keep);
        return 0;
    }
}
