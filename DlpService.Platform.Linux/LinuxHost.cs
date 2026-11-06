// [Thay đổi] File mới (GĐ2 bước 2.2d): gom MỌI thứ riêng của Linux vào một chỗ — tương ứng WindowsHost.AddWindowsPlatform() bên host Windows.
// Program.cs của host Linux chỉ còn gọi một dòng. Gồm: systemd, chọn bố cục thư mục, nạp /etc/dlp-service/appsettings.json, đăng ký 3 adapter,
// khoá thư mục log.
using DlpService.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DlpService.Platform.Linux;

/// <summary>Đăng ký các thành phần dành riêng cho Linux vào host.</summary>
public static class LinuxHost
{
    /// <summary>Thư mục cài đặt của gói .deb/.rpm (ke-hoach-trien-khai-linux.md 5.4). File chạy nằm ở đây → tự dùng chế độ System.</summary>
    public const string InstallDirectory = "/opt/dlp-service/";

    /// <summary>Biến môi trường chọn bố cục: "system" hoặc "portable". Không đặt → tự nhận theo vị trí file chạy.</summary>
    public const string LayoutVariable = "DLP_LAYOUT";

    /// <summary>Biến môi trường đổi gốc "/" của chế độ System — CHỈ để chạy thử không có sudo (VD DLP_SYSTEM_ROOT=$HOME/dlp-root).</summary>
    public const string SystemRootVariable = "DLP_SYSTEM_ROOT";

