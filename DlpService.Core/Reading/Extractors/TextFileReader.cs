// [Thay đổi] File mới (mở rộng định dạng - Bước 1): hàm đọc file text dùng chung cho TxtExtractor và CsvExtractor
using System.Text;

namespace DlpService.Reading.Extractors;

/// <summary>Đọc file text với encoding đúng: UTF-8 (có hoặc không BOM), nếu thấy ký tự lỗi thì thử lại bằng Windows-1258 (tiếng Việt).</summary>
internal static class TextFileReader
{
    /// <summary>Đọc toàn bộ file text, cho phép chương trình khác đang mở file (FileShare.ReadWrite).</summary>
    /// <param name="filePath">Đường dẫn file text.</param>
    public static string ReadAll(string filePath)
    {
        // Đọc hết byte 1 lần, rồi mới giải mã — để thử lại encoding khác mà không phải mở file lần 2
        byte[] bytes;
        using (var stream = ExtractorHelpers.OpenShared(filePath)) // [Thay đổi] Bước 2: dùng hàm mở file chung
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }

        // StreamReader tự nhận BOM (UTF-8/UTF-16); không có BOM thì mặc định UTF-8
        string text;
        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
        }

        // Ký tự '�' (dấu hỏi trong ô vuông) = có byte không phải UTF-8 hợp lệ → nhiều khả năng file lưu bằng
        // bảng mã ANSI tiếng Việt (VD CSV xuất từ Excel cũ). Thử lại bằng Windows-1258.
        if (text.Contains('�'))
        {
            text = Encoding.GetEncoding(1258).GetString(bytes);
        }

        return text;
    }
}
