// [Thay đổi] File mới (GĐ2 bước 2.2c): bố cục thư mục của bản Linux, 2 chế độ:
//   - System  (cài bằng gói, chạy dưới systemd): theo chuẩn thư mục Linux (FHS), như bảng "Bố cục trên máy" ở ke-hoach-trien-khai-linux.md 5.4
//       rule      /etc/dlp-service/rules          (cấu hình — admin sửa được)
//       DB        /var/lib/dlp-service/dlp.db     (dữ liệu biến đổi)
//       log       /var/log/dlp-service
//       model     <thư mục chạy>/confidence/...   (đi cùng bản build, không phải cấu hình)
//   - Portable (dev, chạy tay bằng "dotnet run", không cần sudo): mọi thứ cạnh file chạy — giống hệt ExecutableAppLayout của Windows.
// Chế độ chọn ở LinuxHost (biến môi trường DLP_LAYOUT, hoặc tự nhận khi file chạy nằm trong /opt/dlp-service).
namespace DlpService.Platform.Linux;

/// <summary>Chế độ bố cục thư mục của bản Linux.</summary>
public enum LinuxLayoutMode
{
    /// <summary>Mọi thứ cạnh file chạy (dev).</summary>
    Portable,

    /// <summary>/etc/dlp-service, /var/lib/dlp-service, /var/log/dlp-service (cài đặt thật).</summary>
    System,
}

/// <summary>Bố cục thư mục của bản Linux. Mọi đường dẫn trả về đều tuyệt đối.</summary>
public sealed class LinuxAppLayout : IAppLayout
{
    private readonly string _appDir;
    private readonly string _configDir;
    private readonly string _dataDir;
    private readonly string _logDir;

    /// <param name="mode">Chế độ bố cục.</param>
    /// <param name="appDirectory">Thư mục chứa file chạy; null = <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="systemRoot">Gốc của các thư mục hệ thống ở chế độ System; mặc định "/". Test (hoặc chạy thử không có sudo) truyền một
    /// thư mục tạm để không đụng /etc, /var thật.</param>
    public LinuxAppLayout(LinuxLayoutMode mode, string? appDirectory = null, string systemRoot = "/")
    {
        Mode = mode;
        _appDir = Path.GetFullPath(appDirectory ?? AppContext.BaseDirectory);
        if (mode == LinuxLayoutMode.System)
        {
            string root = Path.GetFullPath(systemRoot);
            _configDir = Path.Combine(root, "etc", "dlp-service");
            _dataDir = Path.Combine(root, "var", "lib", "dlp-service");
            _logDir = Path.Combine(root, "var", "log", "dlp-service");
        }
        else
        {
            _configDir = _appDir;
            _dataDir = Path.Combine(_appDir, "data");
            _logDir = Path.Combine(_appDir, "logs");
        }
    }

    /// <summary>Chế độ đang dùng.</summary>
    public LinuxLayoutMode Mode { get; }

    /// <summary>Thư mục cấu hình: chế độ System là /etc/dlp-service (chứa appsettings.json và rules/); Portable là thư mục chạy.</summary>
    public string ConfigFolder => _configDir;

    /// <summary>Thư mục dữ liệu (chứa DB).</summary>
    public string DataFolder => _dataDir;

    /// <inheritdoc/>
    public string RulesFolder => Path.Combine(_configDir, "rules");

    /// <inheritdoc/>
    public string LogFolder => _logDir;

    /// <inheritdoc/>
    public string DefaultDbPath => Path.Combine(_dataDir, "dlp.db");

    /// <inheritdoc/>
    public string DefaultConfidenceModelPath => Path.Combine(_appDir, "confidence", "file-confidence-v1.json");

    /// <inheritdoc/>
    /// <remarks>Đường dẫn tương đối (VD "Storage:DbPath": "khac.db") tính theo thư mục dữ liệu ở chế độ System — vì thư mục làm việc của
    /// service dưới systemd là "/" — và theo thư mục chạy ở chế độ Portable (giống Windows).</remarks>
    public string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(Mode == LinuxLayoutMode.System ? _dataDir : _appDir, path);
    }
}
