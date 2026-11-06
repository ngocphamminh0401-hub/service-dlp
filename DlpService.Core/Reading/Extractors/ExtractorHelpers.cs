// [Thay đổi] File mới (mở rộng định dạng - Bước 2): tiện ích dùng chung cho mọi extractor
// [Thay đổi] Chặn zip bomb (Bước 12): thêm EnsureNotZipBomb — soi trước kích thước giải nén của từng phần trong
// file ZIP (.docx/.xlsx/.pptx đều là file ZIP) trước khi đưa cho thư viện đọc thật sự xử lý.
using System.IO.Compression;

namespace DlpService.Reading.Extractors;

/// <summary>Tiện ích dùng chung: mở file ở chế độ chia sẻ, quy ước nối ô bảng.</summary>
internal static class ExtractorHelpers
{
    // Kích thước SAU GIẢI NÉN tối đa cho phép của 1 phần bên trong file ZIP. Zip bomb khai báo trong header ZIP
    // 1 phần chỉ vài KB nén nhưng giải nén ra hàng trăm GB (toàn số 0, nén được cực mạnh) — chặn TRƯỚC khi giải nén.
    private const long MaxUncompressedEntryBytes = 200L * 1024 * 1024; // 200MB

    /// <summary>Từ chối file nếu có phần bên trong khai báo giải nén ra quá lớn (nghi ngờ zip bomb). File không
    /// phải định dạng ZIP (vd .xls nhị phân cũ) thì bỏ qua, không áp dụng kiểm tra này.</summary>
    /// <param name="filePath">Đường dẫn file .docx/.xlsx/.pptx cần kiểm tra trước khi mở bằng OpenXml/ExcelDataReader.</param>
    public static void EnsureNotZipBomb(string filePath)
    {
        using var stream = OpenShared(filePath);

        Span<byte> magic = stackalloc byte[2];
        bool isZip = stream.Read(magic) == 2 && magic[0] == (byte)'P' && magic[1] == (byte)'K';
        if (!isZip)
        {
            return;
        }

        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > MaxUncompressedEntryBytes)
            {
                throw new InvalidDataException(
                    $"Nghi ngờ zip bomb: 1 phần trong file giải nén ra > {MaxUncompressedEntryBytes / 1024 / 1024}MB");
            }
        }
    }

    /// <summary>Ký tự nối các ô trong 1 hàng bảng (Excel, bảng Word/PowerPoint) — mỗi hàng thành 1 dòng text, để chế độ
    /// bảng của proximity và volume_escalation hoạt động như với CSV.</summary>
    public const string CellSeparator = " | ";

    /// <summary>Mở file chỉ-đọc nhưng CHO PHÉP chương trình khác đang mở/ghi/xoá (Word, Excel đang mở file) — tránh
    /// lỗi "file đang bị khoá" không cần thiết khi chỉ cần đọc.</summary>
    /// <param name="filePath">Đường dẫn file.</param>
    public static FileStream OpenShared(string filePath) =>
        new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Nối các ô thành 1 dòng; ô chứa xuống dòng được đổi thành dấu cách để không làm vỡ hàng.
    /// Trả chuỗi rỗng nếu mọi ô đều trống.</summary>
    /// <param name="cells">Nội dung các ô của 1 hàng.</param>
    public static string JoinRow(IEnumerable<string> cells)
    {
        var cleaned = cells.Select(c => c.Replace("\r", " ").Replace("\n", " ").Trim()).ToList();

        // Bỏ các ô trống ở cuối hàng (Excel/Word thường có nhiều cột trống thừa)
        while (cleaned.Count > 0 && cleaned[^1].Length == 0)
        {
            cleaned.RemoveAt(cleaned.Count - 1);
        }

        return cleaned.Count == 0 ? string.Empty : string.Join(CellSeparator, cleaned);
    }

    // [Thay đổi] Streaming + giữ cấu trúc (Bước 4): tách 1 dòng CSV thành các ô, có xử lý ô nằm trong dấu ngoặc kép
    // (ô chứa dấu phẩy hoặc chính dấu ngoặc kép bên trong, theo chuẩn CSV — "" nghĩa là 1 dấu " thật trong ô).
    /// <summary>Tách 1 dòng CSV thành danh sách ô, hiểu ô đặt trong dấu ngoặc kép.</summary>
    /// <param name="line">1 dòng CSV thô.</param>
    public static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++; // bỏ qua dấu " thứ 2 của cặp "" (chỉ là 1 dấu " thật trong nội dung ô)
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        cells.Add(current.ToString());
        return cells;
    }
}