    /// <summary>Đăng ký systemd, bố cục thư mục, cấu hình /etc, các adapter Linux và file log. Trả bố cục để host dùng tiếp.</summary>
    public static LinuxAppLayout AddLinuxPlatform(this HostApplicationBuilder builder)
    {
        // Nhận biết khi được systemd khởi động: báo READY/STOPPING (Type=notify), log theo định dạng journald. Chạy tay thì không có tác dụng.
        builder.Services.AddSystemd();

        var layout = CreateLayout();
        var mode = layout.Mode;

        if (mode == LinuxLayoutMode.System)
        {
            AddSystemConfigFile(builder.Configuration, Path.Combine(layout.ConfigFolder, "appsettings.json"));
        }

        builder.Services.AddSingleton(layout);
        builder.Services.AddSingleton<IAppLayout>(layout);
        builder.Services.AddSingleton<IPathPolicy>(new LinuxPathPolicy());

        IDataFolderGuard guard = OperatingSystem.IsWindows() ? new NoOpDataFolderGuard() : new LinuxDataFolderGuard();
        builder.Services.AddSingleton(guard);

        // [Thay đổi] GĐ3: nguồn sự kiện inotify riêng (bỏ được thư mục khỏi việc theo dõi, đếm watch, phát hiện tràn hàng đợi — xem
        // InotifyFileChangeSource). "Watch:Backend": "fsw" → dùng lại FileSystemWatcher của .NET (dự phòng nếu inotify riêng gặp sự cố).
        // Không đăng ký → Worker tự dùng FileSystemWatcherSource.
        string backend = builder.Configuration["Watch:Backend"] ?? "inotify";
        if (OperatingSystem.IsLinux() && !string.Equals(backend, "fsw", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddSingleton<IFileChangeSource, InotifyFileChangeSource>();
        }

        // File log là kênh log CHÍNH (journald trên Ubuntu 16.04/CentOS 7 mặc định không lưu bền — mục 4.1). Log chứa đường dẫn file của
        // mọi người dùng (R7) → thư mục log khoá giống thư mục dữ liệu.
        CreateFolder(layout.LogFolder, mode);
        guard.Restrict(layout.LogFolder);
        builder.Logging.AddProvider(new FileLoggerProvider(layout.LogFolder));

        return layout;
    }

    /// <summary>Tạo bố cục theo biến môi trường <see cref="LayoutVariable"/> / <see cref="SystemRootVariable"/> và vị trí file chạy.</summary>
    public static LinuxAppLayout CreateLayout()
    {
        var mode = ResolveMode(Environment.GetEnvironmentVariable(LayoutVariable), AppContext.BaseDirectory);
        string root = Environment.GetEnvironmentVariable(SystemRootVariable) is { Length: > 0 } r ? r : "/";
        return new LinuxAppLayout(mode, systemRoot: root);
    }

    /// <summary>Cấu hình cho lệnh chẩn đoán "explain" (không khởi động host): cùng thứ tự nguồn như service — appsettings.json cạnh file
    /// chạy, rồi /etc/dlp-service/appsettings.json (chế độ System), rồi biến môi trường — để explain mở đúng DB mà service đang ghi.</summary>
    public static IConfiguration BuildExplainConfiguration(LinuxAppLayout layout)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true);
        if (layout.Mode == LinuxLayoutMode.System)
        {
            builder.AddJsonFile(Path.Combine(layout.ConfigFolder, "appsettings.json"), optional: true);
        }
        return builder.AddEnvironmentVariables().Build();
    }

    /// <summary>Chọn chế độ bố cục: biến môi trường thắng; không có thì System khi file chạy nằm trong <see cref="InstallDirectory"/>.</summary>
    /// <exception cref="InvalidOperationException">Giá trị biến môi trường không hợp lệ.</exception>
    public static LinuxLayoutMode ResolveMode(string? variable, string appDirectory)
    {
        if (!string.IsNullOrWhiteSpace(variable))
        {
            return variable.Trim().ToLowerInvariant() switch
            {
                "system" => LinuxLayoutMode.System,
                "portable" => LinuxLayoutMode.Portable,
                _ => throw new InvalidOperationException($"{LayoutVariable}=\"{variable}\" không hợp lệ — chỉ nhận \"system\" hoặc \"portable\""),
            };
        }
        string dir = Path.TrimEndingDirectorySeparator(appDirectory) + "/";
        return dir.StartsWith(InstallDirectory, StringComparison.Ordinal) ? LinuxLayoutMode.System : LinuxLayoutMode.Portable;
    }

    // Chèn /etc/dlp-service/appsettings.json NGAY TRƯỚC nguồn biến môi trường: thắng appsettings.json đi kèm bản build, nhưng vẫn THUA
    // biến môi trường và tham số dòng lệnh (giữ đúng thứ tự ưu tiên chuẩn của .NET — để systemd Environment= / test ghi đè được).
    // [Thay đổi] GĐ3 (sửa lỗi của GĐ2, tìm ra khi chạy kịch bản): .NET nạp biến môi trường HAI lần — nhóm "DOTNET_*" ở ĐẦU danh sách (cấu hình
    // của host) và nhóm không tiền tố ở GẦN CUỐI. Bản cũ chèn trước nguồn biến môi trường ĐẦU TIÊN → /etc nằm trước cả appsettings.json đi
    // kèm → mọi giá trị trong /etc bị ghi đè (chỉ "thắng" được những mục mà file đi kèm không có, VD mảng Watch:Folders rỗng).
    // Nay chèn trước nguồn biến môi trường CUỐI CÙNG: sau appsettings*.json, trước biến môi trường + dòng lệnh.
    internal static void AddSystemConfigFile(IConfigurationBuilder configuration, string path)
    {
        var sources = configuration.Sources;
        int index = sources.Count;
        for (int i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource)
            {
                index = i;
                break;
            }
        }
        var json = new ConfigurationBuilder().AddJsonFile(path, optional: true, reloadOnChange: false).Sources[0];
        sources.Insert(index, json);
    }

    private static void CreateFolder(string folder, LinuxLayoutMode mode)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (UnauthorizedAccessException ex) when (mode == LinuxLayoutMode.System)
        {
            // Thư mục /var/log/dlp-service do gói cài đặt tạo (GĐ4). Chạy tay ở chế độ System mà chưa cài → báo rõ cách chạy dev.
            throw new InvalidOperationException(
                $"Không tạo được {folder} (thiếu quyền). Chế độ System cần cài gói hoặc chạy bằng sudo; chạy thử thì đặt {LayoutVariable}=portable.", ex);
        }
    }
}
