// [Thay đổi] File mới: benchmark có ĐÁP ÁN ĐÚNG — so sánh extractor hiện tại của DlpService với Apache Tika Server trên bộ file
// do DlpTestGen sinh (corpus/manifest.json). Đo 4 nhóm chỉ số: thời gian trích/file, RAM đỉnh, chất lượng text, tỉ lệ lỗi.
//
// Thiết kế đo cho SẠCH:
//  - Extractor cũ chạy trong 1 TIẾN TRÌNH CON riêng (corpus-extract): RAM đỉnh đo bằng PeakWorkingSet64 của đúng tiến trình đó,
//    không lẫn bộ nhớ của regex scanner hay của Tika.
//  - Tika Server do chính tool tự khởi động (java -jar ... -p <cổng>), RAM = tổng WorkingSet của các tiến trình java mới sinh ra,
//    lấy mẫu mỗi 100 ms.
//  - Giai đoạn trích CHỈ trích text và lưu ra đĩa; chấm điểm (đối chiếu đáp án + quét regex) làm SAU, nên không tính vào thời gian/RAM.
//  - Chạy khởi động vài file trước khi đo (Tika nạp lớp Java, .NET JIT).
// Không in và không lưu nội dung nhạy cảm vào báo cáo: chỉ số liệu + tên vị trí/rule. Text trích ra lưu tạm ở thư mục --out.
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpTikaBench;

internal static class CorpusBench
{
    private const int MaxChars = 2_000_000;   // cùng giới hạn với FileReaderService
    private const int WarmupFiles = 5;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    private sealed record MarkerDto(string Kind, string Loc, string Value);

    private sealed class EntryDto
    {
        public string File { get; set; } = "";
        public string Format { get; set; } = "";
        public string Category { get; set; } = "";
        public string Phase { get; set; } = "";
        public long Size { get; set; }
        public List<MarkerDto> Markers { get; set; } = [];
    }

    // Outcome: ok | fail (từ chối êm, có thông báo) | timeout | crash (mất kết nối/server chết)
    private sealed record Extract(bool Ok, string Text, string? Error, string Outcome);

    private sealed record Row(int Index, string File, bool Ok, double Ms, int Chars, string? Error, string Outcome);

    private sealed record Ram(double BaselineMb, double PeakNormalMb, double PeakFinalMb, int Restarts);

    public static async Task<int> Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string cmd = args[0];
        string corpus = args.Length > 1 ? args[1] : "";
        string? Opt(string name, string? def = null) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        string outDir = Path.GetFullPath(Opt("--out") ?? "tika-bench-out");
        if (!Directory.Exists(corpus) || !File.Exists(Path.Combine(corpus, "manifest.json")))
        {
            Console.Error.WriteLine("Cách dùng: DlpTikaBench corpus-all <thư-mục-corpus> --jar <tika-server.jar> [--out <thư-mục>] [--port 9999]");
            return 1;
        }
        corpus = Path.GetFullPath(corpus);
        var entries = LoadManifest(corpus);

