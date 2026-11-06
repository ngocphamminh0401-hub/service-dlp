// [Thay đổi] File mới (checklist "chạy như Windows Service thật" - đổi hướng logging): ghi log ra file text
// theo ngày, thay cho Event Log — EventLogLoggerProvider trên máy test không ghi được gì (tự nuốt lỗi âm
// thầm, không rõ lý do), file logger đơn giản hơn, không có tầng trung gian nào có thể "nuốt" lỗi kiểu đó.
namespace DlpService.Logging;

/// <summary>1 logger ghi ra file text theo ngày (VD logs/dlpservice-2026-09-23.log, tách .1.log, .2.log... khi quá lớn), append, kèm timestamp/category/level.</summary>
public class FileLogger : ILogger
{
    private readonly string _categoryName;
    // [Thay đổi] Log rotation: thay cặp (logFolder, writeLock) bằng tham chiếu tới provider — provider giữ trạng
    // thái DÙNG CHUNG (file đang ghi, số thứ tự phần, ngày hiện tại) cho mọi FileLogger.
    // [Thay đổi] Ghi log qua hàng đợi: provider giữ hàng đợi + luồng nền ghi file, không còn khoá ghi ở đây.
    private readonly FileLoggerProvider _provider;

    public FileLogger(string categoryName, FileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {_categoryName}: {formatter(state, exception)}";
        if (exception != null)
        {
            line += Environment.NewLine + exception;
        }

        // [Thay đổi] Ghi log qua hàng đợi: trước đây mỗi dòng lấy khoá chung rồi mở/ghi/đóng file ngay tại đây → các luồng
        // xử lý file phải xếp hàng chờ nhau (nút thắt thông lượng của cả service, đo ở buổi 12). Giờ chỉ đưa dòng vào hàng
        // đợi của provider; 1 luồng nền ghi ra file (giữ thứ tự, xoay vòng 5 MB/ngày, thử lại khi file bị khoá, lỗi thì bỏ
        // dòng — logger vẫn không bao giờ làm sập service).
        _provider.Enqueue(line);
    }
}
