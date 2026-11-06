using DlpService;
using DlpService.Diagnostics;
using DlpService.Llm;
using DlpService.Logging;
using DlpService.Pipeline;
using DlpService.Platform;
using DlpService.Platform.Windows; // [Thay đổi] Tách lõi bước 5: adapter riêng của Windows

// [Thay đổi] Lệnh chẩn đoán "explain <file_id>": chạy tay từ console, KHÔNG khởi động Windows Service/Worker —
// chỉ đọc DB rồi thoát ngay, đúng ý "lệnh chẩn đoán" (không phải 1 chế độ chạy nền mới).
if (args.Length > 0 && string.Equals(args[0], "explain", StringComparison.OrdinalIgnoreCase))
{
    return ExplainCommand.Run(args);
}

var builder = Host.CreateApplicationBuilder(args);

// [Thay đổi] Tách lõi bước 1: bố cục thư mục (hiện: cạnh file chạy, giữ đúng hành vi cũ). Khai báo kiểu interface để chỗ khác
// (Worker) chỉ phụ thuộc IAppLayout; sau này host Linux thay bằng bố cục /etc, /var/lib, /var/log.
IAppLayout layout = new ExecutableAppLayout();

// [Thay đổi] Tách lõi bước 4: mọi thứ riêng của Windows (SCM, Event Log, ACL, quy tắc đường dẫn) gom vào Platform/WindowsHost.
builder.AddWindowsPlatform();

// [Thay đổi] checklist "chạy như Windows Service thật" - đổi hướng logging: Event Log (AddEventLog ở trên)
// không ghi được gì trên máy test dù đã sửa SourceName, không rõ lý do (tự nuốt lỗi âm thầm) -> thêm file
// logger tự viết làm nơi ghi log CHÍNH, đáng tin cậy hơn, mở bằng Notepad xem trực tiếp được.
// [Thay đổi] Tách lõi bước 1: thư mục log lấy từ IAppLayout (trước đây ghép AppContext.BaseDirectory tại đây). Đăng ký vào DI để Worker dùng chung.
builder.Logging.AddProvider(new FileLoggerProvider(layout.LogFolder));
builder.Services.AddSingleton(layout);

// [Thay đổi] Tách lõi bước 5: vòng điều phối job LLM là phần RIÊNG của host Windows → host đăng ký; Worker (lõi) chỉ biết IJobDispatchLoop
builder.Services.AddSingleton<IJobDispatchLoop, LlmDispatchLoop>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;
