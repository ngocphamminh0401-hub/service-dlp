// [Thay đổi] File mới (GĐ3 bước 3.1, R4): xoá bản ghi theo đường dẫn — đúng file, cả cây con khi là thư mục, KHÔNG xoá nhầm đường dẫn chỉ
// trùng tiền tố ("a" không được xoá "ab.txt"), xoá cả job/kết quả LLM đi kèm, và liệt kê đường dẫn cho quét đối soát.
using DlpService.Storage;
using Microsoft.Data.Sqlite;

namespace DlpService.Tests.Storage;

public sealed class ResultStoreRemovalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-store-rm-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_folder, "test.db");
    private static readonly char S = Path.DirectorySeparatorChar;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static ScanRecord Record(string path, int? priority = null) => new(
        path, 100, DateTime.UtcNow, "hash-" + path, 0.5, priority == null ? ResultStore.StatusFinal : ResultStore.StatusProvisional,
        priority != null, "[\"PiiBasic\"]", [new FindingRecord("vn_name", 0.5, null, false, 1, "offset:1")], LlmPriority: priority);

    private static string P(params string[] parts) => S + string.Join(S, parts);

    private long Count(string table)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Xoa_file_chi_xoa_dung_file_do()
    {
        using var store = new ResultStore(DbPath);
        store.SaveScan(Record(P("home", "a", "x.txt")));
        store.SaveScan(Record(P("home", "a", "y.txt")));

        Assert.Equal(1, store.RemovePath(P("home", "a", "x.txt")));

        Assert.Null(store.TryGetFileState(P("home", "a", "x.txt")));
        Assert.NotNull(store.TryGetFileState(P("home", "a", "y.txt")));
    }

    [Fact]
    public void Xoa_thu_muc_xoa_ca_cay_con_nhung_khong_xoa_duong_dan_trung_tien_to()
    {
        using var store = new ResultStore(DbPath);
        store.SaveScan(Record(P("home", "a", "x.txt")));
        store.SaveScan(Record(P("home", "a", "con", "chau", "y.txt")));
        store.SaveScan(Record(P("home", "ab.txt")));       // "a" là tiền tố của "ab" nhưng KHÔNG nằm trong thư mục a
        store.SaveScan(Record(P("home", "a-khac", "z.txt")));

        Assert.Equal(2, store.RemovePath(P("home", "a")));

        Assert.NotNull(store.TryGetFileState(P("home", "ab.txt")));
        Assert.NotNull(store.TryGetFileState(P("home", "a-khac", "z.txt")));
        Assert.Equal(2, Count("file_state"));
        Assert.Equal(2, Count("scan_result"));
        Assert.Equal(2, Count("finding"));
    }

    [Fact]
    public void Xoa_ca_job_LLM_cua_file()
    {
        using var store = new ResultStore(DbPath);
        store.SaveScan(Record(P("home", "a", "xam.txt"), priority: 1)); // vùng xám → có llm_job
        Assert.Equal(1, Count("llm_job"));

        Assert.Equal(1, store.RemovePath(P("home", "a", "xam.txt")));

        Assert.Equal(0, Count("llm_job"));
        Assert.Equal(0, Count("file_state"));
    }

    [Fact]
    public void Duong_dan_khong_co_ban_ghi_tra_0_va_khong_ghi_audit()
    {
        using var store = new ResultStore(DbPath);
        store.SaveScan(Record(P("home", "a", "x.txt")));
        long auditBefore = Count("audit");

        Assert.Equal(0, store.RemovePath(P("home", "a", ".x.txt.swp")));

        Assert.Equal(auditBefore, Count("audit"));
    }

    [Fact]
    public void Xoa_co_ghi_audit_chi_so_luong_khong_co_duong_dan()
    {
        using var store = new ResultStore(DbPath);
        store.SaveScan(Record(P("home", "a", "bi-mat.txt")));
        store.RemovePath(P("home", "a"));

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT detail FROM audit WHERE event = 'file_removed';";
        string detail = (string)cmd.ExecuteScalar()!;
        Assert.Equal("1 file", detail);
        Assert.DoesNotContain("bi-mat", detail);
    }

    [Fact]
    public void Liet_ke_duong_dan_duoi_thu_muc()
    {
        using var store = new ResultStore(DbPath);
        store.SaveScan(Record(P("home", "a", "x.txt")));
        store.SaveScan(Record(P("home", "a", "con", "y.txt")));
        store.SaveScan(Record(P("home", "ab.txt")));

        var paths = store.ListPathsUnder(P("home", "a"));

        Assert.Equal([P("home", "a", "con", "y.txt"), P("home", "a", "x.txt")], paths.Order(StringComparer.Ordinal));
    }
}
