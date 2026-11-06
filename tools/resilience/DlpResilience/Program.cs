// [Thay đổi] File mới: test chịu lỗi (kiến trúc mục 3.2, 10, 13.2 "Chịu lỗi"). Kill THẬT (TerminateProcess, như "End task" /
// mất điện — process không kịp dọn dẹp) rồi khởi động lại, kiểm tra:
//   A. Kill service lúc đang quét: DB không hỏng, không mất/nhân đôi kết quả, không job trùng — so từng file với 1 lần chạy sạch.
//   B. Kill worker lúc job đang chạy (worker giả, vì chưa có worker LLM thật) + khởi động lại service (RecoverRunningJobs):
//      không mất job, mỗi job đúng 1 llm_result, không job nào bị xử lý lại ngoài số lần được khôi phục.
//
// Cách chạy:  dotnet run --project tools/resilience/DlpResilience [-- --kills 3 --keep]
//   --kills N : số lần kill mỗi kịch bản (mặc định 3)      --keep : giữ DB tạm để xem lại
// Service quét thư mục cố định D:\Service-DLP\watch-folder (Worker.cs); thư mục KHÔNG được đổi trong lúc test.
// DB tạm ở %TEMP%\dlp-resilience (service chạy với --Storage:DbPath=...), không đụng DB thật. Báo cáo: tools/resilience/results/.
using System.Diagnostics;
using System.Text;
using DlpResilience;
using DlpService.Reading;
using DlpService.Storage;

if (args.Length > 0 && args[0] == "fake-worker")
{
    return FakeWorker.Run(args[1], int.Parse(args[2]), int.Parse(args[3]));
}

int kills = args.SkipWhile(a => a != "--kills").Skip(1).Select(int.Parse).FirstOrDefault(3);
bool keep = args.Contains("--keep");
const string WatchFolder = @"D:\Service-DLP\watch-folder";

var report = new Report();
string work = Path.Combine(Path.GetTempPath(), "dlp-resilience");
if (Directory.Exists(work))
{
    Directory.Delete(work, true);
}

var reader = new FileReaderService();
int expected = Directory.EnumerateFiles(WatchFolder, "*", SearchOption.AllDirectories).Count(reader.IsSupported);
report.Line($"Thư mục quét: {WatchFolder} — {expected} file hỗ trợ. Kill mỗi kịch bản: {kills} lần.");

// ================= A. Kill service lúc đang quét =================
report.Section("A. Kill service lúc đang quét");
var clean = new Db(Path.Combine(work, "clean", "data", "dlp.db"));
var killed = new Db(Path.Combine(work, "killed", "data", "dlp.db"));

var sw = Stopwatch.StartNew();
using (var svc = ServiceProcess.Start(clean.Path))
{
    Wait.Until(() => clean.TryLong("SELECT COUNT(*) FROM scan_result") >= expected, TimeSpan.FromMinutes(15), "chạy sạch quét xong");
    svc.Kill();
}
report.Line($"Chạy sạch (không kill): {sw.Elapsed.TotalSeconds:0} s.");

for (int k = 1; k <= kills; k++)
{
    long target = expected * k / (kills + 1);
    using var svc = ServiceProcess.Start(killed.Path);
    Wait.Until(() => killed.TryLong("SELECT COUNT(*) FROM scan_result") >= target, TimeSpan.FromMinutes(15), $"quét tới {target} file");
    svc.Kill();
    report.Line($"Kill lần {k}: ngay khi đạt {target} file → lúc kill có {killed.TryLong("SELECT COUNT(*) FROM scan_result")} scan_result, " +
        $"integrity_check = {killed.Scalar("PRAGMA integrity_check")}");
}
using (var svc = ServiceProcess.Start(killed.Path))
{
    Wait.Until(() => killed.TryLong("SELECT COUNT(*) FROM scan_result") >= expected, TimeSpan.FromMinutes(15), "quét nốt sau khi kill");
    svc.Kill();
}

