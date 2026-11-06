// [Thay đổi] File mới (kế hoạch tách lõi, bước 1): kiểm tra hợp đồng của IAppLayout với bản "cạnh file chạy" (Windows).
// Sau này adapter Linux dùng lại đúng các phép kiểm tra chung (đường dẫn tuyệt đối, ResolvePath giữ đường dẫn tuyệt đối).
using DlpService.Platform;

namespace DlpService.Tests.Platform;

public class AppLayoutTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "dlp-layout-test");

    [Fact]
    public void Cac_duong_dan_mac_dinh_deu_tuyet_doi_va_nam_duoi_thu_muc_goc()
    {
        IAppLayout layout = new ExecutableAppLayout(Base);

        foreach (var path in new[] { layout.RulesFolder, layout.LogFolder, layout.DefaultDbPath, layout.DefaultConfidenceModelPath })
        {
            Assert.True(Path.IsPathRooted(path), path);
            Assert.StartsWith(Base, path);
        }
        Assert.EndsWith("rules", layout.RulesFolder);
        Assert.EndsWith("dlp.db", layout.DefaultDbPath);
    }

    [Fact]
    public void ResolvePath_duong_dan_tuong_doi_tinh_theo_thu_muc_goc()
    {
        IAppLayout layout = new ExecutableAppLayout(Base);

        Assert.Equal(Path.Combine(Base, "data", "dlp.db"), layout.ResolvePath(Path.Combine("data", "dlp.db")));
    }

    [Fact]
    public void ResolvePath_duong_dan_tuyet_doi_giu_nguyen()
    {
        IAppLayout layout = new ExecutableAppLayout(Base);
        string absolute = Path.Combine(Path.GetTempPath(), "noi-khac", "dlp.db");

        Assert.Equal(absolute, layout.ResolvePath(absolute));
    }

    [Fact]
    public void Khong_truyen_thu_muc_goc_thi_dung_thu_muc_chua_file_chay()
    {
        IAppLayout layout = new ExecutableAppLayout();

        Assert.StartsWith(AppContext.BaseDirectory, layout.RulesFolder);
    }
}
