// [Thay đổi] File mới: kiểm tra ResultStore.FailJobPermanently (LLM báo OUTPUT_TOO_LONG = lỗi vĩnh viễn của 1 file):
// job chuyển failed NGAY (không qua pending/backoff), scan_result chốt final nhưng GIỮ needs_review và điểm regex.
// Dùng file SQLite tạm, xoá sau mỗi test — không đụng DB thật của service.
using DlpService.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DlpService.Tests.Storage;

public sealed class ResultStoreFailJobPermanentlyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dlp-failperm-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* best-effort dọn dẹp test */ }
    }

    // Tạo 1 file vùng xám (provisional + needs_review) kèm job P1 pending, lấy job ra (running). Trả về job.
    private LlmJob PickGreyJob(ResultStore store, string path)
    {
        store.SaveScan(new ScanRecord(path, 100, DateTime.UtcNow, "hash-x", 0.5, ResultStore.StatusProvisional,
            true, "[\"PiiBasic\"]", [new FindingRecord("vn_name", 0.5, null, false, 1, "offset:1")], LlmPriority: LlmJobPriority.GreyZone));
        var job = store.TryPickNextJob();
        Assert.NotNull(job);
        return job!;
    }

    private (string Status, bool NeedsReview, double SFinal) ReadScan(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.status, s.needs_review, s.s_final FROM scan_result s JOIN file_state f ON f.file_id = s.file_id WHERE f.path = $p;
            """;
        cmd.Parameters.AddWithValue("$p", path);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (r.GetString(0), r.GetInt64(1) == 1, r.GetDouble(2));
    }

    [Fact]
    public void FailJobPermanently_JobDangRunning_ChuyenFailedNgayVaChotFileFinal()
    {
        using var store = new ResultStore(_dbPath);
        const string path = @"C:\watch\xam.docx";
        var job = PickGreyJob(store, path);

        Assert.True(store.FailJobPermanently(job.JobId, "OUTPUT_TOO_LONG"));

        var counts = store.CountJobsByStatus();
        Assert.Equal(1, counts.GetValueOrDefault(LlmJobStatus.Failed));
        Assert.Equal(0, counts.GetValueOrDefault(LlmJobStatus.Pending)); // KHÔNG quay lại pending để thử lại

        var scan = ReadScan(path);
        Assert.Equal(ResultStore.StatusFinal, scan.Status);
        Assert.True(scan.NeedsReview);        // giữ cờ cần xem lại như Pass 1 đã đặt
        Assert.Equal(0.5, scan.SFinal);       // giữ điểm regex
    }

    [Fact]
    public void FailJobPermanently_JobKhongConRunning_TraFalseVaKhongDoiGi()
    {
        using var store = new ResultStore(_dbPath);
        var job = PickGreyJob(store, @"C:\watch\xam2.docx");
        Assert.True(store.FailJobPermanently(job.JobId, "OUTPUT_TOO_LONG"));

        // Gọi lần 2: job đã failed, không còn running -> không hợp lệ
        Assert.False(store.FailJobPermanently(job.JobId, "OUTPUT_TOO_LONG"));
        Assert.Equal(1, store.CountJobsByStatus().GetValueOrDefault(LlmJobStatus.Failed));
    }

    [Fact]
    public void FailJob_LoiTam_VanThuLaiNhuCu_KhongBiAnhHuong()
    {
        using var store = new ResultStore(_dbPath);
        var job = PickGreyJob(store, @"C:\watch\xam3.docx");

        Assert.True(store.FailJob(job.JobId, "MODEL_ERROR"));

        var counts = store.CountJobsByStatus();
        Assert.Equal(1, counts.GetValueOrDefault(LlmJobStatus.Pending)); // lỗi tạm: về pending chờ backoff
        Assert.Equal(0, counts.GetValueOrDefault(LlmJobStatus.Failed));
    }
}
