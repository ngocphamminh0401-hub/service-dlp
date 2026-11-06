// [Thay đổi] File mới (GĐ2 bước 2.1): điểm khởi động của bản Linux. Cùng khung với DlpService/Program.cs (host Windows) nhưng:
//   - AddSystemd() thay cho AddWindowsPlatform() (SCM + Event Log + ACL)
//   - KHÔNG đăng ký IJobDispatchLoop → Worker tự bỏ qua vòng LLM (Llm:Enabled=false trong appsettings.json)
using DlpService;
using DlpService.Diagnostics;
using DlpService.Logging;
using DlpService.Platform;

// Lệnh chẩn đoán "explain <file_id>": chỉ đọc DB rồi thoát, không khởi động service (giống bản Windows)
if (args.Length > 0 && string.Equals(args[0], "explain", StringComparison.Ordinal))
{
    return ExplainCommand.Run(args);
}

var builder = Host.CreateApplicationBuilder(args);

// Nhận biết khi được systemd khởi động: báo READY/STOPPING cho systemd, log theo định dạng journald.
// Chạy tay ("dotnet run") thì không có tác dụng.
builder.Services.AddSystemd();

// TẠM — Bước 2.2: 3 adapter dưới đây là bản trung lập có sẵn trong Core, chỉ để Worker khởi động được.
//   - ExecutableAppLayout: rules/data/logs cạnh file chạy → Bước 2.2 thay bằng bố cục /etc/dlp-service, /var/lib/dlp-service, /var/log/dlp-service
//   - CaseInsensitivePathPolicy: SAI trên Linux (đường dẫn phân biệt hoa/thường) → Bước 2.2 thay bằng bản so Ordinal
//   - NoOpDataFolderGuard: không khoá thư mục dữ liệu → Bước 2.2 thay bằng chmod 0700
IAppLayout layout = new ExecutableAppLayout();
builder.Services.AddSingleton(layout);
builder.Services.AddSingleton<IPathPolicy>(new CaseInsensitivePathPolicy());
builder.Services.AddSingleton<IDataFolderGuard>(new NoOpDataFolderGuard());

// Log ra file (cạnh log console/journald) — dùng chung FileLoggerProvider của Core
builder.Logging.AddProvider(new FileLoggerProvider(layout.LogFolder));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;
