// [Thay đổi] File mới (M1 — fail-closed khi không khoá được thư mục DB): requireProtectedFolder = true thì MỌI trường hợp không khoá được đều ném
// DataFolderProtectionException và KHÔNG tạo file DB; false (test/tool) giữ hành vi cũ (chỉ cảnh báo). Dùng guard giả để tạo từng tình huống.
using DlpService.Platform;
using DlpService.Storage;
using Microsoft.Data.Sqlite;

namespace DlpService.Tests.Storage;

public sealed class ResultStoreProtectionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-protect-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_folder, "dlp.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // Guard giả: có khoá thật (Enforces) nhưng Restrict luôn lỗi — như khi tài khoản chạy service không phải chủ thư mục
    private sealed class FailingGuard : IDataFolderGuard
    {
        public void Restrict(string folder) => throw new UnauthorizedAccessException("giả lập: không đủ quyền");
        public string Description => "guard giả (luôn lỗi)";
    }

    // Guard giả: khoá thành công
    private sealed class OkGuard : IDataFolderGuard
    {
        public int Calls;
        public void Restrict(string folder) => Calls++;
        public string Description => "guard giả (luôn được)";
    }

    public static TheoryData<string> UnprotectedCases => new() { "null", "noop", "failing" };

    private static IDataFolderGuard? Guard(string kind) => kind switch
    {
        "noop" => new NoOpDataFolderGuard(),
        "failing" => new FailingGuard(),
        _ => null
    };

    [Theory]
    [MemberData(nameof(UnprotectedCases))]
    public void Bat_buoc_bao_ve_ma_khong_khoa_duoc_thi_nem_loi_va_khong_tao_DB(string kind)
    {
        var ex = Assert.Throws<DataFolderProtectionException>(() =>
            new ResultStore(DbPath, guard: Guard(kind), requireProtectedFolder: true));

        Assert.Equal(_folder, ex.Folder);
        Assert.False(File.Exists(DbPath), "không được tạo file DB trong thư mục chưa khoá");
    }

    [Theory]
    [MemberData(nameof(UnprotectedCases))]
    public void Khong_bat_buoc_thi_giu_hanh_vi_cu_mo_duoc_DB(string kind)
    {
        using var store = new ResultStore(DbPath, guard: Guard(kind));

        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public void Bat_buoc_bao_ve_va_khoa_duoc_thi_mo_binh_thuong()
    {
        var guard = new OkGuard();

        using var store = new ResultStore(DbPath, guard: guard, requireProtectedFolder: true);

        Assert.Equal(1, guard.Calls);
        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public void Loi_goc_cua_guard_duoc_giu_lai_de_chan_doan()
    {
        var ex = Assert.Throws<DataFolderProtectionException>(() =>
            new ResultStore(DbPath, guard: new FailingGuard(), requireProtectedFolder: true));

        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
    }
}
