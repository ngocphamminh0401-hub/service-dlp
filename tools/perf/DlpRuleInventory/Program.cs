// [Thay đổi] File mới (Benchmark T01): thống kê bộ rule đã nạp để xác nhận số liệu của Code Review (A-stat) và làm đầu vào cho
// bộ sinh dataset (T02, cần danh sách từ khoá). Đếm trên đối tượng SensitivePattern SAU RuleLoader (cờ khai ở cấp rule đã được
// gán xuống từng pattern, giá trị mặc định đã áp) — khác với đếm thô trong file JSON.
// Cách dùng: DlpRuleInventory [--rules <thư mục rules>] [--out <file .md>] [--keywords-out <file .txt>]
// Chỉ ghi tên rule + số đếm + từ khoá (từ khoá là văn bản công khai trong rules/, không phải dữ liệu nhạy cảm).
using System.Globalization;
using System.Text;
using DlpService.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpRuleInventory;

internal static class Program
{
    // Bản sao số lượng từ khoá cố định DocContextProbeKeywords trong RegexScanner (private nên không đọc được) — 10 từ
    private const int DocContextProbeKeywordCount = 10;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string rulesDir = Arg(args, "--rules") ?? Path.Combine(AppContext.BaseDirectory, "rules");
        string outFile = Arg(args, "--out") ?? Path.Combine("tools", "perf", "baseline", "rules-inventory.md");
        string? keywordsOut = Arg(args, "--keywords-out");

        var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(rulesDir);
        string rulesVer = RuleLoader.ComputeRulesVersion(rulesDir);
        var jsonFiles = Directory.GetFiles(rulesDir, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("compound.json", StringComparison.OrdinalIgnoreCase)).ToList();

        // Gom theo khoá rule giống RegexScanner.BuildTelemetry / FileProcessor.BuildFindings (RuleId, không có thì Name)
        var rules = patterns.GroupBy(p => p.RuleId ?? p.Name).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();