foreach (var (name, db) in new[] { ("sạch", clean), ("bị kill", killed) })
{
    report.Check($"[{name}] PRAGMA integrity_check = ok", (string?)db.Scalar("PRAGMA integrity_check") == "ok");
    report.Check($"[{name}] đủ {expected} file_state, {expected} scan_result",
        db.Long("SELECT COUNT(*) FROM file_state") == expected && db.Long("SELECT COUNT(*) FROM scan_result") == expected);
    report.Check($"[{name}] không file provisional (có nhãn) nào thiếu job",
        db.Long("SELECT COUNT(*) FROM scan_result s WHERE s.status='provisional' AND s.labels_json IS NOT NULL AND NOT EXISTS (SELECT 1 FROM llm_job j WHERE j.file_id=s.file_id)") == 0);
    report.Check($"[{name}] không job trùng cho cùng (file, nội dung)",
        db.Long("SELECT COUNT(*) FROM (SELECT 1 FROM llm_job WHERE status<>'stale' GROUP BY file_id, file_hash HAVING COUNT(*) > 1)") == 0);
}

// So từng file: kết quả + finding + job của lần bị kill phải GIỐNG HỆT lần chạy sạch
const string PerFile = """
    SELECT f.path, f.file_hash, s.status, s.needs_review, printf('%.6f', s.s1), IFNULL(s.labels_json, 'NULL'),
      (SELECT IFNULL(group_concat(x, ','), '') FROM (SELECT rule_id || ':' || count AS x FROM finding WHERE file_id = f.file_id ORDER BY rule_id)),
      (SELECT IFNULL(group_concat(y, ','), '') FROM (SELECT priority || ':' || status AS y FROM llm_job WHERE file_id = f.file_id ORDER BY priority))
    FROM file_state f JOIN scan_result s USING (file_id) ORDER BY f.path;
    """;
var cleanRows = clean.Rows(PerFile);
var killedRows = killed.Rows(PerFile);
var diffs = cleanRows.Except(killedRows).Concat(killedRows.Except(cleanRows)).ToList();
report.Check($"Kết quả từng file (trạng thái, nhãn, S1, finding, job) giống hệt lần chạy sạch — {diffs.Count} khác biệt", diffs.Count == 0);
foreach (var d in diffs.Take(5))
{
    report.Line("    khác: " + d);
}
string JobsByPriority(Db db) => string.Join(", ", db.Rows("SELECT 'P' || priority || '=' || COUNT(*) FROM llm_job GROUP BY priority ORDER BY priority"));
report.Check($"Số job theo nhóm: sạch [{JobsByPriority(clean)}] = bị kill [{JobsByPriority(killed)}]", JobsByPriority(clean) == JobsByPriority(killed));

// ================= B. Kill worker lúc job đang chạy =================
report.Section("B. Kill worker lúc job đang chạy (2 worker giả song song, 30% job bị hoàn thành 2 lần)");
long total = killed.Long("SELECT COUNT(*) FROM llm_job");
report.Line($"Hàng đợi ban đầu: {total} job ({JobsByPriority(killed)}), tất cả pending.");
int duplicates = 0;
for (int k = 1; k <= kills; k++)
{
    long target = total * k / (kills + 1);
    var workers = new[] { FakeWorker.Start(killed.Path), FakeWorker.Start(killed.Path) };
    Wait.Until(() => killed.TryLong("SELECT COUNT(*) FROM llm_job WHERE status='done'") >= target, TimeSpan.FromMinutes(5), $"xong {target} job");
    long runningAtKill = killed.Long("SELECT COUNT(*) FROM llm_job WHERE status='running'");
    foreach (var w in workers)
    {
        duplicates += w.KillAndCountDuplicates();
    }
    long stuck = killed.Long("SELECT COUNT(*) FROM llm_job WHERE status='running'");
    report.Line($"Kill lần {k}: khi đạt {target} job done → {runningAtKill} job đang running lúc kill, {stuck} job kẹt running sau kill");

    // Khởi động lại SERVICE THẬT → Worker gọi RecoverRunningJobs lúc khởi động
    using var svc = ServiceProcess.Start(killed.Path);
    Wait.Until(() => killed.TryLong("SELECT COUNT(*) FROM llm_job WHERE status='running'") == 0, TimeSpan.FromMinutes(2), "service khôi phục job running");
    svc.Kill();
}
var finalWorkers = new[] { FakeWorker.Start(killed.Path), FakeWorker.Start(killed.Path) };
foreach (var w in finalWorkers)
{
    duplicates += w.WaitAndCountDuplicates(TimeSpan.FromMinutes(5));
}

