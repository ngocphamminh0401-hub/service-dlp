// [Thay đổi] File mới (mở rộng định dạng - Bước 2): tiện ích dùng chung cho mọi extractor
// [Thay đổi] Chặn zip bomb (Bước 12) → H4: chuyển sang ZipLimits.cs (ngân sách giải nén đầy đủ).

namespace DlpService.Reading.Extractors;

/// <summary>Tiện ích dùng chung: mở file ở chế độ chia sẻ, quy ước nối ô bảng.</summary>
internal static class ExtractorHelpers
{
    // [Thay đổi] H4: EnsureNotZipBomb (chỉ chặn 1 phần > 200 MB, kiểm ở lần mở file riêng) đã thay bằng ZipLimits.EnsureWithin —
    // ngân sách tổng + số phần + tỉ lệ nén, kiểm trên chính stream extractor sẽ đọc.

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
