// [Thay đổi] File mới: kiểm tra ResultStore.GetExplain/GetTrace gộp đúng dữ liệu từ scan_result + finding + llm_job +
// llm_result (dùng để chẩn đoán qua lệnh "explain <file_id>"). Dùng file SQLite tạm trong TestContext.GetTempPath,
// xoá sau mỗi test (IDisposable) — không đụng DB thật của service.
using DlpService.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DlpService.Tests.Storage;

public sealed class ResultStoreExplainTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dlp-explain-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // nhả handle file trước khi xoá, tránh lỗi "file đang bị dùng" trên Windows
        try { File.Delete(_dbPath); } catch (IOException) { /* best-effort dọn dẹp test */ }
    }

    // Test-only: ResultStore không có API công khai trả file_id (không cần cho luồng chính), nên tự mở 1 kết nối
    // riêng đọc lại — an toàn vì SaveScan đã COMMIT xong trước khi hàm này được gọi.
    private long ReadFileId(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_id FROM file_state WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void GetExplain_FileChuaTungQuet_TraVeNull()
    {
        using var store = new ResultStore(_dbPath);

        Assert.Null(store.GetExplain(999));
    }

    [Fact]
    public void GetExplain_SauSaveScan_GomDuScanResultVaFinding_ChuaCoJob()
    {
        using var store = new ResultStore(_dbPath);
        const string path = @"C:\watch\vip.docx";
        var findings = new[] { new FindingRecord("vn_id", 0.9, true, true, 1, "offset:10") };

        store.SaveScan(new ScanRecord(path, 1234, DateTime.UtcNow, "hash-1", 0.9, ResultStore.StatusProvisional,
            true, "[\"PiiNationalId\"]", findings, LlmPriority: null, RulesVer: "rv1", MergePolicyVer: "mv1"));

        long fileId = ReadFileId(path);
        var explain = store.GetExplain(fileId);

        Assert.NotNull(explain);
        Assert.Equal(path, explain!.Path);
        Assert.Equal(0.9, explain.S1);
        Assert.Equal(0.9, explain.SFinal); // Pass 1, chưa có LLM merge
        Assert.Equal("regex", explain.Source);
        Assert.True(explain.NeedsReview);
        Assert.Equal("rv1", explain.RulesVer);
        Assert.Equal("mv1", explain.MergePolicyVer);
        Assert.Single(explain.Findings);
        Assert.Equal("vn_id", explain.Findings[0].RuleId);
        Assert.True(explain.Findings[0].HardRule);
        Assert.Null(explain.JobId); // chưa tạo job LLM (llmPriority = null)
    }

    [Fact]
    public void GetExplain_SauKhiMergeXongLlm_GomDuCa8Truong()
    {
        using var store = new ResultStore(_dbPath);
        const string path = @"C:\watch\grey.docx";
        var findings = new[] { new FindingRecord("phone", 0.6, null, false, 3, "offset:5") };

        store.SaveScan(new ScanRecord(path, 500, DateTime.UtcNow, "hash-2", 0.6, ResultStore.StatusProvisional,
            true, "[\"PiiPhone\"]", findings, LlmPriority: LlmJobPriority.GreyZone, RulesVer: "rv1", MergePolicyVer: "mv1"));

        long fileId = ReadFileId(path);
        var job = store.TryPickNextJob();
        Assert.NotNull(job);

        store.CompleteJob(job!.JobId, new LlmResultRecord("{\"labels\":[]}", 0.75, 0.9, "phi-4", "p1", 1200),
            merge: new ScanMergeUpdate(SFinal: 0.75, NeedsReview: false, Source: "llm", MergePolicyVer: "mv2"),
            fileId: job.FileId, fileHash: job.FileHash);

        var explain = store.GetExplain(fileId);

        Assert.NotNull(explain);
        Assert.Equal(0.6, explain!.S1);
        Assert.Equal(0.75, explain.SFinal);
        Assert.Equal("llm", explain.Source);
        Assert.Equal("mv2", explain.MergePolicyVer); // ghi đè lúc merge, khác lúc Pass 1 (mv1)
        Assert.Equal("rv1", explain.RulesVer); // rules_ver không đổi lúc merge (chỉ đổi lúc quét lại)
        Assert.Equal(job.JobId, explain.JobId);
        Assert.Equal(LlmJobStatus.Done, explain.JobStatus);
        Assert.Equal("phi-4", explain.ModelVer);
        Assert.Equal("p1", explain.PromptVer);
        Assert.Equal(0.75, explain.S2);
    }

    [Fact]
    public void GetTrace_KhopVoiGetExplain_TraVeDungCacTruongDaLuu()
    {
        using var store = new ResultStore(_dbPath);
        const string path = @"C:\watch\trace.docx";
        var findings = new[] { new FindingRecord("email", 0.4, null, false, 1, "") };

        store.SaveScan(new ScanRecord(path, 10, DateTime.UtcNow, "hash-3", 0.4, ResultStore.StatusProvisional,
            false, null, findings, RulesVer: "rv9", MergePolicyVer: "mv9"));

        var trace = store.GetTrace(path);

        Assert.NotNull(trace);
        Assert.Equal(0.4, trace!.S1);
        Assert.Equal(0.4, trace.SFinal);
        Assert.Equal("regex", trace.Source);
        Assert.Equal("rv9", trace.RulesVer);
        Assert.Equal("mv9", trace.MergePolicyVer);
        Assert.Null(trace.S2); // chưa có LLM
    }
}
