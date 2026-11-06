using DlpService;
using DlpService.Diagnostics;
using DlpService.Logging;

// [Thay đổi] Lệnh chẩn đoán "explain <file_id>": chạy tay từ console, KHÔNG khởi động Windows Service/Worker —
// chỉ đọc DB rồi thoát ngay, đúng ý "lệnh chẩn đoán" (không phải 1 chế độ chạy nền mới).
if (args.Length > 0 && string.Equals(args[0], "explain", StringComparison.OrdinalIgnoreCase))
{
    return ExplainCommand.Run(args);
}

var builder = Host.CreateApplicationBuilder(args);

// [Thay đổi] Bước A1 (checklist "chạy như Windows Service thật"): cho app tự nhận biết khi nó được
// Service Control Manager (SCM) khởi động (qua sc.exe/services.msc) và trả lời đúng giao thức SCM cần
// (start/stop/pause). Khi chạy tay bằng "dotnet run" như trước giờ, dòng này KHÔNG có tác dụng gì —
// chỉ phát huy tác dụng sau khi cài vào SCM (bước sau trong checklist).
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "DlpService";
});

// [Thay đổi] Bước B (checklist "chạy như Windows Service thật"): thêm Event Log làm nơi nhận log thứ 2,
// bên cạnh Console mặc định. Khi chạy dưới Service Control Manager (không có console đính kèm), Console
// coi như không ai đọc — Event Log là nơi DUY NHẤT còn "sống" để xem log lúc đó.
// AddEventLog() chỉ chạy được trên Windows (dự án này vốn chỉ chạy Windows) — bọc bằng
// OperatingSystem.IsWindows() để hết cảnh báo CA1416 và an toàn nếu lỡ build/chạy thử trên máy khác.
if (OperatingSystem.IsWindows())
{
    // [Thay đổi] Chỉ định rõ SourceName="DlpService" thay vì để mặc định (".NET Runtime") — nguồn mặc định
    // đang không ghi được log trên máy test (EventLogLoggerProvider tự nuốt lỗi khi ghi, không báo gì cả),
    // trong khi "DlpService" đã CHỨNG MINH ghi được (đây chính là nguồn WindowsServiceLifetime dùng để ghi
    // 2 dòng "Service started/stopped successfully").
    // CA1416 tắt tạm trong khối này: trình phân tích không lần theo được "OperatingSystem.IsWindows()" ở
    // ngoài xuyên qua lambda bên dưới, dù thực tế đã an toàn (đang nằm trong nhánh if này).
#pragma warning disable CA1416
    builder.Logging.AddEventLog(settings =>
    {
        settings.SourceName = "DlpService";
    });
#pragma warning restore CA1416
}

// [Thay đổi] checklist "chạy như Windows Service thật" - đổi hướng logging: Event Log (AddEventLog ở trên)
// không ghi được gì trên máy test dù đã sửa SourceName, không rõ lý do (tự nuốt lỗi âm thầm) -> thêm file
// logger tự viết làm nơi ghi log CHÍNH, đáng tin cậy hơn, mở bằng Notepad xem trực tiếp được.
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(AppContext.BaseDirectory, "logs")));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;
