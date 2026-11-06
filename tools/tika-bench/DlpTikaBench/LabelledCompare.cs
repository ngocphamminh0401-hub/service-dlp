// [Thay đổi] File mới (đánh giá Tika native trên dữ liệu CÓ NHÃN): chạy CẢ HAI bộ đọc (extractor hiện tại và Tika native) trên cùng bộ
// rawdata có nhãn của DlpBaseline (D:\dlp-classifier\rawdata), cho qua CÙNG bộ quét + bộ phân loại của service, rồi chấm theo đúng
// định nghĩa của DlpBaseline: (A) nhị phân "nhạy cảm" (positive = nhãn confidential/top_secret, dự đoán positive = Confidential/Restricted),
// (B) xếp đúng 4 mức (accuracy, macro-F1). Nhờ vậy biết bên nào ĐÚNG hơn, không chỉ biết hai bên khác nhau.
// Quy ước giống DlpBaseline: file đọc lỗi tính là Public; không có chỗ khớp thì Public. Không lưu nội dung/giá trị khớp.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DlpService.Classification;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpTikaBench;

internal static class LabelledCompare
{
    private sealed record Sample(string Id, string Domain, string Label, SensitivityLevel Truth, bool Borderline, string? Docx, string? Pdf);

    private sealed record Item(Sample Sample, string Format, string Path);

    private sealed record Outcome(SensitivityLevel Level, int Matches, double ExtractMs, bool ReadOk, int Chars);

    public static int Run(string[] args)
    {
        string? Opt(string name, string? def = null) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        string raw = Opt("--raw", @"D:\dlp-classifier\rawdata")!;
        string dll = Opt("--dll", "")!;
        string? sha = Opt("--sha256");
        int limit = int.Parse(Opt("--limit", "0")!);
        int threads = int.Parse(Opt("--threads", "8")!);
        string outDir = Opt("--out", "labelled-out")!;
        Directory.CreateDirectory(outDir);

        // ---- Đọc nhãn (giống SampleLoader của DlpBaseline) ----
        var samples = new List<Sample>();
        foreach (var folder in Directory.EnumerateDirectories(raw).Order(StringComparer.OrdinalIgnoreCase))
        {
            string meta = Path.Combine(folder, "_metadata.jsonl");
            if (!File.Exists(meta)) continue;
            foreach (var line in File.ReadLines(meta).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    string? id = r.TryGetProperty("id", out var a) ? a.GetString() : null;
                    string? label = r.TryGetProperty("label", out var b) ? b.GetString() : null;
                    var truth = MapLabel(label);
                    if (id == null || label == null || truth == null) continue;
                    string? P(string key) => r.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
                        ? Path.Combine(folder, Path.GetFileName(p.GetString()!)) : null;
                    samples.Add(new Sample(id, r.TryGetProperty("domain", out var d) ? d.GetString() ?? "?" : "?", label, truth.Value,
                        r.TryGetProperty("is_borderline", out var bl) && bl.ValueKind == JsonValueKind.True, P("docx_path"), P("pdf_path")));
                }
                catch (JsonException) { }
            }
        }
        if (limit > 0)
        {
            var groups = samples.GroupBy(s => (s.Domain, s.Label)).ToList();
            int per = (int)Math.Ceiling(limit / (double)groups.Count);
            samples = groups.SelectMany(g => g.Take(per)).Take(limit).ToList();
        }
        var items = samples.SelectMany(s => new[] { new Item(s, "docx", s.Docx ?? ""), new Item(s, "pdf", s.Pdf ?? "") })
            .Where(i => i.Path.Length > 0 && File.Exists(i.Path)).ToList();
        Console.WriteLine($"Mẫu: {samples.Count:N0}; file: {items.Count:N0}; luồng: {threads}");

        // ---- Cùng bộ quét + phân loại của service ----
        var rules = new RuleLoader(NullLogger.Instance).LoadFromFolder(System.IO.Path.Combine(AppContext.BaseDirectory, "rules"));
        var scanner = new RegexScanner(rules);
        var engine = new ClassificationEngine(NullLogger.Instance);
        var reader = new FileReaderService();
        using var native = new NativeEngine(dll, sha, 0, 30_000);
        Console.WriteLine($"DLL SHA-256: {native.Sha256}");

        Outcome Analyze(string path, bool ok, string text, double ms)
        {
            if (!ok) return new Outcome(SensitivityLevel.Public, 0, ms, false, 0);
            var scan = scanner.Scan(text);
            if (scan.Matches.Count == 0) return new Outcome(SensitivityLevel.Public, 0, ms, true, text.Length);
            var level = engine.Classify(new ScanContext { FilePath = path, Content = text, Matches = scan.Matches }).Level;
            return new Outcome(level, scan.Matches.Count, ms, true, text.Length);
        }

