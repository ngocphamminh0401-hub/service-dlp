// [Thay đổi] File mới (tích hợp Tika native): giữ DUY NHẤT MỘT thư viện native cho cả tiến trình.
// Lý do dùng lớp static: FileReaderService được tạo ở nhiều chỗ (FileWatcherService, vòng điều phối LLM...) và mỗi lần tạo lại
// sinh extractor mới bằng reflection (Activator.CreateInstance, constructor không tham số) — extractor không nhận được cấu hình
// qua DI. Còn isolate GraalVM thì chỉ nên tạo 1 lần (tốn thời gian và bộ nhớ).
using Microsoft.Extensions.Logging;

namespace DlpService.Reading.Native;

/// <summary>Cấu hình + vòng đời của thư viện Tika native. Worker gọi Configure khi khởi động và Shutdown khi dừng.</summary>
public static class TikaNativeRuntime
{
    private static readonly object Gate = new();
    private static TikaNativeOptions _options = new();   // mặc định: tắt
    private static ILogger? _logger;
    private static TikaNativeLibrary? _library;
    private static bool _attempted;                       // đã thử nạp (thành công hay không) — chỉ thử 1 lần mỗi lần Configure

    /// <summary>Đặt cấu hình. Phải gọi TRƯỚC khi tạo FileReaderService đầu tiên (nếu không, cấu hình bị bỏ qua và có cảnh báo).</summary>
    public static void Configure(TikaNativeOptions options, ILogger logger)
    {
        lock (Gate)
        {
            if (_attempted)
            {
                logger.LogWarning("[TikaNative] Cấu hình đến muộn: thư viện đã được thử nạp, bỏ qua cấu hình mới");
                return;
            }
            _options = options;
            _logger = logger;
        }
    }

    /// <summary>Thư viện đã nạp xong và sẵn sàng; null nếu tắt hoặc nạp thất bại. Lần đầu truy cập sẽ thử nạp.</summary>
    internal static TikaNativeLibrary? Library
    {
        get
        {
            lock (Gate)
            {
                if (!_attempted)
                {
                    _attempted = true;
                    TryLoad();
                }
                return _library;
            }
        }
    }

    /// <summary>true nếu bộ đọc Tika native dùng được (đã bật, nạp thành công).</summary>
    public static bool IsAvailable => Library != null;

    // [Thay đổi] Tika native chế độ Replace
    /// <summary>true nếu Tika native dùng được VÀ được cấu hình thay thế toàn bộ bộ đọc cũ.</summary>
    public static bool ReplacesAll => IsAvailable && _options.Mode == TikaNativeMode.Replace;

    private static void TryLoad()
    {
        if (!_options.Enabled)
        {
            _logger?.LogInformation("[TikaNative] Tắt (TikaNative:Enabled = false), không hỗ trợ thêm định dạng nào");
            return;
        }
        try
        {
            _library = TikaNativeLibrary.Load(_options, AppContext.BaseDirectory);
            // [Thay đổi] ghi thêm chế độ (Supplement/Replace) và số isolate để biết service đang chạy cấu hình nào
            _logger?.LogInformation("[TikaNative] Đã nạp DLL (SHA-256 {sha}), chế độ {mode}, {isolates} isolate, hạn {timeout} ms/file, tối đa {chars:N0} ký tự",
                _library.Sha256, _options.Mode, _library.IsolateCount, _options.TimeoutMs, _options.MaxChars);
        }
        catch (Exception ex)
        {
            // Không nạp được KHÔNG được làm dừng service: chỉ không hỗ trợ thêm định dạng. Chỉ ghi tên loại lỗi + thông báo (không có nội dung file).
            _logger?.LogWarning("[TikaNative] Bật nhưng không nạp được ({type}): {message}. Service chạy tiếp không có định dạng bổ sung",
                ex.GetType().Name, ex.Message);
            _library = null;
        }
    }

    /// <summary>Huỷ parser và isolate. Gọi khi service dừng; an toàn khi gọi nhiều lần.</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            _library?.Dispose();
            _library = null;
        }
    }

    /// <summary>Chỉ dùng trong test: đưa trạng thái về như lúc mới khởi động tiến trình.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _library?.Dispose();
            _library = null;
            _attempted = false;
            _options = new TikaNativeOptions();
            _logger = null;
        }
    }
}
