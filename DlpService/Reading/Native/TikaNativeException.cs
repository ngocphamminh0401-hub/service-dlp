// [Thay đổi] File mới (tích hợp Tika native): các loại lỗi của bộ đọc Tika native.
// FileReaderService chỉ ghi TÊN loại exception (không ghi Message, vì Message có thể chứa trích đoạn nội dung file),
// nên mỗi tình huống chính có 1 loại riêng để log nói được "quá hạn" hay "có mật khẩu" thay vì "lỗi chung".
namespace DlpService.Reading.Native;

/// <summary>Lỗi chung của bộ đọc Tika native (DLL chưa sẵn sàng, file hỏng, lỗi nội bộ, hết bộ nhớ).</summary>
public class TikaNativeException(string message) : Exception(message);

/// <summary>Đọc file quá hạn thời gian cấu hình (TimeoutMs).</summary>
public sealed class TikaNativeTimeoutException(string message) : TikaNativeException(message);

/// <summary>File có mật khẩu/mã hoá nên không đọc được nội dung.</summary>
public sealed class TikaNativeEncryptedException(string message) : TikaNativeException(message);
