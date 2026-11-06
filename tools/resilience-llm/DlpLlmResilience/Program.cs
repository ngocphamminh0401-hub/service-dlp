// [Thay đổi] File mới (mục 5.3 + test chịu lỗi): xác nhận Worker (LlmJobDispatcher) không treo/sập khi:
//   1. LLM chạy SAI contract_ver -> phải chuyển "regex-only", KHÔNG gửi request nào (mục 5.3).
//   2. LLM bị GIẾT giữa chừng lúc đang xử lý (TerminateProcess, không kịp dọn dẹp).
//   3. LLM TREO (không tự báo timeout như bình thường) -> phải bị chặn bởi deadline PHÍA CLIENT.
//   4. LLM trả lời kiểu "RÁC" (đúng khuôn dạng proto, giá trị vô lý) -> không được làm sập Worker.
//
// Cách chạy: dotnet run --project tools/resilience-llm/DlpLlmResilience
using System.Diagnostics;
using DlpService.Llm;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

string work = Path.Combine(Path.GetTempPath(), "dlp-llm-resilience");
if (Directory.Exists(work))
{
    Directory.Delete(work, true);
}
Directory.CreateDirectory(work);

var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
var scanner = new RegexScanner(patterns);
var reader = new FileReaderService();
var report = new ResilienceReport();

// ================= 1) Contract_ver lệch =================
report.Section("1. LLM chạy sai contract_ver -> chuyển regex-only, không gửi request");
{
    using var db = new TestDb(work, "contract-mismatch");
    using var server = FakeLlmProcess.Start(contractVer: 99); // LLM giả tự nhận contract_ver = 99 (sai)
    await WaitReadyAsync(server.PipeName);
    string jobId = db.CreateGreyJob(reader, "file1.txt", "So CCCD: 001199000001");

    var dispatcher = BuildDispatcher(db.Store, reader, scanner, server.PipeName);

    string result = await dispatcher.TryDispatchOnceAsync().WaitAsync(TimeSpan.FromSeconds(15));
    report.Check("Kết quả trả về là contract_mismatch", result.StartsWith("contract_mismatch", StringComparison.Ordinal), $"result={result}");
    report.Check("Job KHÔNG bị đụng vào (vẫn pending, chưa gửi request nào)", db.GetJobStatus(jobId) == "pending", $"status={db.GetJobStatus(jobId)}");
}

// ================= 2) Kill LLM giữa chừng =================
report.Section("2. LLM bị giết (TerminateProcess) giữa chừng lúc đang xử lý");
{
    using var db = new TestDb(work, "kill-midflight");
    using var server = FakeLlmProcess.Start(contractVer: 1);
    await WaitReadyAsync(server.PipeName);
    string jobId = db.CreateGreyJob(reader, "file2.txt", "So CCCD: 001199000002", forceJobIdPrefix: "HANG-");

    var dispatcher = BuildDispatcher(db.Store, reader, scanner, server.PipeName);

    var dispatchTask = dispatcher.TryDispatchOnceAsync();
    await Task.Delay(700); // để request kịp tới server và bắt đầu "treo" (HANG-)
    server.Kill();

    string result = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(15));
    report.Check("Không ném exception, tự trả về kết quả (không phải throw ra ngoài)", true, $"result={result}");
    report.Check("Job không bị kẹt ở 'running' (được đưa về pending/failed để thử lại)", db.GetJobStatus(jobId) != "running", $"status={db.GetJobStatus(jobId)}");
}

// ================= 3) LLM treo (không tự báo timeout) — deadline PHÍA CLIENT phải chặn =================
report.Section("3. LLM treo (không trả lời) -> deadline phía client phải tự cắt, không đợi mãi");
{
    using var db = new TestDb(work, "hang");
    using var server = FakeLlmProcess.Start(contractVer: 1);
    await WaitReadyAsync(server.PipeName);
    string jobId = db.CreateGreyJob(reader, "file3.txt", "So CCCD: 001199000003", forceJobIdPrefix: "HANG-");

    var dispatcher = BuildDispatcher(db.Store, reader, scanner, server.PipeName, jobDeadlineSeconds: 1); // deadline ngắn để test nhanh

    var sw = Stopwatch.StartNew();
    string result = await dispatcher.TryDispatchOnceAsync().WaitAsync(TimeSpan.FromSeconds(15));
    report.Check("Tự cắt trong khoảng thời gian hợp lý (< 10s dù LLM treo vô hạn)", sw.Elapsed < TimeSpan.FromSeconds(10),
        $"mất {sw.Elapsed.TotalSeconds:0.0}s, result={result}");
    report.Check("Job được xử lý như TIMEOUT (không phải no_response)", result == "done:timeout", $"result={result}");
    report.Check("Job không bị kẹt ở 'running'", db.GetJobStatus(jobId) != "running", $"status={db.GetJobStatus(jobId)}");
}

