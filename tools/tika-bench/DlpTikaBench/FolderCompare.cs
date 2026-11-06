// [Thay đổi] File mới (benchmark thư mục đang theo dõi): so sánh 2 thư mục kết quả trích text (bộ đọc hiện tại "old" và Tika native "native")
// trên cùng 1 thư mục file THẬT (không có đáp án đúng). Thay vì chấm theo marker, đo điều mà service thực sự quyết định:
// số chỗ khớp regex, mức nhạy cảm do ClassificationEngine gán, và vùng định tuyến (Certain/Grey/Low/NoMatch) — đúng chuỗi
// Reader → Scanner → Engine → Router của FileWatcherService.ProcessFile.
// Chỉ in số liệu và tên file; KHÔNG in nội dung hay giá trị khớp.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DlpService.Classification;
using DlpService.Scanning;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpTikaBench;

internal static class FolderCompare
{
    private sealed record Row(int Index, string File, bool Ok, double Ms, int Chars, string? Error, string Outcome);

    private sealed record Verdict(int Matches, SensitivityLevel Level, RouteZone Zone, HashSet<string> Rules);

    public static int Run(string[] args)
    {
        string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string manifestDir = Opt("--manifest") ?? "", oldDir = Opt("--old") ?? "", nativeDir = Opt("--native") ?? "", md = Opt("--md") ?? "";
        var jo = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var files = JsonDocument.Parse(File.ReadAllText(Path.Combine(manifestDir, "manifest.json"))).RootElement.EnumerateArray()
            .Select(e => e.GetProperty("File").GetString()!).ToList();
        Dictionary<int, Row> LoadRows(string dir) => File.ReadAllLines(Path.Combine(dir, "rows.jsonl")).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<Row>(l, jo)!).ToDictionary(r => r.Index);
        var oldRows = LoadRows(oldDir);
        var natRows = LoadRows(nativeDir);
        string? Text(string dir, int i) { string p = Path.Combine(dir, "texts", $"{i}.txt"); return File.Exists(p) ? File.ReadAllText(p) : null; }

        // Cùng bộ rule và ngưỡng như service (appsettings.json: THigh 0.80, TLow 0.10)
        var rules = new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
        var scanner = new RegexScanner(rules);
        var engine = new ClassificationEngine(NullLogger.Instance);
        var router = new RouterOptions { THigh = 0.80, TLow = 0.10 };

        Verdict Analyze(string path, string text)
        {
            var scan = scanner.Scan(text);
            if (scan.Matches.Count == 0)
            {
                return new Verdict(0, SensitivityLevel.Public, RouteZone.NoMatch, []);
            }
            var (level, _) = engine.Classify(new ScanContext { FilePath = path, Content = text, Matches = scan.Matches });
            var s1 = FileScorer.Compute(scan.Matches, text.Length);
            var route = ScoreRouter.Route(level, s1, scan.Matches.Count, router);
            return new Verdict(scan.Matches.Count, level, route.Zone, scan.Matches.Select(m => m.RuleId ?? m.PatternName).ToHashSet());
        }