        return cmd switch
        {
            "corpus-extract" => await ExtractOldMode(entries, corpus, Path.Combine(outDir, "old")),
            "corpus-all" => await All(entries, corpus, outDir, Opt("--jar") ?? "", int.Parse(Opt("--port", "9999")!)),
            // [Thay đổi] Bước 2.2: 2 lệnh phụ để chấm điểm 1 engine ngoài (VD Tika chạy trên JVM, sau này là DLL native)
            "corpus-list" => ListFiles(entries, Opt("--out") ?? "files.txt"),
            "corpus-score" => ScoreOne(entries, Opt("--dir") ?? ""),
            // [Thay đổi] Bước 2.5: chạy bản DLL native trong tiến trình con riêng + so sánh nhiều engine cạnh nhau
            "corpus-extract-native" => await ExtractNativeMode(entries, corpus, Path.Combine(outDir, "native"), Opt("--dll") ?? "",
                int.Parse(Opt("--threads", "1")!), args.Contains("--gc-large"), Opt("--sha256"), int.Parse(Opt("--timeout-ms", "30000")!)),
            "corpus-detect" => await DetectMode(entries, corpus, Opt("--dll") ?? ""),
            "corpus-compare" => Compare(entries, Opt("--engines") ?? "", Opt("--md")),
            _ => 1
        };
    }

    // [Thay đổi] Trích bằng tika_native.dll. Chạy trong tiến trình riêng để RAM đỉnh (PeakWorkingSet64) là RAM THẬT của
    // .NET + DLL native (isolate Java nằm trong cùng tiến trình). threads > 1: mỗi luồng tự attach vào isolate.
    private static Task<int> ExtractNativeMode(List<EntryDto> entries, string corpus, string outDir, string dll, int threads, bool gcLarge, string? sha, int timeoutMs)
    {
        Directory.CreateDirectory(Path.Combine(outDir, "texts"));
        using var eng = new NativeEngine(dll, sha, 0, timeoutMs);
        Console.WriteLine($"[native] SHA-256 của DLL: {eng.Sha256}");
        var proc = Process.GetCurrentProcess();

        Row One(IntPtr t, int idx, EntryDto e)
        {
            var sw = Stopwatch.StartNew();
            var (status, text) = eng.Extract(t, Path.Combine(corpus, e.File));
            sw.Stop();
            // Quy ước giống các engine khác: rỗng (mã 5) vẫn tính "ok" nhưng 0 ký tự
            bool ok = status is 0 or 5;
            var x = new Extract(ok, text, ok ? (status == 5 ? "empty" : null) : $"native status {status}", ok ? "ok" : status == 7 ? "timeout" : "fail"); // [Thay đổi] mã 7 = quá hạn
            SaveText(outDir, idx, x);
            return new Row(idx, e.File, ok, sw.Elapsed.TotalMilliseconds, text.Length, x.Error, x.Outcome);
        }

        var ordered = Ordered(entries).ToList();
        foreach (var (idx, e) in ordered.Where(x => x.Entry.Phase == "normal").Take(WarmupFiles))
        {
            One(eng.MainThread, idx, e);
        }
        GC.Collect();
        proc.Refresh();
        double baseline = Mb(proc.WorkingSet64);
        double peakNormal = 0;

        var rows = new System.Collections.Concurrent.ConcurrentBag<Row>();
        foreach (var phase in new[] { "normal", "large" })
        {
            if (phase == "large")
            {
                proc.Refresh();
                peakNormal = Mb(proc.PeakWorkingSet64);
            }
            var work = new System.Collections.Concurrent.ConcurrentQueue<(int Index, EntryDto Entry)>(ordered.Where(x => x.Entry.Phase == phase));
            if (threads <= 1)
            {
                while (work.TryDequeue(out var w))
                {
                    rows.Add(One(eng.MainThread, w.Index, w.Entry));
                    if (gcLarge && phase == "large") eng.CollectGarbage(eng.MainThread);
                }
            }
            else
            {
                var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
                {
                    IntPtr t = eng.Attach();
                    try
                    {
                        while (work.TryDequeue(out var w))
                        {
                            rows.Add(One(t, w.Index, w.Entry));
                            if (gcLarge && phase == "large") eng.CollectGarbage(t);
                        }
                    }
                    finally { eng.Detach(t); }
                })).ToList();
                workers.ForEach(w => w.Start());
                workers.ForEach(w => w.Join());
            }
        }
        proc.Refresh();
        double peakFinal = Mb(proc.PeakWorkingSet64);
        File.WriteAllLines(Path.Combine(outDir, "rows.jsonl"), rows.OrderBy(r => r.Index).Select(r => JsonSerializer.Serialize(r)));
        File.WriteAllText(Path.Combine(outDir, "ram.json"), JsonSerializer.Serialize(new Ram(baseline, peakNormal, peakFinal, 0)));
        Console.WriteLine($"[native] xong {entries.Count} file, {threads} luồng. RAM: nền {baseline:N0} MB, đỉnh file thường {peakNormal:N0} MB, đỉnh cuối {peakFinal:N0} MB");
        return Task.FromResult(0);
    }

    // [Thay đổi] Bước 2.6: thử tika_detect QUA DLL. Với file hợp lệ (thường, lớn, đổi đuôi) so MIME Tika trả về với loại THẬT của
    // nội dung (Entry.Format, không phải đuôi file). File lỗi cố ý chỉ in phân bố. Không in nội dung file.
    private static Task<int> DetectMode(List<EntryDto> entries, string corpus, string dll)
    {
        using var eng = new NativeEngine(dll);
        var t = eng.MainThread;
        var expected = new Dictionary<string, string[]>
        {
            ["docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
            ["pptx"] = ["application/vnd.openxmlformats-officedocument.presentationml.presentation"],
            ["xlsx"] = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
            ["xls"] = ["application/vnd.ms-excel"],
            ["pdf"] = ["application/pdf"],
            ["txt"] = ["text/plain"],
            ["csv"] = ["text/csv", "text/plain"],
            ["eml"] = ["message/rfc822"]
        };
        var sw = new Stopwatch();
        var perFmt = new Dictionary<string, (int Ok, int Total, Dictionary<string, int> Wrong)>();
        var faultMimes = new Dictionary<string, int>();
        var times = new List<double>();
        var renamed = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            sw.Restart();
            string mime = eng.Detect(t, Path.Combine(corpus, e.File));
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (e.Category is "corrupt" or "garbage" or "empty")
            {
                string key = $"{e.Category}/{e.Format}: {mime}";
                faultMimes[key] = faultMimes.GetValueOrDefault(key) + 1;
                continue;
            }
            bool ok = expected[e.Format].Contains(mime);
            var cur = perFmt.GetValueOrDefault(e.Format, (0, 0, new Dictionary<string, int>()));
            if (!ok) cur.Item3[mime] = cur.Item3.GetValueOrDefault(mime) + 1;
            perFmt[e.Format] = (cur.Item1 + (ok ? 1 : 0), cur.Item2 + 1, cur.Item3);
            if (e.Category == "renamed") renamed.Add($"{e.File.Split('/').Last()} -> {mime} ({(ok ? "đúng loại thật" : "SAI")})");
        }
        Console.WriteLine("| Loại thật | Đúng / tổng | MIME sai (nếu có) |");
        Console.WriteLine("|---|---:|---|");
        foreach (var kv in perFmt.OrderBy(k => k.Key))
            Console.WriteLine($"| {kv.Key} | {kv.Value.Ok}/{kv.Value.Total} | {(kv.Value.Wrong.Count == 0 ? "-" : string.Join("; ", kv.Value.Wrong.Select(w => $"{w.Key} x{w.Value}")))} |");
        Console.WriteLine("\nFile đổi đuôi:");
        renamed.ForEach(r => Console.WriteLine("  " + r));
        Console.WriteLine("\nFile lỗi cố ý (loại/định dạng: MIME Tika trả về):");
        foreach (var kv in faultMimes.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key} x{kv.Value}");
        times.Sort();
        Console.WriteLine($"\nThời gian tika_detect: trung vị {times[times.Count / 2]:N2} ms, p95 {times[(int)(times.Count * 0.95)]:N2} ms, tối đa {times[^1]:N1} ms");
        return Task.FromResult(0);
    }

    // [Thay đổi] So sánh N engine cạnh nhau: --engines "cũ=<thư-mục>,tika=<thư-mục>,native=<thư-mục>" (mỗi thư mục có rows.jsonl,
    // texts/, và tuỳ chọn ram.json). Cùng cách chấm điểm với Report()/ScoreOne().
    private static int Compare(List<EntryDto> entries, string spec, string? mdPath)
    {
        var engines = spec.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('=', 2)).Select(a => (Name: a[0], Dir: a[1])).ToList();
        if (engines.Count < 2) { Console.Error.WriteLine("Cần ít nhất 2 engine: --engines tên=thư-mục,tên=thư-mục"); return 1; }
        var rows = new Dictionary<string, Dictionary<int, Row>>();
        var found = new Dictionary<string, Dictionary<int, bool[]>>();
        var ram = new Dictionary<string, Ram?>();
        foreach (var (name, dir) in engines)
        {
            rows[name] = File.ReadAllLines(Path.Combine(dir, "rows.jsonl")).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<Row>(l, Json)!).ToDictionary(r => r.Index);
            found[name] = [];
            for (int i = 0; i < entries.Count; i++)
            {
                string tp = Path.Combine(dir, "texts", $"{i}.txt");
                string n = File.Exists(tp) ? Norm(File.ReadAllText(tp)) : "";
                found[name][i] = entries[i].Markers.Select(m => n.Contains(Norm(m.Value), StringComparison.Ordinal)).ToArray();
            }
            string rp = Path.Combine(dir, "ram.json");
            ram[name] = File.Exists(rp) ? JsonSerializer.Deserialize<Ram>(File.ReadAllText(rp), Json) : null;
        }

        var sb = new StringBuilder();
        void L(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        string[] fmts = ["txt", "csv", "docx", "pptx", "xlsx", "xls", "pdf", "eml"];
        string Hdr(string prefix) => "| Định dạng | " + string.Join(" | ", engines.Select(e => prefix.Replace("{}", e.Name))) + " |";
        string Sep() => "|---|" + string.Join("|", engines.Select(_ => "---:")) + "|";
        double Median(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? double.NaN : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
        double P95(IEnumerable<double> v) { var s = v.OrderBy(x => x).ToList(); return s.Count == 0 ? double.NaN : s[(int)Math.Ceiling(0.95 * (s.Count - 1))]; }
        IEnumerable<int> Idx(Func<EntryDto, bool> p) => Enumerable.Range(0, entries.Count).Where(i => p(entries[i]));
        string F(double v, string f = "N1") => double.IsNaN(v) ? "-" : v.ToString(f);

        L("## Thời gian trích / file thường (ms, trung vị; trong ngoặc là p95)");
        L(Hdr("{}")); L(Sep());
        foreach (var f in fmts)
        {
            var ids = Idx(e => e.Category == "normal" && e.Format == f).ToList();
            L($"| {f} | " + string.Join(" | ", engines.Select(e => { var v = ids.Where(i => rows[e.Name].TryGetValue(i, out var r) && r.Ok).Select(i => rows[e.Name][i].Ms).ToList(); return $"{F(Median(v))} ({F(P95(v))})"; })) + " |");
        }

        L("\n## File lớn (ms | ký tự | marker tìm thấy)");
        L(Hdr("{}")); L(Sep());
        foreach (var f in fmts)
        {
            var i = Idx(e => e.Category == "large" && e.Format == f).FirstOrDefault(-1);
            if (i < 0) continue;
            L($"| {f} | " + string.Join(" | ", engines.Select(e => rows[e.Name].TryGetValue(i, out var r) ? $"{r.Ms:N0} ms · {r.Chars:N0} · {found[e.Name][i].Count(x => x)}/{entries[i].Markers.Count}" : "-")) + " |");
        }

        L("\n## RAM (MB): nền / đỉnh file thường / đỉnh cuối");
        foreach (var e in engines) L($"- **{e.Name}**: " + (ram[e.Name] is { } r ? $"{r.BaselineMb:N0} / {r.PeakNormalMb:N0} / {r.PeakFinalMb:N0}" : "(không có số liệu)"));

        L("\n## Chất lượng: % marker tìm thấy (file thường)");
        L(Hdr("{}")); L(Sep());
        foreach (var f in fmts)
        {
            var ids = Idx(e => e.Category == "normal" && e.Format == f).ToList();
            int tot = ids.Sum(i => entries[i].Markers.Count);
            L($"| {f} | " + string.Join(" | ", engines.Select(e => $"{(tot == 0 ? 0 : 100.0 * ids.Sum(i => found[e.Name][i].Count(x => x)) / tot):N1}%")) + " |");
        }

        L("\n## File đổi đuôi (marker tìm thấy / tổng)");
        L(Hdr("{}")); L(Sep());
        foreach (var i in Idx(e => e.Category == "renamed"))
            L($"| {entries[i].File.Split('/').Last()} | " + string.Join(" | ", engines.Select(e => $"{found[e.Name][i].Count(x => x)}/{entries[i].Markers.Count}")) + " |");

        L("\n## Tỉ lệ lỗi");
        var valid = Idx(e => e.Category is "normal" or "large" or "renamed").ToList();
        foreach (var e in engines) L($"- **{e.Name}** file hợp lệ lỗi: {valid.Count(i => !(rows[e.Name].TryGetValue(i, out var r) && r.Ok))}/{valid.Count}");
        foreach (var cat in new[] { "corrupt", "garbage", "empty" })
        {
            var ids = Idx(e => e.Category == cat).ToList();
            L($"- file {cat} ({ids.Count}): " + string.Join("; ", engines.Select(e =>
                $"{e.Name}: từ chối {ids.Count(i => rows[e.Name].TryGetValue(i, out var r) && !r.Ok)}, trả text {ids.Count(i => rows[e.Name].TryGetValue(i, out var r) && r.Ok && r.Chars > 0)}, rỗng {ids.Count(i => rows[e.Name].TryGetValue(i, out var r) && r.Ok && r.Chars == 0)}")));
        }
        if (mdPath != null) File.WriteAllText(mdPath, sb.ToString(), new UTF8Encoding(true));
        return 0;
    }

    // [Thay đổi] Ghi danh sách đường dẫn tương đối theo ĐÚNG thứ tự trong manifest (chỉ số dòng = chỉ số file trong manifest)
    private static int ListFiles(List<EntryDto> entries, string outFile)
    {
        File.WriteAllLines(outFile, entries.Select(e => e.File), new UTF8Encoding(false));
        Console.WriteLine($"Đã ghi {entries.Count} dòng vào {Path.GetFullPath(outFile)}");
        return 0;
    }

    // [Thay đổi] Chấm điểm 1 thư mục kết quả (rows.jsonl + texts/<chỉ-số>.txt) theo đáp án: % marker tìm thấy theo định dạng,
    // vị trí còn thiếu, và số file lỗi. Cùng cách tính với Report() nên so được với bảng của Tika Server / extractor cũ.
    private static int ScoreOne(List<EntryDto> entries, string dir)
    {
        string rowsPath = Path.Combine(dir, "rows.jsonl");
        if (!File.Exists(rowsPath))
        {
            Console.Error.WriteLine($"Không thấy {rowsPath}");
            return 1;
        }
        var rows = File.ReadAllLines(rowsPath).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<Row>(l, Json)!).ToDictionary(r => r.Index);
        var found = new Dictionary<int, bool[]>();
        for (int i = 0; i < entries.Count; i++)
        {
            string tp = Path.Combine(dir, "texts", $"{i}.txt");
            string n = File.Exists(tp) ? Norm(File.ReadAllText(tp)) : "";
            found[i] = entries[i].Markers.Select(m => n.Contains(Norm(m.Value), StringComparison.Ordinal)).ToArray();
        }

        string[] fmts = ["txt", "csv", "docx", "pptx", "xlsx", "xls", "pdf", "eml"];
        Console.WriteLine("| Định dạng | Marker | Tìm thấy | % | File lỗi / tổng (file hợp lệ) |");
        Console.WriteLine("|---|---:|---:|---:|---:|");
        foreach (var f in fmts)
        {
            var ids = Enumerable.Range(0, entries.Count).Where(i => entries[i].Format == f && entries[i].Category == "normal").ToList();
            int tot = ids.Sum(i => entries[i].Markers.Count), hit = ids.Sum(i => found[i].Count(x => x));
            var valid = Enumerable.Range(0, entries.Count).Where(i => entries[i].Format == f && entries[i].Category is "normal" or "large" or "renamed").ToList();
            int bad = valid.Count(i => !(rows.TryGetValue(i, out var r) && r.Ok));
            Console.WriteLine($"| {f} | {tot} | {hit} | {(tot == 0 ? 0 : 100.0 * hit / tot):N1}% | {bad}/{valid.Count} |");
        }

        Console.WriteLine("\nVị trí còn thiếu (file thường):");
        foreach (var f in fmts)
        {
            var ids = Enumerable.Range(0, entries.Count).Where(i => entries[i].Format == f && entries[i].Category == "normal").ToList();
            foreach (var loc in ids.SelectMany(i => entries[i].Markers.Select(m => m.Loc)).Distinct())
            {
                int tot = 0, hit = 0;
                foreach (var i in ids)
                    for (int k = 0; k < entries[i].Markers.Count; k++)
                        if (entries[i].Markers[k].Loc == loc) { tot++; if (found[i][k]) hit++; }
                if (hit < tot) Console.WriteLine($"  {f} / {loc}: {hit}/{tot}");
            }
        }

        var rn = Enumerable.Range(0, entries.Count).Where(i => entries[i].Category == "renamed").ToList();
        Console.WriteLine("\nFile đổi đuôi: " + string.Join("; ", rn.Select(i => $"{entries[i].File.Split('/').Last()}={found[i].Count(x => x)}/{entries[i].Markers.Count}")));
        var faults = Enumerable.Range(0, entries.Count).Where(i => entries[i].Category is "corrupt" or "garbage" or "empty").ToList();
        int fOk = faults.Count(i => rows.TryGetValue(i, out var r) && r.Ok && r.Chars > 0), fEmpty = faults.Count(i => rows.TryGetValue(i, out var r) && r.Ok && r.Chars == 0), fFail = faults.Count(i => rows.TryGetValue(i, out var r) && !r.Ok);
        Console.WriteLine($"File lỗi cố ý ({faults.Count}): trả text {fOk}, trả rỗng {fEmpty}, từ chối {fFail}");
        return 0;
    }

    // Thứ tự đo: giai đoạn "normal" (file thường + file lỗi + đổi đuôi) trước, rồi "large"
    private static List<EntryDto> LoadManifest(string corpus)
    {
        var all = JsonSerializer.Deserialize<List<EntryDto>>(File.ReadAllText(Path.Combine(corpus, "manifest.json")), Json)!;
        return all;
    }

    private static IEnumerable<(int Index, EntryDto Entry)> Ordered(List<EntryDto> entries) =>
        entries.Select((e, i) => (i, e)).OrderBy(x => x.e.Phase == "large" ? 1 : 0).ThenBy(x => x.i);

    // ===================== Trích bằng extractor cũ (tiến trình con) =====================
    private static Task<int> ExtractOldMode(List<EntryDto> entries, string corpus, string outDir)
    {
        Directory.CreateDirectory(Path.Combine(outDir, "texts"));
        var reader = new FileReaderService(); // tự nạp mọi extractor, giống Worker
        var proc = Process.GetCurrentProcess();

        Extract ExtractOne(string path)
        {
            var r = reader.TryReadFile(path);
            if (r.Success) return new Extract(true, r.Content ?? "", null, "ok");
            bool timeout = r.ErrorMessage?.Contains("quá 30s", StringComparison.Ordinal) == true || r.ErrorMessage?.Contains("quá 30", StringComparison.Ordinal) == true;
            return new Extract(false, "", r.ErrorMessage, timeout ? "timeout" : "fail");
        }

        foreach (var (_, e) in Ordered(entries).Where(x => x.Entry.Phase == "normal").Take(WarmupFiles))
        {
            ExtractOne(Path.Combine(corpus, e.File));
        }
        GC.Collect();
        proc.Refresh();
        double baseline = Mb(proc.WorkingSet64);
        double peakNormal = 0;

        string rowsPath = Path.Combine(outDir, "rows.jsonl");
        File.WriteAllText(rowsPath, "");
        string? lastPhase = null;
        foreach (var (idx, e) in Ordered(entries))
        {
            if (lastPhase == "normal" && e.Phase == "large")
            {
                proc.Refresh();
                peakNormal = Mb(proc.PeakWorkingSet64);
            }
            lastPhase = e.Phase;
            var sw = Stopwatch.StartNew();
            var x = ExtractOne(Path.Combine(corpus, e.File));
            sw.Stop();
            SaveText(outDir, idx, x);
            AppendRow(rowsPath, new Row(idx, e.File, x.Ok, sw.Elapsed.TotalMilliseconds, x.Text.Length, x.Error, x.Outcome));
        }
        proc.Refresh();
        double peakFinal = Mb(proc.PeakWorkingSet64);
        if (peakNormal == 0) peakNormal = peakFinal;
        File.WriteAllText(Path.Combine(outDir, "ram.json"), JsonSerializer.Serialize(new Ram(baseline, peakNormal, peakFinal, 0)));
        Console.WriteLine($"[cũ] xong {entries.Count} file. RAM: nền {baseline:N0} MB, đỉnh {peakFinal:N0} MB");
        return Task.FromResult(0);
    }

    // ===================== Điều phối: chạy cũ (tiến trình con) + Tika, rồi chấm điểm =====================
    private static async Task<int> All(List<EntryDto> entries, string corpus, string outDir, string jar, int port)
    {
        if (!File.Exists(jar))
        {
            Console.Error.WriteLine($"Không thấy file jar của Tika Server: {jar}");
            return 1;
        }
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);

        // ---- 1. Extractor cũ: tiến trình con ----
        Console.WriteLine("== [1/3] Trích bằng extractor hiện tại (tiến trình con) ==");
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var a in new[] { typeof(CorpusBench).Assembly.Location, "corpus-extract", corpus, "--out", outDir }) psi.ArgumentList.Add(a);
        using (var child = Process.Start(psi)!)
        {
            await child.WaitForExitAsync();
            if (child.ExitCode != 0) Console.WriteLine($"  CẢNH BÁO: tiến trình con thoát mã {child.ExitCode} (có thể đã sập giữa chừng; kết quả chỉ gồm phần đã ghi).");
        }

        // ---- 2. Tika ----
        Console.WriteLine("== [2/3] Trích bằng Apache Tika Server ==");
        var tikaOut = Path.Combine(outDir, "tika");
        Directory.CreateDirectory(Path.Combine(tikaOut, "texts"));
        var ramTika = await ExtractTika(entries, corpus, tikaOut, jar, port);

        // ---- 3. Chấm điểm ----
        Console.WriteLine("== [3/3] Chấm điểm (đối chiếu đáp án + quét regex) ==");
        Report(entries, outDir, ReadRam(Path.Combine(outDir, "old", "ram.json")), ramTika);
        return 0;
    }

    private static Ram ReadRam(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Ram>(File.ReadAllText(path), Json)! : new Ram(0, 0, 0, 0);

    // Khởi động/dừng Tika Server do tool quản lý, để biết chính xác tiến trình java nào là của Tika
    private sealed class TikaHost : IDisposable
    {
        private readonly string _jar;
        private readonly int _port;
        private Process? _proc;
        public DateTime LaunchedAt { get; private set; }
        public string Url => $"http://127.0.0.1:{_port}";
        public TikaHost(string jar, int port) { _jar = jar; _port = port; }
        public bool Alive => _proc is { HasExited: false };

        public async Task StartAsync(HttpClient http)
        {
            Stop();
            LaunchedAt = DateTime.Now.AddSeconds(-1);
            var psi = new ProcessStartInfo("java") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-jar", _jar, "-p", _port.ToString(), "-h", "127.0.0.1" }) psi.ArgumentList.Add(a);
            _proc = Process.Start(psi)!;
            _proc.OutputDataReceived += (_, _) => { }; // nuốt log để pipe không đầy
            _proc.ErrorDataReceived += (_, _) => { };
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            for (int i = 0; i < 120; i++)
            {
                try { await http.GetStringAsync(Url + "/tika"); return; }
                catch { await Task.Delay(500); }
            }
            throw new InvalidOperationException("Tika Server không khởi động được sau 60 giây.");
        }

        public void Stop()
        {
            try { if (_proc is { HasExited: false }) { _proc.Kill(entireProcessTree: true); _proc.WaitForExit(5000); } } catch { }
            _proc = null;
        }

        public void Dispose() => Stop();
    }

    // Lấy mẫu tổng WorkingSet của các tiến trình java sinh ra sau thời điểm khởi động Tika
    private sealed class RamSampler : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private double _peak;
        public double Current { get; private set; }
        public double Peak => _peak;
        public RamSampler(Func<DateTime> since) => _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                double sum = 0;
                foreach (var p in Process.GetProcessesByName("java"))
                {
                    try { if (p.StartTime >= since()) sum += Mb(p.WorkingSet64); } catch { }
                    finally { p.Dispose(); }
                }
                Current = sum;
                if (sum > _peak) _peak = sum;
                try { await Task.Delay(100, _cts.Token); } catch (OperationCanceledException) { }
            }
        });
        public void ResetPeak() => _peak = Current;
        public void Dispose() { _cts.Cancel(); try { _loop.Wait(2000); } catch { } }
    }

    private static async Task<Ram> ExtractTika(List<EntryDto> entries, string corpus, string outDir, string jar, int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var host = new TikaHost(jar, port);
        await host.StartAsync(http);
        using var sampler = new RamSampler(() => host.LaunchedAt);
        int restarts = 0;

        async Task<Extract> One(string path, bool retried = false)
        {
            try
            {
                await using var stream = File.OpenRead(path);
                using var req = new HttpRequestMessage(HttpMethod.Put, host.Url + "/tika") { Content = new StreamContent(stream) };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                using var resp = await http.SendAsync(req);
                if (resp.StatusCode != HttpStatusCode.OK) return new Extract(false, "", $"HTTP {(int)resp.StatusCode}", "fail");
                string text = Normalizer.ToNfc(await resp.Content.ReadAsStringAsync());
                if (text.Length > MaxChars) text = text[..MaxChars];
                return new Extract(true, text, null, "ok");
            }
            catch (TaskCanceledException)
            {
                // Quá 60 giây: coi là treo; khởi động lại server để file sau không bị ảnh hưởng
                restarts++;
                await host.StartAsync(http);
                return new Extract(false, "", "timeout 60s", "timeout");
            }
            catch (HttpRequestException ex)
            {
                // Mất kết nối = server chết (VD hết bộ nhớ khi gặp file xấu): khởi động lại rồi thử lại đúng 1 lần
                restarts++;
                await host.StartAsync(http);
                if (!retried)
                {
                    var again = await One(path, retried: true);
                    return again.Ok ? again : new Extract(false, "", ex.GetType().Name, "crash");
                }
                return new Extract(false, "", ex.GetType().Name, "crash");
            }
        }

        foreach (var (_, e) in Ordered(entries).Where(x => x.Entry.Phase == "normal").Take(WarmupFiles))
        {
            await One(Path.Combine(corpus, e.File));
        }
        await Task.Delay(1500);
        double baseline = sampler.Current;
        sampler.ResetPeak();

        string rowsPath = Path.Combine(outDir, "rows.jsonl");
        File.WriteAllText(rowsPath, "");
        double peakNormal = 0;
        string? lastPhase = null;
        foreach (var (idx, e) in Ordered(entries))
        {
            if (lastPhase == "normal" && e.Phase == "large")
            {
                peakNormal = sampler.Peak;
                sampler.ResetPeak();
            }
            lastPhase = e.Phase;
            var sw = Stopwatch.StartNew();
            var x = await One(Path.Combine(corpus, e.File));
            sw.Stop();
            SaveText(outDir, idx, x);
            AppendRow(rowsPath, new Row(idx, e.File, x.Ok, sw.Elapsed.TotalMilliseconds, x.Text.Length, x.Error, x.Outcome));
        }
        await Task.Delay(300);
        double peakLarge = sampler.Peak;
        if (peakNormal == 0) peakNormal = peakLarge;
        Console.WriteLine($"[Tika] xong. RAM: nền {baseline:N0} MB, đỉnh {Math.Max(peakNormal, peakLarge):N0} MB, khởi động lại server {restarts} lần");
        return new Ram(baseline, peakNormal, Math.Max(peakNormal, peakLarge), restarts);
    }

    private static double Mb(long bytes) => bytes / 1024.0 / 1024.0;

    private static void SaveText(string outDir, int idx, Extract x)
    {
        if (x.Ok && x.Text.Length > 0)
        {
            File.WriteAllText(Path.Combine(outDir, "texts", $"{idx}.txt"), x.Text, new UTF8Encoding(false));
        }
    }

    private static void AppendRow(string path, Row r) => File.AppendAllText(path, JsonSerializer.Serialize(r) + "\n");

    // ===================== Chấm điểm + báo cáo số liệu =====================
    private sealed record Scored(Row? Row, string Text, int Matches);

    // Bỏ khoảng trắng + NFC + chữ thường: so "giá trị đánh dấu" không bị lệch vì xuống dòng/dấu cách do định dạng
    private static string Norm(string s) => new string(Normalizer.ToNfc(s).Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static void Report(List<EntryDto> entries, string outDir, Ram ramOld, Ram ramTika)
    {
        var rules = new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
        var scanner = new RegexScanner(rules);

        Dictionary<int, Row> LoadRows(string engine) => File.Exists(Path.Combine(outDir, engine == "old" ? "old" : "tika", "rows.jsonl"))
            ? File.ReadAllLines(Path.Combine(outDir, engine == "old" ? "old" : "tika", "rows.jsonl")).Where(l => l.Length > 0)
                .Select(l => JsonSerializer.Deserialize<Row>(l, Json)!).ToDictionary(r => r.Index)
            : [];
        var rowsBy = new Dictionary<string, Dictionary<int, Row>> { ["old"] = LoadRows("old"), ["tika"] = LoadRows("tika") };

        // Điểm từng file x từng bên: marker nào tìm thấy, số chỗ khớp regex
        var found = new Dictionary<string, Dictionary<int, bool[]>> { ["old"] = [], ["tika"] = [] };
        var regex = new Dictionary<string, Dictionary<int, int>> { ["old"] = [], ["tika"] = [] };
        foreach (var engine in new[] { "old", "tika" })
        {
            for (int i = 0; i < entries.Count; i++)
            {
                string tp = Path.Combine(outDir, engine, "texts", $"{i}.txt");
                string text = File.Exists(tp) ? File.ReadAllText(tp) : "";
                string n = Norm(text);
                found[engine][i] = entries[i].Markers.Select(m => n.Contains(Norm(m.Value), StringComparison.Ordinal)).ToArray();
                regex[engine][i] = text.Length > 0 ? scanner.Scan(text).Matches.Count : 0;
            }
        }

        var sb = new StringBuilder();
        void L(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        string[] fmts = ["txt", "csv", "docx", "pptx", "xlsx", "xls", "pdf", "eml"];
        double Pct(int a, int b) => b == 0 ? double.NaN : 100.0 * a / b;
        string F(double v, string fmt = "N1") => double.IsNaN(v) ? "-" : v.ToString(fmt);
        double Pctl(IEnumerable<double> v, double p)
        {
            var s = v.OrderBy(x => x).ToList();
            if (s.Count == 0) return double.NaN;
            double rank = p * (s.Count - 1);
            int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
            return s[lo] + (s[hi] - s[lo]) * (rank - lo);
        }

        IEnumerable<int> Idx(Func<EntryDto, bool> pred) => Enumerable.Range(0, entries.Count).Where(i => pred(entries[i]));

        // ---- T1: thời gian trích / file (file thường, đã trích thành công) ----
        L("## 1. Thời gian trích text / file (ms) — file thường, chỉ tính file trích thành công");
        L("| Định dạng | n | Cũ: trung vị | Cũ: TB | Cũ: p95 | Cũ: max | Tika: trung vị | Tika: TB | Tika: p95 | Tika: max | Tika/Cũ (trung vị) |");
        L("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var f in fmts)
        {
            var ids = Idx(e => e.Category == "normal" && e.Format == f).ToList();
            var o = ids.Where(i => rowsBy["old"].TryGetValue(i, out var r) && r.Ok).Select(i => rowsBy["old"][i].Ms).ToList();
            var t = ids.Where(i => rowsBy["tika"].TryGetValue(i, out var r) && r.Ok).Select(i => rowsBy["tika"][i].Ms).ToList();
            double mo = Pctl(o, .5), mt = Pctl(t, .5);
            L($"| {f} | {ids.Count} | {F(mo)} | {F(o.DefaultIfEmpty(double.NaN).Average())} | {F(Pctl(o, .95))} | {F(o.DefaultIfEmpty(double.NaN).Max())} | " +
              $"{F(mt)} | {F(t.DefaultIfEmpty(double.NaN).Average())} | {F(Pctl(t, .95))} | {F(t.DefaultIfEmpty(double.NaN).Max())} | {F(mt / mo, "N1")}x |");
        }

        L();
        L("## 2. File lớn (1 file mỗi định dạng)");
        L("| Định dạng | Kích thước (MB) | Cũ: ms | Cũ: ký tự | Cũ: kết quả | Tika: ms | Tika: ký tự | Tika: kết quả | Marker tìm thấy (cũ / Tika) |");
        L("|---|---:|---:|---:|---|---:|---:|---|---|");
        foreach (var f in fmts)
        {
            var i = Idx(e => e.Category == "large" && e.Format == f).FirstOrDefault(-1);
            if (i < 0) continue;
            rowsBy["old"].TryGetValue(i, out var ro);
            rowsBy["tika"].TryGetValue(i, out var rt);
            int total = entries[i].Markers.Count;
            L($"| {f} | {entries[i].Size / 1024.0 / 1024.0:N1} | {F(ro?.Ms ?? double.NaN, "N0")} | {ro?.Chars:N0} | {Res(ro)} | {F(rt?.Ms ?? double.NaN, "N0")} | {rt?.Chars:N0} | {Res(rt)} | {found["old"][i].Count(x => x)}/{total} · {found["tika"][i].Count(x => x)}/{total} |");
        }

        L();
        L("## 3. RAM đỉnh (MB)");
        L("| | Nền (sau khởi động) | Đỉnh – file thường | Đỉnh – gồm file lớn | Tăng so với nền (đỉnh cuối) |");
        L("|---|---:|---:|---:|---:|");
        L($"| Extractor hiện tại (tiến trình .NET) | {ramOld.BaselineMb:N0} | {ramOld.PeakNormalMb:N0} | {ramOld.PeakFinalMb:N0} | {ramOld.PeakFinalMb - ramOld.BaselineMb:N0} |");
        L($"| Tika Server (tiến trình java) | {ramTika.BaselineMb:N0} | {ramTika.PeakNormalMb:N0} | {ramTika.PeakFinalMb:N0} | {ramTika.PeakFinalMb - ramTika.BaselineMb:N0} |");
        L($"Tika Server phải khởi động lại {ramTika.Restarts} lần trong lúc chạy.");
        L("(Ghi chú: nền của tiến trình .NET gồm cả runtime + thư viện đã nạp; đỉnh của tiến trình .NET lấy từ PeakWorkingSet64, đỉnh của Tika lấy mẫu mỗi 100 ms nên có thể thấp hơn đỉnh thật một chút.)");

        L();
        L("## 4. Chất lượng text — % giá trị đánh dấu tìm thấy trong text trích ra (file thường)");
        L("| Định dạng | Số marker | Cũ | Tika | Cũ: số chỗ khớp regex | Tika: số chỗ khớp regex |");
        L("|---|---:|---:|---:|---:|---:|");
        foreach (var f in fmts)
        {
            var ids = Idx(e => e.Category == "normal" && e.Format == f).ToList();
            int tot = ids.Sum(i => entries[i].Markers.Count);
            int fo = ids.Sum(i => found["old"][i].Count(x => x)), ft = ids.Sum(i => found["tika"][i].Count(x => x));
            L($"| {f} | {tot} | {F(Pct(fo, tot))}% | {F(Pct(ft, tot))}% | {ids.Sum(i => regex["old"][i])} | {ids.Sum(i => regex["tika"][i])} |");
        }

        L();
        L("### 4a. Theo vị trí đánh dấu (chỉ in vị trí mà ít nhất một bên < 100%)");
        L("| Định dạng | Vị trí | Marker | Cũ | Tika |");
        L("|---|---|---:|---:|---:|");
        foreach (var f in fmts)
        {
            var ids = Idx(e => e.Category == "normal" && e.Format == f).ToList();
            foreach (var loc in ids.SelectMany(i => entries[i].Markers.Select(m => m.Loc)).Distinct())
            {
                int tot = 0, fo = 0, ft = 0;
                foreach (var i in ids)
                    for (int k = 0; k < entries[i].Markers.Count; k++)
                        if (entries[i].Markers[k].Loc == loc) { tot++; if (found["old"][i][k]) fo++; if (found["tika"][i][k]) ft++; }
                if (fo < tot || ft < tot) L($"| {f} | {loc} | {tot} | {F(Pct(fo, tot))}% | {F(Pct(ft, tot))}% |");
            }
        }

        L();
        L("### 4b. Theo loại giá trị (mọi file thường)");
        L("| Loại | Marker | Cũ | Tika |");
        L("|---|---:|---:|---:|");
        foreach (var kind in new[] { "cccd", "vi", "email" })
        {
            int tot = 0, fo = 0, ft = 0;
            foreach (var i in Idx(e => e.Category == "normal"))
                for (int k = 0; k < entries[i].Markers.Count; k++)
                    if (entries[i].Markers[k].Kind == kind) { tot++; if (found["old"][i][k]) fo++; if (found["tika"][i][k]) ft++; }
            L($"| {kind} | {tot} | {F(Pct(fo, tot))}% | {F(Pct(ft, tot))}% |");
        }

        L();
        L("### 4c. File đổi đuôi (nội dung thật khác đuôi file)");
        L("| File | Cũ: kết quả | Cũ: marker | Tika: kết quả | Tika: marker |");
        L("|---|---|---:|---|---:|");
        foreach (var i in Idx(e => e.Category == "renamed"))
        {
            int tot = entries[i].Markers.Count;
            rowsBy["old"].TryGetValue(i, out var ro);
            rowsBy["tika"].TryGetValue(i, out var rt);
            L($"| {entries[i].File.Split('/').Last()} | {Res(ro)} | {found["old"][i].Count(x => x)}/{tot} | {Res(rt)} | {found["tika"][i].Count(x => x)}/{tot} |");
        }

        L();
        L("## 5. Tỉ lệ lỗi");
        L("### 5a. File hợp lệ (thường + lớn + đổi đuôi): số file trích THẤT BẠI / tổng");
        L("| Định dạng | Tổng | Cũ: lỗi | Cũ: % | Tika: lỗi | Tika: % |");
        L("|---|---:|---:|---:|---:|---:|");
        int gT = 0, gO = 0, gK = 0;
        foreach (var f in fmts)
        {
            var ids = Idx(e => e.Format == f && e.Category is "normal" or "large" or "renamed").ToList();
            int bo = ids.Count(i => !(rowsBy["old"].TryGetValue(i, out var r) && r.Ok));
            int bt = ids.Count(i => !(rowsBy["tika"].TryGetValue(i, out var r) && r.Ok));
            gT += ids.Count; gO += bo; gK += bt;
            L($"| {f} | {ids.Count} | {bo} | {F(Pct(bo, ids.Count))}% | {bt} | {F(Pct(bt, ids.Count))}% |");
        }
        L($"| **Tổng** | {gT} | {gO} | {F(Pct(gO, gT))}% | {gK} | {F(Pct(gK, gT))}% |");

        L();
        L("### 5b. File lỗi cố ý (cắt cụt / rác / rỗng): cách mỗi bên xử lý");
        L("| Loại | Số file | Cũ: từ chối êm | Cũ: trả text | Cũ: treo/sập | Tika: từ chối êm | Tika: trả text | Tika: treo/sập |");
        L("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var cat in new[] { "corrupt", "garbage", "empty" })
        {
            var ids = Idx(e => e.Category == cat).ToList();
            (int fail, int text, int bad) Cnt(string eng) => (
                ids.Count(i => rowsBy[eng].TryGetValue(i, out var r) && r.Outcome == "fail"),
                ids.Count(i => rowsBy[eng].TryGetValue(i, out var r) && r.Outcome == "ok"),
                ids.Count(i => !rowsBy[eng].TryGetValue(i, out var r) || r.Outcome is "timeout" or "crash"));
            var (a, b, c) = Cnt("old"); var (d, e2, g) = Cnt("tika");
            L($"| {cat} | {ids.Count} | {a} | {b} | {c} | {d} | {e2} | {g} |");
        }

        L();
        L("### 5c. Thông báo lỗi thường gặp (file hợp lệ + file lỗi)");
        foreach (var eng in new[] { "old", "tika" })
        {
            var errs = rowsBy[eng].Values.Where(r => !r.Ok).GroupBy(r => $"{entries[r.Index].Format}/{entries[r.Index].Category}: {r.Error}").OrderByDescending(g => g.Count()).Take(12);
            L($"- **{(eng == "old" ? "Cũ" : "Tika")}**: " + string.Join("; ", errs.Select(g => $"{g.Key} ×{g.Count()}")));
        }

        File.WriteAllText(Path.Combine(outDir, "summary.md"), sb.ToString(), new UTF8Encoding(true));
        Console.WriteLine($"\nBáo cáo số liệu: {Path.Combine(outDir, "summary.md")}");

        string Res(Row? r) => r == null ? "(không có)" : r.Ok ? "ok" : $"{r.Outcome}: {r.Error}";
    }
}