// ================= 4) Phản hồi "rác" (đúng khuôn dạng, giá trị vô lý) =================
report.Section("4. LLM trả lời kiểu rác (Status=OK nhưng confidence âm, model_ver rỗng, nhãn ngoài tập đóng)");
{
    using var db = new TestDb(work, "garbage");
    using var server = FakeLlmProcess.Start(contractVer: 1);
    await WaitReadyAsync(server.PipeName);
    string jobId = db.CreateGreyJob(reader, "file4.txt", "So CCCD: 001199000004", forceJobIdPrefix: "GARBAGE-");

    var dispatcher = BuildDispatcher(db.Store, reader, scanner, server.PipeName);

    string result = await dispatcher.TryDispatchOnceAsync().WaitAsync(TimeSpan.FromSeconds(15));
    report.Check("Không ném exception khi nhận dữ liệu vô lý", true, $"result={result}");
    report.Check("Vẫn hoàn tất job (done) thay vì kẹt lại", result == "done:ok", $"result={result}");
    report.Check("Job chuyển sang 'done' trong DB", db.GetJobStatus(jobId) == "done", $"status={db.GetJobStatus(jobId)}");
}

string resultFile = report.Save();
Console.WriteLine();
Console.WriteLine(report.Failures == 0 ? "== TẤT CẢ PASS ==" : $"== {report.Failures} FAIL ==");
Console.WriteLine($"Báo cáo: {resultFile}");
Directory.Delete(work, true);
return report.Failures == 0 ? 0 : 1;

static LlmJobDispatcher BuildDispatcher(ResultStore store, FileReaderService reader, RegexScanner scanner, string pipeName, int jobDeadlineSeconds = 5)
{
    var inferenceClient = InferenceClient.ConnectNamedPipe(pipeName);
    var breaker = new CircuitBreaker();
    var guarded = new GuardedInferenceClient(inferenceClient, breaker);
    var idle = new MachineIdleChecker(new MachineIdleOptions { MaxCpuPercent = 100 }); // ép "rảnh" cho test
    var options = new LlmJobDispatcherOptions { TokenBudget = 2000, JobDeadline = TimeSpan.FromSeconds(jobDeadlineSeconds) };
    return new LlmJobDispatcher(store, reader, scanner, guarded, idle, options, NullLogger.Instance);
}

// [Thay đổi] Chờ LLM giả khởi động XONG bằng 1 client RIÊNG (không phải dispatcher) — dispatcher sẽ LẤY JOB thật ngay
// khi gọi, nếu dùng nó để "chờ sẵn sàng" sẽ vô tình tiêu thụ mất job của bài test.
static async Task WaitReadyAsync(string pipeName)
{
    using var client = InferenceClient.ConnectNamedPipe(pipeName);
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(30))
    {
        if (await client.TryGetInfoAsync(TimeSpan.FromSeconds(2)) != null)
        {
            return;
        }
        await Task.Delay(300);
    }
    throw new TimeoutException($"LLM giả (pipe {pipeName}) không sẵn sàng sau 30s");
}

/// <summary>1 DB SQLite tạm + tiện ích tạo job vùng xám trực tiếp (bỏ qua toàn bộ pipeline quét/eligibility thật —
/// bài test này chỉ quan tâm hành vi của LlmJobDispatcher khi LLM có vấn đề, không test lại Scanning/EscalationPolicy).</summary>
internal sealed class TestDb : IDisposable
{
    private readonly string _dbPath;
    public ResultStore Store { get; }

    public TestDb(string workDir, string name)
    {
        _dbPath = Path.Combine(workDir, name + ".db");
        Store = new ResultStore(_dbPath);
    }

