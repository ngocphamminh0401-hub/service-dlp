// [Thay đổi] File mới (giảm thời gian ghi DB): kiểm tra 3 thay đổi của ResultStore — index mới cho llm_job, kết nối CHỈ ĐỌC
// riêng cho tra cache (đọc ngay sau ghi vẫn thấy dữ liệu mới), checkpoint WAL chạy nền — và an toàn khi nhiều luồng cùng dùng.
// Dùng thư mục tạm riêng (ResultStore khoá ACL thư mục chứa DB), xoá sau mỗi test.
using DlpService.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DlpService.Tests.Storage;

public sealed class ResultStoreConcurrencyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-store-test-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_folder, "test.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static ScanRecord Record(string path, string hash, int? priority = null) => new(
        path, 100, DateTime.UtcNow, hash, 0.5, priority == null ? ResultStore.StatusFinal : ResultStore.StatusProvisional,
        priority != null, "[\"PiiBasic\"]", [new FindingRecord("vn_name", 0.5, null, false, 1, "offset:1")], LlmPriority: priority);

    [Fact]
    public void TraCacheNgaySauKhiGhi_ThayDuLieuMoi()
    {
        using var store = new ResultStore(DbPath);
        Assert.Null(store.TryGetFileState(@"C:\a.docx"));

        store.SaveScan(Record(@"C:\a.docx", "hash-1"));
        var state = store.TryGetFileState(@"C:\a.docx");
        Assert.NotNull(state);
        Assert.Equal("hash-1", state!.FileHash);
        Assert.True(state.HasConclusion);

        store.SaveScan(Record(@"C:\a.docx", "hash-2")); // quét lại → kết nối đọc thấy bản mới ngay
        Assert.Equal("hash-2", store.TryGetFileState(@"C:\a.docx")!.FileHash);
    }

    [Fact]
    public void MoDb_CoDuIndexMoiChoLlmJob()
    {
        new ResultStore(DbPath).Dispose();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'llm_job';";
        var names = new List<string>();
        using (var r = cmd.ExecuteReader()) { while (r.Read()) names.Add(r.GetString(0)); }
        Assert.Contains("ix_job_file", names);
        Assert.Contains("ix_job_prio_created", names);
        Assert.Contains("ix_job_prio_next", names);
    }

    [Fact]
    public void NhieuLuongGhiVaDocCungLuc_KhongLoi_KhongMatBanGhi()
    {
        using var store = new ResultStore(DbPath);
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            string path = $@"C:\f{i}.pdf";
            store.TryGetFileState(path);
            store.SaveScan(Record(path, $"h{i}", i % 10 == 0 ? LlmJobPriority.GreyZone : null));
            Assert.NotNull(store.TryGetFileState(path));
        });
        Assert.All(Enumerable.Range(0, 400), i => Assert.Equal($"h{i}", store.TryGetFileState($@"C:\f{i}.pdf")!.FileHash));
    }

    [Fact]
    public void CheckpointNen_DonWalVaoFileDb()
    {
        using var store = new ResultStore(DbPath);
        for (int i = 0; i < 2_000; i++)
        {
            store.SaveScan(Record($@"C:\g{i}.docx", $"h{i}"));
        }

        // Sau vài chu kỳ checkpoint (1 giây/lần), dữ liệu đã được chép từ WAL vào file DB chính
        Thread.Sleep(2_500);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);"; // trả (busy, số trang WAL, số trang đã checkpoint)
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        long walPages = r.GetInt64(1), checkpointed = r.GetInt64(2);
        Assert.Equal(walPages, checkpointed); // không còn trang nào chưa được chép vào DB
        Assert.True(new FileInfo(DbPath).Length > 100_000, "file DB chính phải chứa dữ liệu đã checkpoint");
    }

    [Fact]
    public void DisposeHaiLan_KhongLoi_SauDisposeKhongNemLoi()
    {
        var store = new ResultStore(DbPath);
        store.Dispose();
        store.Dispose();
        Assert.Null(store.TryGetFileState(@"C:\x.docx"));
        Assert.Equal(JobEnqueueOutcome.None, store.SaveScan(Record(@"C:\x.docx", "h")));
    }
}
