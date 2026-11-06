// [Thay đổi] File mới (GĐ2 bước 2.2c/d): kiểm tra bố cục thư mục Linux (2 chế độ) và cách LinuxHost chọn chế độ.
// Chế độ System được kiểm với gốc giả (systemRoot = thư mục tạm) → không cần sudo, không đụng /etc, /var thật.
using DlpService.Platform;
using DlpService.Platform.Linux;

namespace DlpService.Tests.Platform;

public class LinuxAppLayoutTests
{
    private static readonly string App = Path.Combine(Path.GetTempPath(), "dlp-linux-app");
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dlp-linux-root");

    [Fact]
    public void Che_do_System_theo_bo_cuc_etc_var_lib_var_log()
    {
        var layout = new LinuxAppLayout(LinuxLayoutMode.System, App, Root);

        Assert.Equal(Path.Combine(Root, "etc", "dlp-service", "rules"), layout.RulesFolder);
        Assert.Equal(Path.Combine(Root, "var", "lib", "dlp-service", "dlp.db"), layout.DefaultDbPath);
        Assert.Equal(Path.Combine(Root, "var", "log", "dlp-service"), layout.LogFolder);
        // Model đi cùng bản build (không phải cấu hình) → cạnh file chạy
        Assert.StartsWith(App, layout.DefaultConfidenceModelPath);
    }

    [Fact]
    public void Che_do_Portable_giong_ban_canh_file_chay_cua_Windows()
    {
        IAppLayout linux = new LinuxAppLayout(LinuxLayoutMode.Portable, App);
        IAppLayout windows = new ExecutableAppLayout(App);

        Assert.Equal(windows.RulesFolder, linux.RulesFolder);
        Assert.Equal(windows.LogFolder, linux.LogFolder);
        Assert.Equal(windows.DefaultDbPath, linux.DefaultDbPath);
        Assert.Equal(windows.DefaultConfidenceModelPath, linux.DefaultConfidenceModelPath);
        Assert.Equal(windows.ResolvePath("data/khac.db"), linux.ResolvePath("data/khac.db"));
    }

    [Fact]
    public void System_duong_dan_tuong_doi_tinh_theo_thu_muc_du_lieu()
    {
        // Dưới systemd thư mục làm việc là "/" → đường dẫn tương đối không được tính theo thư mục làm việc
        var layout = new LinuxAppLayout(LinuxLayoutMode.System, App, Root);

        Assert.Equal(Path.Combine(Root, "var", "lib", "dlp-service", "khac.db"), layout.ResolvePath("khac.db"));
        Assert.Equal("/tuyet/doi.db", layout.ResolvePath("/tuyet/doi.db"));
    }

    [Fact]
    public void Moi_duong_dan_deu_tuyet_doi()
    {
        foreach (var mode in new[] { LinuxLayoutMode.System, LinuxLayoutMode.Portable })
        {
            IAppLayout layout = new LinuxAppLayout(mode, App, Root);
            foreach (var path in new[] { layout.RulesFolder, layout.LogFolder, layout.DefaultDbPath, layout.DefaultConfidenceModelPath })
            {
                Assert.True(Path.IsPathRooted(path), $"{mode}: {path}");
            }
        }
    }

    [Theory]
    [InlineData(null, "/opt/dlp-service/", LinuxLayoutMode.System)]       // cài bằng gói → System
    [InlineData(null, "/opt/dlp-service", LinuxLayoutMode.System)]        // thiếu "/" cuối vẫn nhận
    [InlineData(null, "/home/a/repo/bin/Debug/net10.0/", LinuxLayoutMode.Portable)] // dev
    [InlineData(null, "/opt/dlp-service-cu/", LinuxLayoutMode.Portable)]  // tên gần giống nhưng KHÔNG phải thư mục cài
    [InlineData("portable", "/opt/dlp-service/", LinuxLayoutMode.Portable)] // biến môi trường thắng
    [InlineData("System", "/home/a/bin/", LinuxLayoutMode.System)]          // không phân biệt hoa/thường
    [InlineData("", "/home/a/bin/", LinuxLayoutMode.Portable)]              // rỗng = không đặt
    public void LinuxHost_chon_che_do(string? variable, string appDir, LinuxLayoutMode expected)
    {
        Assert.Equal(expected, LinuxHost.ResolveMode(variable, appDir));
    }

    [Fact]
    public void LinuxHost_gia_tri_bien_moi_truong_sai_thi_bao_loi_ro()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LinuxHost.ResolveMode("prod", "/home/a/bin/"));
        Assert.Contains(LinuxHost.LayoutVariable, ex.Message);
    }
}
