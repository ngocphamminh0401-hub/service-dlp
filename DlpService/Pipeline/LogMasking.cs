// [Thay đổi] File mới (kế hoạch tách lõi, bước 3): che giá trị nhạy cảm trước khi ghi log (CLAUDE.md mục 7.5 — không log CCCD/số thẻ nguyên văn).
// Tách khỏi FileWatcherService để mọi nơi ghi log dùng chung MỘT cách che, và để host Linux dùng lại nguyên.
namespace DlpService.Pipeline;

/// <summary>Che giá trị nhạy cảm khi ghi log.</summary>
public static class LogMasking
{
    // [Thay đổi] Hàm phụ mới: che giá trị nhạy cảm trước khi log, ví dụ "012345678901" -> "012******901"
    public static string Mask(string value)
    {
        // Chuỗi quá ngắn (<= 6 ký tự) thì che hết
        if (value.Length <= 6)
        {
            return "***";
        }

        // Giữ 3 ký tự đầu và 3 ký tự cuối, phần giữa thay bằng dấu *
        return value[..3] + new string('*', value.Length - 6) + value[^3..];
    }
}
