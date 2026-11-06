// [Thay đổi] File mới: đo ResultStore dưới tải song song.
//  1) Nạp sẵn lịch sử (--prefill file, ~10 % có job vùng xám) — giống DB thật sau vài lượt quét.
//  2) --threads luồng cùng xử lý --files file mới: TryGetFileState (tra cache) rồi SaveScan (ghi kết quả, ~8 % tạo job P1),
//     đúng thứ tự FileWatcherService.ProcessFile. Đo thời gian từng thao tác (gồm cả chờ khoá) + thông lượng.
// Không đụng DB thật của service; DB tạm xoá khi xong (trừ khi --keep).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DlpService.Storage;

namespace DlpDbBench;

internal static class Bench
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int prefill = ArgInt(args, "--prefill", 30_000);
        int files = ArgInt(args, "--files", 10_000);
        int threads = ArgInt(args, "--threads", 20);
        bool keep = args.Contains("--keep");

        string folder = Path.Combine(Path.GetTempPath(), "dlp-dbbench-" + Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(folder, "bench.db");
        try
        {
            using var store = new ResultStore(dbPath);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < prefill; i++)
            {
                store.SaveScan(Record($"C:\\lich-su\\f{i:D6}.docx", i, i % 10 == 0 ? LlmJobPriority.GreyZone : null));
            }
            Console.WriteLine($"Nạp sẵn {prefill:N0} file trong {sw.Elapsed.TotalSeconds:N1} giây");

            // Đo: mỗi luồng lấy file kế tiếp từ hàng đợi chung (giống ThreadPool xử lý file theo sự kiện)
            var cacheMs = new ConcurrentBag<double>();
            var saveMs = new ConcurrentBag<double>();
            int next = -1;
            sw.Restart();
            var workers = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < files)
                {
                    string path = $"C:\\moi\\f{i:D6}.pdf";
                    var t = Stopwatch.StartNew();
                    store.TryGetFileState(path);
                    cacheMs.Add(t.Elapsed.TotalMilliseconds);
                    t.Restart();
                    store.SaveScan(Record(path, prefill + i, i % 12 == 0 ? LlmJobPriority.GreyZone : null));
                    saveMs.Add(t.Elapsed.TotalMilliseconds);
                }
            })).ToArray();
            Task.WaitAll(workers);
            double seconds = sw.Elapsed.TotalSeconds;

            Console.WriteLine($"{files:N0} file, {threads} luồng: {seconds:N2} giây → {files / seconds:N0} file/giây");
            Print("Tra cache", cacheMs);
            Print("Ghi DB   ", saveMs);
            return 0;
        }
        finally
        {
            if (!keep)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            else
            {
                Console.WriteLine($"Giữ DB: {dbPath}");
            }
        }
    }

    // Kết quả quét giả: 5 finding (chỉ mã rule + vị trí, giống service), hash khác nhau cho mỗi file
    private static ScanRecord Record(string path, int n, int? priority) => new(
        path, 50_000 + n, DateTime.UtcNow, $"{n:x64}", 0.5, priority == null ? "final" : "provisional",
        NeedsReview: priority != null, "{\"level\":\"Confidential\"}",
        Enumerable.Range(0, 5).Select(k => new FindingRecord($"rule_{k:D3}", 0.8, null, false, 1, $"offset:{k * 100}")).ToList(),
        priority, RulesVer: "bench", MergePolicyVer: "bench", FileConf: 0.9, ConfBranch: "A");

    private static void Print(string name, ConcurrentBag<double> values)
    {
        var s = values.OrderBy(v => v).ToArray();
        double P(double p) => s[Math.Clamp((int)Math.Ceiling(p / 100 * s.Length) - 1, 0, s.Length - 1)];
        Console.WriteLine($"  {name}: TB {s.Average():N2} ms · p50 {P(50):N2} · p95 {P(95):N2} · p99 {P(99):N2} · max {s[^1]:N1}");
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
    }
}