    /// <summary>Tạo 1 file thật + 1 job P1 (vùng xám) trỏ tới file đó. Trả về job_id thật sự sẽ được xử lý.</summary>
    public string CreateGreyJob(FileReaderService reader, string fileName, string content, string? forceJobIdPrefix = null)
    {
        string filePath = Path.Combine(Path.GetDirectoryName(_dbPath)!, fileName);
        File.WriteAllText(filePath, content);

        var extractor = reader.FindExtractor(filePath)!;
        string flatText = string.Join("\n", extractor.Extract(filePath).Select(b => b.Text));
        string hash = ContentHash.Compute(flatText);
        var info = new FileInfo(filePath);
        var findings = new List<FindingRecord> { new("vn_id_001", 0.85, true, true, 1, "offset:9") };

        Store.SaveScan(new ScanRecord(filePath, info.Length, info.LastWriteTimeUtc, hash, 0.5,
            ResultStore.StatusProvisional, true, null, findings, LlmPriority: 1));

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        string jobId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT job_id FROM llm_job WHERE status = 'pending' ORDER BY created_utc DESC LIMIT 1;";
            jobId = (string)cmd.ExecuteScalar()!;
        }

        // [Thay đổi] Chỉ để TEST: đổi job_id thành có tiền tố quy ước của LLM giả (HANG-/GARBAGE-...) — job_id thật
        // do ResultStore tự sinh (Guid), không có cách nào truyền tiền tố qua API công khai.
        if (forceJobIdPrefix != null)
        {
            string newJobId = forceJobIdPrefix + jobId;
            using var update = conn.CreateCommand();
            update.CommandText = "UPDATE llm_job SET job_id = $new WHERE job_id = $old;";
            update.Parameters.AddWithValue("$new", newJobId);
            update.Parameters.AddWithValue("$old", jobId);
            update.ExecuteNonQuery();
            jobId = newJobId;
        }

        return jobId;
    }

    public string? GetJobStatus(string jobId)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM llm_job WHERE job_id = $id;";
        cmd.Parameters.AddWithValue("$id", jobId);
        return cmd.ExecuteScalar() as string;
    }

    public void Dispose()
    {
        Store.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>Chạy LLM giả (tools/fake-llm) làm process con thật — kill được như 1 tiến trình thật (TerminateProcess).</summary>
internal sealed class FakeLlmProcess : IDisposable
{
    private readonly Process _process;
    public string PipeName { get; }

    private FakeLlmProcess(Process process, string pipeName)
    {
        _process = process;
        PipeName = pipeName;
    }

    public static FakeLlmProcess Start(uint contractVer)
    {
        string pipeName = "dlp-resilience-" + Guid.NewGuid().ToString("N")[..8];
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "run", "--project", @"D:\Service-DLP\tools\fake-llm\DlpFakeLlm", "--", "serve", "--pipe", pipeName, "--contract-ver", contractVer.ToString() })
        {
            psi.ArgumentList.Add(a);
        }
        var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new FakeLlmProcess(process, pipeName);
    }

    /// <summary>Giết THẬT (TerminateProcess), không cho dọn dẹp — mô phỏng crash/kill giữa chừng.</summary>
    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose()
    {
        Kill();
        _process.WaitForExit();
        _process.Dispose();
    }
}

/// <summary>In PASS/FAIL và lưu báo cáo Markdown vào tools/resilience-llm/results/.</summary>
internal sealed class ResilienceReport
{
    private readonly System.Text.StringBuilder _md = new();
    public int Failures { get; private set; }

    public void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
        _md.AppendLine().AppendLine("## " + title).AppendLine();
    }

    public void Check(string name, bool ok, string detail)
    {
        if (!ok)
        {
            Failures++;
        }
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} — {detail}");
        _md.AppendLine($"- **{(ok ? "PASS" : "FAIL")}** {name} — {detail}");
    }

    public string Save()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !(dir.Name == "resilience-llm" && dir.Parent?.Name == "tools"))
        {
            dir = dir.Parent;
        }
        string folder = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"resilience-llm-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(file, $"# Test chịu lỗi LLM — {DateTime.Now:yyyy-MM-dd HH:mm}\n\nKết quả: " +
            (Failures == 0 ? "**TẤT CẢ PASS**" : $"**{Failures} FAIL**") + "\n" + _md);
        return file;
    }
}