        // Từ khoá theo rule: hợp của Keywords (context), Primary, Secondary — loại trùng trong 1 rule (không phân biệt hoa thường)
        List<string> RuleKeywords(IGrouping<string, SensitivePattern> r) => r
            .SelectMany(p => p.Keywords.Concat(p.PrimaryKeywords).Concat(p.SecondaryKeywords))
            .Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        int keywordOccurrences = rules.Sum(r => RuleKeywords(r).Count);
        var distinctKeywords = rules.SelectMany(RuleKeywords).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal).ToList();
        int withDiacritics = distinctKeywords.Count(HasDiacritics);
        int occurrencesWithDiacritics = rules.Sum(r => RuleKeywords(r).Count(HasDiacritics));

        // Cờ cấp rule = có ÍT NHẤT 1 pattern con bật cờ đó
        var flags = new (string Name, Func<SensitivePattern, bool> Test)[]
        {
            ("context_required", p => p.ContextRequired),
            ("exclude_if_no_keywords", p => p.ExcludeIfNoKeywords),
            ("proximity (pattern hoặc rule)", p => p.Proximity != null),
            ("proximity_window cấp rule", p => p.RuleProximity != null),
            ("allow_table_fallback", p => p.Proximity?.AllowTableFallback == true),
            ("negation_filter", p => p.NegationFilter != null),
            ("escalation", p => p.Escalation != null),
            ("volume_escalation", p => p.VolumeEscalation != null),
            ("placeholder_exclusion", p => p.Placeholder != null),
            ("level_gate", p => p.LevelGate != null),
            ("validator (mọi loại)", p => p.Validator != null),
            ("validator checksum", p => p.ValidatorIsChecksum),
            ("cvv_expiry_boost", p => p.CvvExpiryBoost > 0)
        };

        // Cận trên số lượt "dò từ khoá trên CẢ nội dung" mà RegexScanner có thể chạy cho 1 file (khi từ khoá KHÔNG có trong file —
        // Contains phải đi hết chuỗi; có từ khoá thì dừng sớm):
        //  - BuildTelemetry: mỗi từ khoá primary của mỗi rule (1 lần / rule) + 10 từ DocContextProbe — chạy với MỌI file quét trọn;
        //  - RuleHasKeyword (cache theo rule): Keywords của rule exclude_if_no_keywords — chỉ khi rule đó có chỗ khớp;
        //  - DocDiscountFactor (cache theo PlaceholderRule): doc_context_discount_keywords — chỉ khi có chỗ khớp;
        //  - vòng deferred: ProximityKeywords × SỐ CHỖ KHỚP deferred — phụ thuộc dữ liệu, không tính tĩnh được (đo ở R2).
        int telemetryPasses = rules.Sum(r => r.First().PrimaryKeywords.Count(k => k.Length > 0)) + DocContextProbeKeywordCount;
        int ruleHasKeywordPasses = rules.Where(r => r.Any(p => p.ExcludeIfNoKeywords))
            .Sum(r => r.First().Keywords.Count(k => k.Length > 0));
        int docDiscountPasses = patterns.Select(p => p.Placeholder).Where(p => p != null).Distinct()
            .Sum(p => p!.DocDiscountKeywords.Count);
        int deferredPerMatch = rules.Where(r => r.Any(p => p.Proximity?.AllowTableFallback == true))
            .Select(r => r.First(p => p.Proximity?.AllowTableFallback == true))
            .Select(p => p.Proximity!.IncludeSecondary ? p.PrimaryKeywords.Count + p.SecondaryKeywords.Count : p.PrimaryKeywords.Count)
            .DefaultIfEmpty(0).Max();

        var md = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        md.AppendLine("# Kiểm kê bộ rule (Benchmark T01)");
        md.AppendLine();
        md.AppendLine($"- Thời điểm: {DateTime.Now:yyyy-MM-dd HH:mm} · thư mục: `{Path.GetFullPath(rulesDir)}` · rules_ver: `{rulesVer[..Math.Min(12, rulesVer.Length)]}`");
        md.AppendLine("- Nguồn: đối tượng `SensitivePattern` sau `RuleLoader.LoadFromFolder` (đúng như service), không phải đếm thô JSON.");
        md.AppendLine();
        md.AppendLine("## 1. Tổng quan");
        md.AppendLine();
        md.AppendLine("| Mục | Đo được | Code Review |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| File rule (trừ compound.json) | {jsonFiles.Count} | 63 |");
        md.AppendLine($"| Rule (khoá RuleId ?? Name) | {rules.Count} | — |");
        md.AppendLine($"| Pattern đã nạp | {patterns.Count} | 221 |");
        md.AppendLine($"| Từ khoá — tổng theo rule (đã bỏ trùng trong rule) | {keywordOccurrences} | 1.134 |");
        md.AppendLine($"| Từ khoá — trong đó có dấu | {occurrencesWithDiacritics} | 690 |");
        md.AppendLine($"| Từ khoá — khác nhau toàn bộ | {distinctKeywords.Count} (có dấu {withDiacritics}) | — |");
        md.AppendLine($"| Regex blocklist (placeholder) | {patterns.Select(p => p.Placeholder).Where(p => p != null).Distinct().Sum(p => p!.Blocklist.Count)} | — |");
        md.AppendLine();
        md.AppendLine("## 2. Cờ theo rule / pattern");
        md.AppendLine();
        md.AppendLine("| Cờ | Số rule (≥1 pattern bật) | Số pattern | Code Review (rule) |");
        md.AppendLine("|---|---|---|---|");
        foreach (var (name, test) in flags)
        {
            string cr = name switch { "context_required" => "62/63", "exclude_if_no_keywords" => "51", _ => "" };
            md.AppendLine($"| {name} | {rules.Count(r => r.Any(test))} | {patterns.Count(test)} | {cr} |");
        }
        md.AppendLine();
        md.AppendLine("## 3. Cận trên số lượt dò từ khoá trên TOÀN nội dung (1 file)");
        md.AppendLine();
        md.AppendLine("Mỗi lượt = 1 lần `string.Contains(k, OrdinalIgnoreCase)` trên cả nội dung; trường hợp xấu nhất (từ khoá không có) đi hết chuỗi.");
        md.AppendLine();
        md.AppendLine("| Nguồn (RegexScanner) | Điều kiện | Số lượt tối đa |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| `BuildTelemetry` (primary của mọi rule + 10 DocContextProbe) | mọi file quét trọn | {telemetryPasses} |");
        md.AppendLine($"| `RuleHasKeyword` (Keywords của rule exclude_if_no_keywords, cache theo rule) | rule đó có ≥1 chỗ khớp | {ruleHasKeywordPasses} |");
        md.AppendLine($"| `DocDiscountFactor` (cache theo PlaceholderRule) | có chỗ khớp ở rule có placeholder | {docDiscountPasses} |");
        md.AppendLine($"| vòng `deferred` (ProximityKeywords, KHÔNG cache) | × số chỗ khớp deferred | ≤ {deferredPerMatch} / chỗ khớp |");
        md.AppendLine($"| **Tổng tĩnh** (chưa tính deferred) | | **{telemetryPasses + ruleHasKeywordPasses + docDiscountPasses}** |");
        md.AppendLine();
        md.AppendLine("Ghi chú: con số \"555 lượt\" của Code Review so với dòng tổng tĩnh ở trên; phần deferred phụ thuộc dữ liệu và được đo ở R2/R3.");
        md.AppendLine();
        md.AppendLine("## 4. Chi tiết theo rule");
        md.AppendLine();
        md.AppendLine("| Rule | Category | Pattern | Từ khoá (context/primary/secondary) | context_req | excl_no_kw | proximity | table_fb | validator |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rules)
        {
            var f = r.First();
            md.AppendLine(string.Create(inv,
                $"| {r.Key} | {f.Category} | {r.Count()} | {f.Keywords.Count}/{f.PrimaryKeywords.Count}/{f.SecondaryKeywords.Count} | {Mark(r.Count(p => p.ContextRequired), r.Count())} | {Mark(r.Count(p => p.ExcludeIfNoKeywords), r.Count())} | {Mark(r.Count(p => p.Proximity != null), r.Count())} | {Mark(r.Count(p => p.Proximity?.AllowTableFallback == true), r.Count())} | {Mark(r.Count(p => p.Validator != null), r.Count())} |"));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        File.WriteAllText(outFile, md.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"Đã ghi {outFile}: {jsonFiles.Count} file, {rules.Count} rule, {patterns.Count} pattern, {keywordOccurrences} từ khoá, lượt dò tĩnh {telemetryPasses + ruleHasKeywordPasses + docDiscountPasses}");

        if (keywordsOut != null)
        {
            // Danh sách từ khoá khác nhau (1 dòng / từ) — đầu vào cho DlpBenchData (bộ D-kw)
            File.WriteAllLines(keywordsOut, distinctKeywords, new UTF8Encoding(false));
            Console.WriteLine($"Đã ghi {distinctKeywords.Count} từ khoá vào {keywordsOut}");
        }
        return 0;
    }

    // "có dấu" = có ký tự ngoài ASCII (chữ tiếng Việt có dấu, đ/Đ)
    private static bool HasDiacritics(string s) => s.Any(c => c > 127);

    // "all" nếu mọi pattern bật, "x/y" nếu một phần, "" nếu không pattern nào
    private static string Mark(int on, int total) => on == 0 ? "" : on == total ? "all" : $"{on}/{total}";

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