        var oldOut = new Outcome[items.Count];
        var natOut = new Outcome[items.Count];
        int done = 0;
        var sw = Stopwatch.StartNew();
        // [Thay đổi] Dùng luồng riêng thay cho Parallel.For: handle isolate chỉ hợp lệ trên ĐÚNG luồng đã attach, và Parallel.For
        // không đảm bảo phần dọn dẹp (localFinally) chạy trên cùng luồng với phần khởi tạo (localInit) → detach nhầm luồng làm
        // GraalVM báo "Must either be at a safepoint or in native mode" và sập tiến trình ở lúc huỷ isolate.
        int next = -1;
        var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            IntPtr thread = native.Attach();
            try
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < items.Count)
                {
                var it = items[i];
                var t0 = Stopwatch.StartNew();
                var rr = reader.TryReadFile(it.Path);
                double m0 = t0.Elapsed.TotalMilliseconds;
                oldOut[i] = Analyze(it.Path, rr.Success, rr.Content ?? "", m0);

                t0.Restart();
                var (status, text) = native.Extract(thread, it.Path);
                double m1 = t0.Elapsed.TotalMilliseconds;
                bool ok = status is 0 or 5;
                natOut[i] = Analyze(it.Path, ok, Normalizer.ToNfc(text), m1);

                int n = Interlocked.Increment(ref done);
                if (n % 4000 == 0) Console.WriteLine($"  ... {n:N0}/{items.Count:N0} ({sw.Elapsed.TotalSeconds:N0}s)");
                }
            }
            finally { native.Detach(thread); }
        })).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        Console.WriteLine($"Xong trong {sw.Elapsed.TotalSeconds:N0}s");

        // ---- Chấm điểm ----
        var sb = new StringBuilder();
        void L(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        bool Pos(SensitivityLevel l) => l >= SensitivityLevel.Confidential;

        (double? P, double? R, double? F1, int Tp, int Fp, int Fn, int Tn) Binary(IEnumerable<int> ids, Outcome[] o)
        {
            int tp = 0, fp = 0, fn = 0, tn = 0;
            foreach (var i in ids)
            {
                bool t = Pos(items[i].Sample.Truth), p = Pos(o[i].Level);
                if (t && p) tp++; else if (!t && p) fp++; else if (t) fn++; else tn++;
            }
            double? pr = tp + fp == 0 ? null : (double)tp / (tp + fp), rc = tp + fn == 0 ? null : (double)tp / (tp + fn);
            double? f1 = pr is double a && rc is double b && a + b > 0 ? 2 * a * b / (a + b) : null;
            return (pr, rc, f1, tp, fp, fn, tn);
        }

        (double Acc, double MacroF1) FourLevel(IEnumerable<int> ids, Outcome[] o)
        {
            var c = new int[4, 4]; // [truth, pred]
            int total = 0;
            foreach (var i in ids) { c[(int)items[i].Sample.Truth, (int)o[i].Level]++; total++; }
            double acc = total == 0 ? 0 : Enumerable.Range(0, 4).Sum(k => c[k, k]) / (double)total;
            double f1Sum = 0;
            for (int k = 0; k < 4; k++)
            {
                int tp = c[k, k], fp = Enumerable.Range(0, 4).Sum(r => c[r, k]) - tp, fn = Enumerable.Range(0, 4).Sum(q => c[k, q]) - tp;
                f1Sum += 2 * tp + fp + fn == 0 ? 0 : 2.0 * tp / (2 * tp + fp + fn);
            }
            return (acc, f1Sum / 4);
        }

        string Pct(double? v) => v is double d ? $"{d * 100:N1}%" : "-";
        var all = Enumerable.Range(0, items.Count).ToList();
        var groupsToShow = new List<(string Name, List<int> Ids)>
        {
            ("Tất cả", all),
            ("docx", all.Where(i => items[i].Format == "docx").ToList()),
            ("pdf", all.Where(i => items[i].Format == "pdf").ToList()),
            ("Ca khó (is_borderline)", all.Where(i => items[i].Sample.Borderline).ToList()),
            ("Ca thường", all.Where(i => !items[i].Sample.Borderline).ToList())
        };
        foreach (var dom in items.Select(i => i.Sample.Domain).Distinct().OrderBy(x => x))
        {
            groupsToShow.Add(($"Domain {dom}", all.Where(i => items[i].Sample.Domain == dom).ToList()));
        }

        L("## A. Nhị phân \"nhạy cảm\" (positive = nhãn confidential/top_secret)");
        L("| Nhóm | Số file | Cũ: Precision | Cũ: Recall | Cũ: F1 | Native: Precision | Native: Recall | Native: F1 | Cũ: FP/FN | Native: FP/FN |");
        L("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (name, ids) in groupsToShow)
        {
            var o = Binary(ids, oldOut); var n = Binary(ids, natOut);
            L($"| {name} | {ids.Count:N0} | {Pct(o.P)} | {Pct(o.R)} | {Pct(o.F1)} | {Pct(n.P)} | {Pct(n.R)} | {Pct(n.F1)} | {o.Fp}/{o.Fn} | {n.Fp}/{n.Fn} |");
        }

        L("\n## B. Xếp đúng 4 mức (Public / Internal / Confidential / Restricted)");
        L("| Nhóm | Cũ: Accuracy | Cũ: Macro-F1 | Native: Accuracy | Native: Macro-F1 |");
        L("|---|---:|---:|---:|---:|");
        foreach (var (name, ids) in groupsToShow.Take(5))
        {
            var o = FourLevel(ids, oldOut); var n = FourLevel(ids, natOut);
            L($"| {name} | {o.Acc * 100:N1}% | {o.MacroF1 * 100:N1}% | {n.Acc * 100:N1}% | {n.MacroF1 * 100:N1}% |");
        }

        L("\n## C. Hai bộ đọc đồng ý với nhau đến đâu (trên cùng 1 file)");
        int sameLevel = all.Count(i => oldOut[i].Level == natOut[i].Level);
        int sameMatches = all.Count(i => oldOut[i].Matches == natOut[i].Matches);
        int bothRight = all.Count(i => oldOut[i].Level == items[i].Sample.Truth && natOut[i].Level == items[i].Sample.Truth);
        int onlyOldRight = all.Count(i => oldOut[i].Level == items[i].Sample.Truth && natOut[i].Level != items[i].Sample.Truth);
        int onlyNatRight = all.Count(i => natOut[i].Level == items[i].Sample.Truth && oldOut[i].Level != items[i].Sample.Truth);
        int bothWrong = all.Count - bothRight - onlyOldRight - onlyNatRight;
        L($"- Cùng mức dự đoán: {sameLevel:N0}/{all.Count:N0} ({100.0 * sameLevel / all.Count:N1}%); cùng số chỗ khớp: {sameMatches:N0} ({100.0 * sameMatches / all.Count:N1}%)");
        L($"- Cả hai đúng mức: {bothRight:N0}; chỉ bộ cũ đúng: {onlyOldRight:N0}; chỉ Tika native đúng: {onlyNatRight:N0}; cả hai sai: {bothWrong:N0}");
        L($"- Đọc lỗi: cũ {oldOut.Count(o => !o.ReadOk):N0}, native {natOut.Count(o => !o.ReadOk):N0}");
        double Sum(Outcome[] o) => o.Sum(x => x.ExtractMs) / 1000;
        L($"- Tổng thời gian trích (cộng dồn các luồng): cũ {Sum(oldOut):N0}s, native {Sum(natOut):N0}s");

        // Chi tiết từng file (chỉ mã/mức/số liệu)
        var csv = new StringBuilder("id,format,domain,truth,old_level,native_level,old_matches,native_matches,old_chars,native_chars,old_ms,native_ms\n");
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            csv.AppendLine($"{it.Sample.Id},{it.Format},{it.Sample.Domain},{it.Sample.Truth},{oldOut[i].Level},{natOut[i].Level},{oldOut[i].Matches},{natOut[i].Matches},{oldOut[i].Chars},{natOut[i].Chars},{oldOut[i].ExtractMs:F1},{natOut[i].ExtractMs:F1}");
        }
        File.WriteAllText(System.IO.Path.Combine(outDir, "labelled.csv"), csv.ToString(), new UTF8Encoding(true));
        File.WriteAllText(System.IO.Path.Combine(outDir, "labelled.md"), sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"\nChi tiết: {System.IO.Path.GetFullPath(System.IO.Path.Combine(outDir, "labelled.csv"))}");
        return 0;
    }

    private static SensitivityLevel? MapLabel(string? label) => label?.Trim().ToLowerInvariant() switch
    {
        "public" => SensitivityLevel.Public,
        "internal" => SensitivityLevel.Internal,
        "confidential" => SensitivityLevel.Confidential,
        "top_secret" or "secret" or "restricted" => SensitivityLevel.Restricted,
        _ => null
    };
}
