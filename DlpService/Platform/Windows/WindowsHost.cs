// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): gom MỌI thứ riêng của Windows trong Program.cs vào một chỗ — đăng ký với Service Control Manager,
// Event Log, ACL thư mục dữ liệu, quy tắc đường dẫn. Program.cs chỉ còn gọi một dòng; host Linux sau này có hàm tương tự (systemd, journald, chmod).
// Phần này KHÔNG chuyển thành interface (IServiceLifetime/ILogSink) vì lõi không dùng nó — nó nằm hoàn toàn ở host.
using System.Runtime.Versioning;
using DlpService.Platform;

namespace DlpService.Platform.Windows;

/// <summary>Đăng ký các thành phần dành riêng cho Windows vào host.</summary>
public static class WindowsHost
{
    /// <summary>Đăng ký Windows Service (SCM), Event Log và các adapter Windows (ACL, quy tắc đường dẫn).</summary>
    public static void AddWindowsPlatform(this HostApplicationBuilder builder)
    {
        // [Thay đổi] Bước A1 (checklist "chạy như Windows Service thật"): cho app tự nhận biết khi nó được
        // Service Control Manager (SCM) khởi động (qua sc.exe/services.msc) và trả lời đúng giao thức SCM cần
        // (start/stop/pause). Khi chạy tay bằng "dotnet run" như trước giờ, dòng này KHÔNG có tác dụng gì.
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "DlpService";
        });

        // Quy tắc đường dẫn (không phân biệt hoa/thường, bỏ file khoá "~$" của Office) và khoá thư mục dữ liệu
        builder.Services.AddSingleton<IPathPolicy>(new CaseInsensitivePathPolicy());
        if (OperatingSystem.IsWindows())
        {
            AddWindowsOnly(builder);
        }
        else
        {
            // Chạy thử trên máy khác: không có ACL Windows → không khoá (ResultStore sẽ ghi log cảnh báo khi guard là null; ở đây dùng NoOp có chủ đích)
            builder.Services.AddSingleton<IDataFolderGuard>(new NoOpDataFolderGuard());
        }
    }

    // [Thay đổi] Bước B (checklist "chạy như Windows Service thật"): thêm Event Log làm nơi nhận log thứ 2, bên cạnh Console mặc định.
    // Khi chạy dưới SCM (không có console đính kèm), Console coi như không ai đọc. Chỉ định rõ SourceName="DlpService" thay vì mặc định
    // (".NET Runtime") — nguồn mặc định từng không ghi được log trên máy test (EventLogLoggerProvider tự nuốt lỗi khi ghi).
    [SupportedOSPlatform("windows")]
    private static void AddWindowsOnly(HostApplicationBuilder builder)
    {
        builder.Logging.AddEventLog(settings =>
        {
            settings.SourceName = "DlpService";
        });
        builder.Services.AddSingleton<IDataFolderGuard>(new WindowsDataFolderGuard());
    }
}
