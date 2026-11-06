// [Thay đổi] File mới (GĐ2 bước 2.1): điểm khởi động của bản Linux. Cùng khung với DlpService/Program.cs (host Windows) nhưng:
//   - AddLinuxPlatform() thay cho AddWindowsPlatform() (SCM + Event Log + ACL)
//   - KHÔNG đăng ký IJobDispatchLoop → Worker tự bỏ qua vòng LLM (Llm:Enabled=false trong appsettings.json)
using DlpService;
using DlpService.Diagnostics;
using DlpService.Platform.Linux;

// Lệnh chẩn đoán "explain <file_id>": chỉ đọc DB rồi thoát, không khởi động service (giống bản Windows)
// [Thay đổi] GĐ2 bước 2.2d: truyền bố cục + cấu hình Linux để explain mở đúng DB (chế độ System: /var/lib/dlp-service/dlp.db)
if (args.Length > 0 && string.Equals(args[0], "explain", StringComparison.Ordinal))
{
    var explainLayout = LinuxHost.CreateLayout();
    return ExplainCommand.Run(args, explainLayout, LinuxHost.BuildExplainConfiguration(explainLayout));
}

// [Thay đổi] GĐ2 bước 2.3 (sửa lỗi tìm ra khi chạy thật): đặt thư mục gốc (content root) = thư mục chứa file chạy. Mặc định .NET lấy THƯ MỤC
// LÀM VIỆC hiện tại → chạy "dotnet /duong/dan/dlp-service.dll" từ chỗ khác, hoặc dưới systemd (thư mục làm việc là "/"), sẽ KHÔNG đọc được
// appsettings.json đi kèm → mọi cấu hình về mặc định trong code (kể cả Llm:Enabled, Score). Bên Windows, AddWindowsService tự làm việc này.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// [Thay đổi] GĐ2 bước 2.2d: mọi thứ riêng của Linux (systemd, bố cục /etc + /var/lib + /var/log, quy tắc đường dẫn phân biệt hoa/thường,
// chmod 0700 thư mục dữ liệu và log, file log) gom vào DlpService.Platform.Linux/LinuxHost. Thay 3 adapter tạm của bước 2.1.
builder.AddLinuxPlatform();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;
