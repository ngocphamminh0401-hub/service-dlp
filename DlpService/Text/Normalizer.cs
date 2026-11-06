// [Thay đổi] File mới: gom các hàm chuẩn hoá chữ dùng chung — trước đây nằm rải rác (NFC ở FileReaderService, bỏ
// dấu + tách từ ẩn bên trong EscalationPolicy). Chuyển về đây để chỗ khác (Scanning, extractor...) dùng lại được,
// không viết lại logic FormD/NonSpacingMark/"đ→d" lần nữa. KHÔNG đổi hành vi so với code cũ.
using System.Globalization;
using System.Text;

namespace DlpService.Text;

/// <summary>Chuẩn hoá chữ dùng chung: NFC (nội dung file), bỏ dấu và tách từ (khớp từ khoá không phân biệt dấu).</summary>
public static class Normalizer
{
    /// <summary>Chuẩn hoá Unicode về dạng DỰNG SẴN (NFC). Tiếng Việt có 2 cách lưu cùng 1 chữ: "ọ" = 1 ký tự (NFC,
    /// cách rule JSON viết) hoặc "o" + dấu nặng rời (NFD — bảng mã Windows-1258, file tạo trên macOS). Nhìn giống
    /// hệt nhưng so sánh chuỗi KHÁC nhau → mọi từ khoá có dấu ("Họ tên", "mật khẩu") sẽ không khớp.</summary>
    public static string ToNfc(string text) => text.Normalize(NormalizationForm.FormC);

    /// <summary>Bỏ dấu tiếng Việt: tách ký tự gốc + dấu (FormD) rồi bỏ dấu; "đ/Đ" không phải dấu ghép nên đổi tay.</summary>
    public static string RemoveDiacritics(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (char ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            plain.Append(ch switch { 'đ' => 'd', 'Đ' => 'D', _ => ch });
        }
        return plain.ToString();
    }

    /// <summary>Tách chuỗi thành các từ không dấu, chữ thường. Ranh giới từ: ký tự không phải chữ/số, chuyển chữ
    /// thường → chữ hoa ("HopDong" → hop, dong), chuyển chữ ↔ số ("luong2024" → luong, 2024).</summary>
    public static List<string> Tokenize(string text)
    {
        string plain = RemoveDiacritics(text);

        var tokens = new List<string>();
        var current = new StringBuilder();
        char previous = '\0';
        foreach (char ch in plain)
        {
            bool boundary = !char.IsLetterOrDigit(ch)
                || (char.IsUpper(ch) && char.IsLower(previous))
                || (char.IsDigit(ch) != char.IsDigit(previous) && char.IsLetterOrDigit(previous));
            if (boundary && current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            previous = ch;
        }
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }
        return tokens;
    }
}
