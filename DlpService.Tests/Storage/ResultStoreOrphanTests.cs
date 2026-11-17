// [Thay đổi] File mới (M5 đường #4): bản ghi nằm ngoài mọi thư mục theo dõi → đánh dấu (file_state.orphan_since_utc), quá ân hạn mới xoá.
// Dùng ĐỒNG HỒ GIẢ (tham số now của SweepOrphans) để kiểm mốc 24 giờ mà không phải chờ thật.
using DlpService.Storage;
using Microsoft.Data.Sqlite;

namespace DlpService.Tests.Storage;

public sealed class ResultStoreOrphanTests : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);

    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-orphan-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly ResultStore _store;
    private string A => Path.Combine(_work, "goc-a");
    private string B => Path.Combine(_work, "goc-b");

    public ResultStoreOrphanTests()
    {
        Directory.CreateDirectory(_work);
        _dbPath = Path.Combine(_work, "dlp.db");
        _store = new ResultStore(_dbPath);
        foreach (var p in new[] { Path.Combine(A, "1.txt"), Path.Combine(B, "2.txt"), Path.Combine(B, "con", "3.txt") })
        {
            _store.SaveScan(new ScanRecord(p, 1, DateTime.UtcNow, "h", 0.9, ResultStore.StatusFinal, false, "{}",
                [new FindingRecord("cccd", 0.9, true, true, 1, "offset:1")]));
        }
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private bool Has(string p) => _store.TryGetFileState(p) != null;

    private long Count(string table)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Lan_dau_chi_danh_dau_truoc_han_khong_xoa_qua_han_moi_xoa_ca_bang_con()
    {
        Assert.Equal(0, _store.SweepOrphans([A], Grace, T0));                      // lần 1: chỉ đánh dấu 2 bản ghi của B
        Assert.Equal(0, _store.SweepOrphans([A], Grace, T0.AddHours(23)));         // chưa đủ 24 giờ
        Assert.True(Has(Path.Combine(B, "2.txt")));

        Assert.Equal(2, _store.SweepOrphans([A], Grace, T0.AddHours(24)));         // đủ hạn → xoá

        Assert.False(Has(Path.Combine(B, "2.txt")));
        Assert.False(Has(Path.Combine(B, "con", "3.txt")));
        Assert.True(Has(Path.Combine(A, "1.txt")));                                // bản ghi trong root không bị đụng
        Assert.Equal(1, Count("finding"));                                          // finding của B cũng bị xoá
        Assert.Equal(1, Count("scan_result"));
    }

    [Fact]
    public void Root_duoc_them_lai_thi_bo_danh_dau_va_dem_lai_tu_dau()
    {
        _store.SweepOrphans([A], Grace, T0);                                        // B bị đánh dấu ở T0
        _store.SweepOrphans([A, B], Grace, T0.AddHours(2));                         // B được thêm lại → bỏ dấu
        Assert.Equal(0, _store.SweepOrphans([A], Grace, T0.AddHours(30)));         // B lại mồ côi → dấu MỚI (T0+30h), chưa xoá

        Assert.True(Has(Path.Combine(B, "2.txt")));
        Assert.Equal(2, _store.SweepOrphans([A], Grace, T0.AddHours(54)));         // 24 giờ sau dấu mới → xoá
    }

    [Fact]
    public void Danh_sach_root_rong_thi_khong_lam_gi()
    {
        Assert.Equal(0, _store.SweepOrphans([], Grace, T0));
        Assert.Equal(0, _store.SweepOrphans([], Grace, T0.AddDays(30)));

        Assert.Equal(3, Count("file_state")); // chốt an toàn: cấu hình rỗng không được xoá cả DB
    }

    [Fact]
    public void Root_co_tien_to_giong_khong_bi_nham()
    {
        string similar = Path.Combine(_work, "goc-a-khac", "4.txt"); // "goc-a" là tiền tố của "goc-a-khac"
        _store.SaveScan(new ScanRecord(similar, 1, DateTime.UtcNow, "h", 0, ResultStore.StatusFinal, false, "{}", []));

        _store.SweepOrphans([A, B], Grace, T0);
        _store.SweepOrphans([A, B], Grace, T0.AddHours(25));

        Assert.False(Has(similar));                   // goc-a-khac KHÔNG thuộc goc-a → mồ côi → bị xoá
        Assert.True(Has(Path.Combine(A, "1.txt")));
    }

    [Fact]
    public void Mo_DB_cu_khong_co_cot_orphan_since_thi_tu_them()
    {
        string oldDb = Path.Combine(_work, "old.db");
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = oldDb }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE file_state (file_id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, size INTEGER NOT NULL,
                  mtime_utc INTEGER NOT NULL, file_hash TEXT, last_scan_utc INTEGER);
                INSERT INTO file_state (path, size, mtime_utc) VALUES ($p, 1, 1);
                """;
            cmd.Parameters.AddWithValue("$p", Path.Combine(_work, "ngoai", "x.txt"));
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new ResultStore(oldDb);

        string root = Path.Combine(_work, "goc");
        Assert.Equal(0, store.SweepOrphans([root], Grace, T0));
        Assert.Equal(1, store.SweepOrphans([root], Grace, T0.AddHours(24)));
    }
}
