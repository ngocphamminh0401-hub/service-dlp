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
        using var stream = ExtractorHelpers.OpenShared(filePath); // [Thay đổi] Bước 2: dùng hàm mở file chung
        return ReadAll(stream); // [Thay đổi] M6: dùng chung bản Stream
    }

    /// <summary>[Thay đổi] M6: như bản đường dẫn nhưng đọc từ Stream (attachment email trong bộ nhớ).</summary>
    public static string ReadAll(Stream stream)
    {
        // Đọc hết byte 1 lần, rồi mới giải mã — để thử lại encoding khác mà không phải mở file lần 2
        // [Thay đổi] Tối ưu B5: đọc thẳng vào mảng đúng kích thước (trước: MemoryStream phình dần + ToArray = 2–3 bản sao byte của file 10 MB)
        byte[] bytes = ReadAllBytes(stream);

        // [Thay đổi] Tối ưu B5: file KHÔNG có BOM → giải mã UTF-8 một lần bằng GetString (cùng kết quả StreamReader: cùng bộ thay ký tự lỗi → '�',
        // trước đây còn qua StringBuilder trung gian). File có BOM (UTF-8/16/32) hoặc byte đầu đáng ngờ → giữ đường StreamReader cũ để tự nhận BOM.
        string text;
        if (bytes.Length == 0 || bytes[0] is not (0xEF or 0xFE or 0xFF or 0x00))
        {
            text = Encoding.UTF8.GetString(bytes);
        }
        else
        {
            // StreamReader tự nhận BOM (UTF-8/UTF-16); không có BOM thì mặc định UTF-8
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
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

    // [Thay đổi] Tối ưu B5: đọc hết Stream vào byte[] — nếu Stream seek được thì cấp đúng 1 mảng; file đang được ghi thêm (lớn hơn lúc đo) thì
    // đọc nốt phần dư như cũ để không mất dữ liệu.
    private static byte[] ReadAllBytes(Stream stream)
    {
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if (remaining >= 0 && remaining < Array.MaxLength)
            {
                var buffer = new byte[remaining];
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer, read, buffer.Length - read);
                    if (n == 0)
                    {
                        break;
                    }
                    read += n;
                }
                if (read < buffer.Length)
                {
                    return buffer.AsSpan(0, read).ToArray(); // file ngắn đi giữa chừng
                }
                int extra = stream.ReadByte();
                if (extra < 0)
                {
                    return buffer;
                }
                using var grown = new MemoryStream(buffer.Length + 4096); // file dài thêm khi đang đọc
                grown.Write(buffer);
                grown.WriteByte((byte)extra);
                stream.CopyTo(grown);
                return grown.ToArray();
            }
        }
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>[Thay đổi] Tối ưu B5: tách dòng kiểu lười, KẾT QUẢ GIỐNG HỆT <c>text.Split(["\r\n", "\n"], None)</c> (dòng cuối có thể rỗng; '\r' đứng một
    /// mình không phải dấu xuống dòng) nhưng không dựng cả mảng dòng một lúc.</summary>
    public static IEnumerable<string> SplitLines(string text)
    {
        int start = 0;
        while (true)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                yield return text.Substring(start);
                yield break;
            }
            int end = nl > start && text[nl - 1] == '\r' ? nl - 1 : nl;
            yield return text.Substring(start, end - start);
            start = nl + 1;
        }
    }
}
