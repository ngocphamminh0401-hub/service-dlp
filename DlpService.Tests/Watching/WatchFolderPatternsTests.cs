// [Thay đổi] File mới (GĐ2, R8): kiểm tra mở rộng mẫu "Watch:Folders" ("/home/*/Documents") trên một cây thư mục giả trong thư mục tạm.
using DlpService.Watching;

namespace DlpService.Tests.Watching;

public class WatchFolderPatternsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dlp-pattern-" + Guid.NewGuid().ToString("N"));

    public WatchFolderPatternsTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, "an", "Documents"));
        Directory.CreateDirectory(Path.Combine(_home, "binh", "Documents"));
        Directory.CreateDirectory(Path.Combine(_home, "chi"));                 // không có Documents → không khớp
        File.WriteAllText(Path.Combine(_home, "file-le.txt"), "x");           // file, không phải thư mục → không khớp
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Sao_o_mot_cap_khop_moi_nguoi_dung_co_thu_muc_do()
    {
        var result = WatchFolderPatterns.Expand(Path.Combine(_home, "*", "Documents"));

        Assert.Equal([Path.Combine(_home, "an", "Documents"), Path.Combine(_home, "binh", "Documents")], result);
    }

    [Fact]
    public void Sao_o_cap_cuoi_chi_tra_thu_muc()
    {
        var result = WatchFolderPatterns.Expand(Path.Combine(_home, "*"));

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(Path.Combine(_home, "file-le.txt"), result);
    }

    [Fact]
    public void Khong_khop_gi_thi_tra_rong()
    {
        Assert.Empty(WatchFolderPatterns.Expand(Path.Combine(_home, "*", "Downloads")));
    }

    [Fact]
    public void Duong_dan_khong_co_mau_tra_lai_nguyen_ke_ca_khi_chua_ton_tai()
    {
        // Nơi gọi (FileWatcherService.StartWatching) tự báo "không tồn tại" như trước
        string missing = Path.Combine(_home, "chua-co");
        Assert.Equal([missing], WatchFolderPatterns.Expand(missing));
    }

    [Fact]
    public void Dau_hoi_khop_mot_ky_tu()
    {
        var result = WatchFolderPatterns.Expand(Path.Combine(_home, "?n", "Documents"));

        Assert.Equal([Path.Combine(_home, "an", "Documents")], result);
    }

    [Fact]
    public void Hai_sao_de_quy_khong_ho_tro()
    {
        Assert.Throws<ArgumentException>(() => WatchFolderPatterns.Expand(Path.Combine(_home, "**", "Documents")));
    }
}
