// [Thay đổi] File mới (tích hợp Tika native): kiểm tra tích hợp THẬT qua đúng FileReaderService của DlpService (không đi đường tắt).
// Kịch bản: off (không cấu hình = mặc định), on (bật + ghim đúng mã băm), nosha (bật, không ghim), wrongsha (ghim sai).
// Với kịch bản on: đọc các file .doc/.ppt/.rtf trong danh sách, so TỪNG FILE với text của bản Tika chạy trên JVM
// (đã lưu ở thư mục --jvm), rồi đọc lại song song nhiều luồng để thử việc attach isolate từ ThreadPool.
// Chỉ in số liệu và tên file, không in nội dung.
using System.Collections.Concurrent;
using System.Diagnostics;
using DlpService.Reading;
using DlpService.Reading.Native;
using DlpService.Text;
using Microsoft.Extensions.Logging;

namespace DlpTikaBench;

internal static class ReaderNativeCheck
{
    public static int Run(string[] args)
    {
        string? Opt(string name, string? def = null) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        string scenario = Opt("--scenario", "off")!;
        string dll = Opt("--dll", "")!;
        string sha = Opt("--sha256", "")!;
        string list = Opt("--list", "")!;
        string jvmDir = Opt("--jvm", "")!;
        int threads = int.Parse(Opt("--threads", "8")!);
        int rounds = int.Parse(Opt("--rounds", "3")!);
        int timeoutMs = int.Parse(Opt("--timeout-ms", "30000")!);

        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var logger = loggerFactory.CreateLogger("TikaNative");

        if (scenario != "off")
        {
            TikaNativeRuntime.Configure(new TikaNativeOptions
            {
                Enabled = true,
                DllPath = dll,
                Sha256 = scenario == "wrongsha" ? new string('0', 64) : scenario == "nosha" ? "" : sha,
                TimeoutMs = timeoutMs
            }, logger);
        }

        var reader = new FileReaderService();
        string[] exts = [".doc", ".ppt", ".rtf", ".docx", ".pdf"];
        Console.WriteLine($"[{scenario}] Đuôi hỗ trợ: " + string.Join(" ", exts.Select(e => $"{e}={reader.SupportedExtensions.Contains(e)}")));
        if (scenario != "on")
        {
            return 0;
        }

        var files = File.ReadAllLines(list).Select((p, i) => (Path: p, Index: i))
            .Where(x => Path.GetExtension(x.Path).ToLowerInvariant() is ".doc" or ".ppt" or ".rtf").ToList();
        Console.WriteLine($"[on] {files.Count} file .doc/.ppt/.rtf");

        // FileReaderService bỏ các dòng trống ở ĐẦU text (chỉ ghép "\n" khi buffer đã có chữ) nên so sánh sau khi bỏ xuống dòng đầu
        string Norm(string s) => Normalizer.ToNfc(s).TrimStart('\n');
        string? Expected(int index)
        {
            string p = Path.Combine(jvmDir, "texts", $"{index}.txt");
            return File.Exists(p) ? Norm(File.ReadAllText(p)) : null;
        }

        // 1) Tuần tự
        int same = 0, diff = 0, failed = 0;
        var perExt = new Dictionary<string, List<double>>();
        foreach (var (path, index) in files)
        {
            var sw = Stopwatch.StartNew();
            var r = reader.TryReadFile(path);
            sw.Stop();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!perExt.TryGetValue(ext, out var l)) perExt[ext] = l = [];
            l.Add(sw.Elapsed.TotalMilliseconds);
            string? exp = Expected(index);
            if (!r.Success) { failed++; Console.WriteLine($"  LỖI {Path.GetFileName(path)}: {r.ErrorMessage}"); }
            else if (exp != null && Norm(r.Content ?? "") == exp) same++;
            else { diff++; Console.WriteLine($"  KHÁC {Path.GetFileName(path)} (dài {r.Content?.Length} so với JVM {exp?.Length})"); }
        }
        Console.WriteLine($"[on] Tuần tự: giống JVM {same} | khác {diff} | lỗi {failed}");
        foreach (var kv in perExt.OrderBy(k => k.Key))
        {
            var s = kv.Value.OrderBy(x => x).ToList();
            Console.WriteLine($"     {kv.Key}: {s.Count} file, trung vị {s[s.Count / 2]:N1} ms, tối đa {s[^1]:N0} ms");
        }

        // 2) Song song: nhiều luồng ThreadPool cùng gọi TryReadFile (mỗi lần gọi tự attach/detach isolate)
        int pDiff = 0, pFail = 0, pOk = 0;
        var work = Enumerable.Range(0, rounds).SelectMany(_ => files).ToList();
        var sw2 = Stopwatch.StartNew();
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = threads }, f =>
        {
            var r = reader.TryReadFile(f.Path);
            string? exp = Expected(f.Index);
            if (!r.Success) Interlocked.Increment(ref pFail);
            else if (exp != null && Norm(r.Content ?? "") == exp) Interlocked.Increment(ref pOk);
            else Interlocked.Increment(ref pDiff);
        });
        sw2.Stop();
        Console.WriteLine($"[on] Song song {threads} luồng x {work.Count} lượt đọc: giống JVM {pOk} | khác {pDiff} | lỗi {pFail} | {sw2.Elapsed.TotalSeconds:N1}s | số luồng ThreadPool đã dùng ~{ThreadPool.ThreadCount}");

        var proc = Process.GetCurrentProcess();
        Console.WriteLine($"[on] RAM tiến trình: {proc.WorkingSet64 / 1048576.0:N0} MB (đỉnh {proc.PeakWorkingSet64 / 1048576.0:N0} MB)");

        TikaNativeRuntime.Shutdown();
        Console.WriteLine("[on] Đã Shutdown (huỷ parser + isolate) không lỗi");
        return (diff + failed + pDiff + pFail) == 0 ? 0 : 2;
    }
}
