// [Thay đổi] File mới (kế hoạch tách lõi, bước 3): che giá trị nhạy cảm trước khi ghi log (CLAUDE.md mục 7.5 — không log CCCD/số thẻ nguyên văn).
// Tách khỏi FileWatcherService để mọi nơi ghi log dùng chung MỘT cách che, và để host Linux dùng lại nguyên.
namespace DlpService.Pipeline;

/// <summary>Che giá trị nhạy cảm khi ghi log.</summary>
public static class LogMasking
{
    // [Thay đổi] GĐ2 (R7): siết cách che. Cách cũ giữ 3 ký tự đầu + 3 cuối → CCCD 12 số lộ 6 số (đầu là mã tỉnh + giới tính + năm sinh,
    // cuối là số ngẫu nhiên — ghép với tên/ngày sinh trong cùng log là đoán được), tên người lộ 3 chữ đầu ("Ngu***").
    // Cách mới: chỉ giữ 2 ký tự CUỐI và chỉ khi giá trị đủ dài (> 8); độ dài vẫn giữ để biết đã khớp chuỗi cỡ nào.
    // Thêm nữa, FileProcessor chỉ ghi dòng chi tiết từng chỗ khớp ở mức Debug (mặc định không bật).
    private const int MinLengthToShowTail = 9;
    private const int TailLength = 2;

    /// <summary>Che giá trị: "012345678901" → "**********01"; chuỗi &lt;= 8 ký tự → "***".</summary>
    public static string Mask(string value)
    {
        if (value.Length < MinLengthToShowTail)
        {
            return "***";
        }
        return new string('*', value.Length - TailLength) + value[^TailLength..];
    }
}