        string Ext(int i) => Path.GetExtension(files[i]).ToLowerInvariant();
        var sb = new StringBuilder();
        void L(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        double Median(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? double.NaN : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
        double P95(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? double.NaN : s[(int)Math.Ceiling(0.95 * (s.Count - 1))]; }
        string F(double v) => double.IsNaN(v) ? "-" : v.ToString("N1");
        var exts = Enumerable.Range(0, files.Count).Select(Ext).Distinct().OrderBy(x => x).ToList();

        // ---- 1. Thời gian và lỗi ----
        L("## 1. Thời gian trích text / file (ms) và lỗi");
        L("| Đuôi | Số file | Cũ: lỗi | Native: lỗi | Cũ: trung vị | Cũ: p95 | Cũ: max | Native: trung vị | Native: p95 | Native: max | Tổng cũ (s) | Tổng native (s) |");
        L("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var e in exts.Append("(tất cả)"))
        {
            var ids = Enumerable.Range(0, files.Count).Where(i => e == "(tất cả)" || Ext(i) == e).ToList();
            var o = ids.Where(i => oldRows[i].Ok).Select(i => oldRows[i].Ms).ToList();
            var n = ids.Where(i => natRows[i].Ok).Select(i => natRows[i].Ms).ToList();
            L($"| {e} | {ids.Count} | {ids.Count(i => !oldRows[i].Ok)} | {ids.Count(i => !natRows[i].Ok)} | {F(Median(o))} | {F(P95(o))} | {F(o.DefaultIfEmpty(double.NaN).Max())} | " +
              $"{F(Median(n))} | {F(P95(n))} | {F(n.DefaultIfEmpty(double.NaN).Max())} | {ids.Sum(i => oldRows[i].Ms) / 1000:N1} | {ids.Sum(i => natRows[i].Ms) / 1000:N1} |");
        }

        // ---- 2. So sánh text ----
        L("\n## 2. Text trích ra: giống hay khác");
        L("| Đuôi | Cả hai đọc được | Y hệt | Chỉ khác khoảng trắng | Khác nội dung | Native dài hơn >5% | Native ngắn hơn >5% | Chỉ một bên có text |");
        L("|---|---:|---:|---:|---:|---:|---:|---:|");
        var diffFiles = new List<(string File, double Ratio)>();
        var verdicts = new Dictionary<int, (Verdict? Old, Verdict? Nat)>();
        foreach (var e in exts)
        {
            int both = 0, same = 0, ws = 0, diff = 0, longer = 0, shorter = 0, one = 0;
            foreach (var i in Enumerable.Range(0, files.Count).Where(i => Ext(i) == e))
            {
                string? a = Text(oldDir, i), b = Text(nativeDir, i);
                Verdict? va = a != null ? Analyze(files[i], a) : null, vb = b != null ? Analyze(files[i], b) : null;
                verdicts[i] = (va, vb);
                if (a == null && b == null) continue;
                if (a == null || b == null) { one++; continue; }
                both++;
                if (a == b) { same++; continue; }
                string na = Regex.Replace(a, @"\s+", " ").Trim(), nb = Regex.Replace(b, @"\s+", " ").Trim();
                if (na == nb) { ws++; continue; }
                diff++;
                double ratio = (double)b.Length / Math.Max(1, a.Length);
                if (ratio > 1.05) longer++; else if (ratio < 0.95) shorter++;
                diffFiles.Add((Path.GetFileName(files[i]), ratio));
            }
            L($"| {e} | {both} | {same} | {ws} | {diff} | {longer} | {shorter} | {one} |");
        }

        // ---- 3. Kết luận của service ----
        L("\n## 3. Kết luận DLP của service trên text của mỗi bên (cả hai đọc được)");
        L("| Đuôi | Số file | Cùng số chỗ khớp | Cùng mức nhạy cảm | Cùng vùng định tuyến | Mức khác nhau | Vùng khác nhau |");
        L("|---|---:|---:|---:|---:|---:|---:|");
        var levelMatrix = new Dictionary<string, int>();
        var zoneMatrix = new Dictionary<string, int>();
        var onlyOld = new Dictionary<string, int>();
        var onlyNat = new Dictionary<string, int>();
        long totOld = 0, totNat = 0;
        var diffExamples = new List<string>();
        foreach (var e in exts)
        {
            var ids = verdicts.Where(kv => Ext(kv.Key) == e && kv.Value.Old != null && kv.Value.Nat != null).Select(kv => kv.Key).ToList();
            int sm = 0, sl = 0, sz = 0;
            foreach (var i in ids)
            {
                var (o, n) = (verdicts[i].Old!, verdicts[i].Nat!);
                totOld += o.Matches; totNat += n.Matches;
                if (o.Matches == n.Matches) sm++;
                if (o.Level == n.Level) sl++; else { var k = $"{o.Level} → {n.Level}"; levelMatrix[k] = levelMatrix.GetValueOrDefault(k) + 1; if (diffExamples.Count < 12) diffExamples.Add($"{Path.GetFileName(files[i])}: mức {k}"); }
                if (o.Zone == n.Zone) sz++; else { var k = $"{o.Zone} → {n.Zone}"; zoneMatrix[k] = zoneMatrix.GetValueOrDefault(k) + 1; }
                foreach (var r in o.Rules.Except(n.Rules)) onlyOld[r] = onlyOld.GetValueOrDefault(r) + 1;
                foreach (var r in n.Rules.Except(o.Rules)) onlyNat[r] = onlyNat.GetValueOrDefault(r) + 1;
            }
            L($"| {e} | {ids.Count} | {sm} | {sl} | {sz} | {ids.Count - sl} | {ids.Count - sz} |");
        }
        L($"\nTổng số chỗ khớp: bộ cũ {totOld:N0}, Tika native {totNat:N0}.");
        L("Mức đổi (cũ → native): " + (levelMatrix.Count == 0 ? "(không có)" : string.Join("; ", levelMatrix.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}"))));
        L("Vùng đổi (cũ → native): " + (zoneMatrix.Count == 0 ? "(không có)" : string.Join("; ", zoneMatrix.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}"))));
        L("Rule chỉ bộ cũ thấy (số file): " + (onlyOld.Count == 0 ? "(không có)" : string.Join(", ", onlyOld.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}={k.Value}"))));
        L("Rule chỉ Tika native thấy (số file): " + (onlyNat.Count == 0 ? "(không có)" : string.Join(", ", onlyNat.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}={k.Value}"))));
        if (diffExamples.Count > 0) { L("Ví dụ file đổi mức: " + string.Join("; ", diffExamples)); }
        if (diffFiles.Count > 0)
        {
            L($"\nFile có text khác nội dung ({diffFiles.Count}); tỉ lệ độ dài native/cũ: nhỏ nhất {diffFiles.Min(d => d.Ratio):N2}, trung vị {Median(diffFiles.Select(d => d.Ratio)):N2}, lớn nhất {diffFiles.Max(d => d.Ratio):N2}");
        }

        // ---- 4. Chi tiết các file mà kết luận khác nhau (chỉ khi truyền --detail) ----
        if (args.Contains("--detail"))
        {
            L("\n## 4. Chi tiết file có kết luận khác (ngữ cảnh ngắn, đã che chữ số)");
            string Mask(string t) => Regex.Replace(Regex.Replace(t, @"\s+", " "), @"\d", "#");
            int shown = 0;
            foreach (var kv in verdicts.OrderBy(k => k.Key))
            {
                if (kv.Value.Old == null || kv.Value.Nat == null) continue;
                var (vo, vn) = (kv.Value.Old, kv.Value.Nat);
                bool differs = vo.Level != vn.Level || vo.Zone != vn.Zone || !vo.Rules.SetEquals(vn.Rules);
                if (!differs || shown >= 14) continue;
                shown++;
                int i = kv.Key;
                string a = Text(oldDir, i)!, b = Text(nativeDir, i)!;
                var ma = scanner.Scan(a).Matches; var mb = scanner.Scan(b).Matches;
                var ca = ma.GroupBy(m => m.RuleId ?? m.PatternName).ToDictionary(g => g.Key, g => g.Count());
                var cb = mb.GroupBy(m => m.RuleId ?? m.PatternName).ToDictionary(g => g.Key, g => g.Count());
                var rd = ca.Keys.Union(cb.Keys).Where(k => ca.GetValueOrDefault(k) != cb.GetValueOrDefault(k)).OrderBy(k => k)
                    .Select(k => $"{k}: cũ {ca.GetValueOrDefault(k)} / native {cb.GetValueOrDefault(k)}");
                L($"- {Path.GetFileName(files[i])} [{Ext(i)}] mức {vo.Level}→{vn.Level}, vùng {vo.Zone}→{vn.Zone}; khác: {string.Join("; ", rd)}");
                // Bên nào thấy nhiều hơn ở rule đầu tiên bị lệch thì lấy ngữ cảnh ở bên đó, rồi tìm cùng vị trí ở bên kia
                var first = ca.Keys.Union(cb.Keys).Where(k => ca.GetValueOrDefault(k) != cb.GetValueOrDefault(k)).OrderBy(k => k).FirstOrDefault();
                if (first == null) continue;
                bool oldMore = ca.GetValueOrDefault(first) > cb.GetValueOrDefault(first);
                string src = oldMore ? a : b, other = oldMore ? b : a;
                var m0 = (oldMore ? ma : mb).First(m => (m.RuleId ?? m.PatternName) == first);
                string before = Mask(src[Math.Max(0, m0.Index - 40)..m0.Index]);
                string atSrc = Mask(src[Math.Max(0, m0.Index - 40)..Math.Min(src.Length, m0.Index + m0.Value.Length + 40)]);
                string anchor = before.Length > 25 ? before[^25..] : before;
                string otherMasked = Mask(other);
                int pos = anchor.Length >= 8 ? otherMasked.IndexOf(anchor, StringComparison.Ordinal) : -1;
                L($"    {(oldMore ? "bên cũ" : "bên native")} thấy: …{atSrc}…");
                L($"    {(oldMore ? "bên native" : "bên cũ")} cùng chỗ: " + (pos < 0 ? "(không tìm thấy đoạn trước đó — bố cục khác)" : "…" + otherMasked[pos..Math.Min(otherMasked.Length, pos + anchor.Length + 100)] + "…"));
            }
        }

        if (md.Length > 0) File.WriteAllText(md, sb.ToString(), new UTF8Encoding(true));
        return 0;
    }
}