report.Check("PRAGMA integrity_check = ok", (string?)killed.Scalar("PRAGMA integrity_check") == "ok");
report.Check($"Không mất job: {killed.Long("SELECT COUNT(*) FROM llm_job WHERE status='done'")}/{total} done, không job nào kẹt ở trạng thái khác",
    killed.Long("SELECT COUNT(*) FROM llm_job") == total && killed.Long("SELECT COUNT(*) FROM llm_job WHERE status<>'done'") == 0);
report.Check($"Mỗi job đúng 1 llm_result ({killed.Long("SELECT COUNT(*) FROM llm_result")} dòng) và kết quả đúng của chính job đó",
    killed.Long("SELECT COUNT(*) FROM llm_result") == total
    && killed.Long("SELECT COUNT(*) FROM llm_result r JOIN llm_job j USING(job_id) WHERE r.json_out = '{\"job\":\"' || j.job_id || '\",\"hash\":\"' || j.file_hash || '\"}'") == total);
report.Check($"llm_cache: 1 mục cho mỗi nội dung khác nhau ({killed.Long("SELECT COUNT(*) FROM llm_cache")} mục)",
    killed.Long("SELECT COUNT(*) FROM llm_cache") == killed.Long("SELECT COUNT(DISTINCT file_hash) FROM llm_job"));
long recovered = killed.Long("SELECT COUNT(*) FROM audit WHERE event='job_recovered'");
long extraPicks = killed.Long("""
    SELECT COUNT(*) FROM (SELECT j.job_id,
      (SELECT COUNT(*) FROM audit a WHERE a.job_id = j.job_id AND a.event = 'job_picked') AS picks,
      (SELECT COUNT(*) FROM audit a WHERE a.job_id = j.job_id AND a.event = 'job_recovered') AS recovers
      FROM llm_job j) WHERE picks <> 1 + recovers;
    """);
report.Check($"Không xử lý lại ngoài khôi phục: mỗi job được lấy đúng 1 + (số lần được khôi phục) lần — {recovered} lần khôi phục, {extraPicks} job lệch", extraPicks == 0);
report.Check($"Có kill giữa lúc job đang chạy (≥ 1 job được khôi phục) — kịch bản thực sự xảy ra", recovered >= 1);
report.Line($"Hoàn thành trùng (CompleteJob gọi 2 lần, mô phỏng at-least-once): {duplicates} lần — vẫn đúng 1 llm_result mỗi job.");

// ================= Kết thúc =================
string resultFile = report.Save();
Console.WriteLine();
Console.WriteLine(report.Failures == 0 ? "== TẤT CẢ PASS ==" : $"== {report.Failures} FAIL ==");
Console.WriteLine($"Báo cáo: {resultFile}");
if (!keep && report.Failures == 0)
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(work, true);
}
else
{
    Console.WriteLine($"DB tạm giữ lại ở: {work}");
}
return report.Failures == 0 ? 0 : 1;

/// <summary>Chạy DlpService.exe (bản copy cạnh tool) với DB riêng; Kill = TerminateProcess (không dọn dẹp).</summary>
internal sealed class ServiceProcess : IDisposable
{
    private readonly Process _process;

    private ServiceProcess(Process process) => _process = process;

    public static ServiceProcess Start(string dbPath)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "DlpService.exe"))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true, // bỏ log console của service (log file vẫn ghi ở <thư mục tool>\logs)
            RedirectStandardError = true
        };
        info.ArgumentList.Add($"--Storage:DbPath={dbPath}");
        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ServiceProcess(process);
    }

    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
        _process.WaitForExit();
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

/// <summary>Worker giả (chưa có worker LLM thật): lấy job → "gọi LLM" (ngủ) → CompleteJob (+ ghi cache). Một phần job gọi
/// CompleteJob 2 lần để mô phỏng giao kết quả trùng (at-least-once). Chạy thành process riêng để kill thật được.</summary>
internal sealed class FakeWorker
{
    private const int DelayMs = 400;
    private const int DuplicatePct = 30;
    private readonly Process _process;
    private int _duplicates;

    private FakeWorker(Process process) => _process = process;

    public static FakeWorker Start(string dbPath)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in new[] { "fake-worker", dbPath, DelayMs.ToString(), DuplicatePct.ToString() })
        {
            info.ArgumentList.Add(a);
        }
        var worker = new FakeWorker(Process.Start(info)!);
        worker._process.OutputDataReceived += (_, e) => { if (e.Data == "DUP") Interlocked.Increment(ref worker._duplicates); };
        worker._process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.Error.WriteLine("  [worker] " + e.Data); };
        worker._process.BeginOutputReadLine();
        worker._process.BeginErrorReadLine();
        return worker;
    }

    public int KillAndCountDuplicates()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
        _process.WaitForExit();
        return _duplicates;
    }

    public int WaitAndCountDuplicates(TimeSpan timeout)
    {
        if (!_process.WaitForExit(timeout))
        {
            throw new TimeoutException("Worker giả không kết thúc");
        }
        _process.WaitForExit(); // đợi đọc hết stdout
        if (_process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Worker giả thoát với mã {_process.ExitCode}");
        }
        return _duplicates;
    }

    /// <summary>Thân của process worker giả.</summary>
    public static int Run(string dbPath, int delayMs, int duplicatePct)
    {
        using var store = new ResultStore(dbPath);
        var random = new Random();
        while (store.TryPickNextJob() is { } job)
        {
            Thread.Sleep(delayMs); // "gọi LLM" — job đang running, kill lúc này = job bị bỏ dở
            var result = new LlmResultRecord($"{{\"job\":\"{job.JobId}\",\"hash\":\"{job.FileHash}\"}}", 0.5, 0.9,
                "fake-model", "fake-prompt", delayMs);
            store.CompleteJob(job.JobId, result, "PACK-" + job.FileHash);
            if (random.Next(100) < duplicatePct)
            {
                store.CompleteJob(job.JobId, result, "PACK-" + job.FileHash);
                Console.WriteLine("DUP");
            }
        }
        return 0;
    }
}

internal static class Wait
{
    public static void Until(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            {
                throw new TimeoutException($"Quá {timeout.TotalMinutes:0} phút chờ: {what}");
            }
            Thread.Sleep(50);
        }
    }
}

/// <summary>In ra console và lưu báo cáo Markdown vào tools/resilience/results/.</summary>
internal sealed class Report
{
    private readonly StringBuilder _md = new();

    public int Failures { get; private set; }

    public void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
        _md.AppendLine().AppendLine("## " + title).AppendLine();
    }

    public void Line(string text)
    {
        Console.WriteLine("   " + text);
        _md.AppendLine("- " + text);
    }

    public void Check(string name, bool ok)
    {
        if (!ok)
        {
            Failures++;
        }
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        _md.AppendLine($"- **{(ok ? "PASS" : "FAIL")}** {name}");
    }

    public string Save()
    {
        // Tìm thư mục tools/resilience đi ngược từ thư mục chạy (bin/Debug/net10.0 → ... → DlpResilience → resilience)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !(dir.Name == "resilience" && dir.Parent?.Name == "tools"))
        {
            dir = dir.Parent;
        }
        string folder = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"resilience-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(file, $"# Test chịu lỗi — {DateTime.Now:yyyy-MM-dd HH:mm}\n\nKết quả: " +
            (Failures == 0 ? "**TẤT CẢ PASS**" : $"**{Failures} FAIL**") + "\n" + _md);
        return file;
    }
}
