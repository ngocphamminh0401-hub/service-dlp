// [Thay đổi] Tối ưu P1 (giai đoạn 2): tìm các KÝ TỰ BẮT BUỘC của một regex — ký tự mà mọi chỗ khớp đều phải chứa (VD '@' của pattern email).
// Nếu file không có ký tự đó thì pattern chắc chắn không khớp, không cần chạy regex (đo 19/11/2026: 4 pattern email = 6,6% thời gian Scan trên dữ liệu thật,
// gần như không có khớp). Bộ phân tích BẢO THỦ: chỉ trả ký tự khi CHẮC CHẮN bắt buộc; nghi ngờ thì bỏ qua (thiếu ký tự chỉ làm mất cơ hội tối ưu,
// không bao giờ làm đổi kết quả). Chỉ xét ký tự ASCII không phải chữ/số/khoảng trắng (chữ phụ thuộc IgnoreCase, chữ số lẫn với backreference).
using System.Text.RegularExpressions;

namespace DlpService.Scanning;

internal static class RegexRequiredChars
{
    /// <summary>Các ký tự bắt buộc ở cấp trên cùng của regex (ngoài nhóm, ngoài lớp ký tự, không đứng trước lượng từ cho phép 0 lần).
    /// Trả mảng rỗng nếu regex có phép chọn "|" ở cấp trên cùng hoặc có cấu trúc chưa hiểu chắc.</summary>
    public static char[] Analyze(string pattern, RegexOptions options)
    {
        if ((options & (RegexOptions.IgnorePatternWhitespace | RegexOptions.RightToLeft | RegexOptions.ECMAScript)) != 0)
        {
            return [];
        }

        var found = new List<char>();
        int n = pattern.Length;
        int i = 0;
        int depth = 0;
        while (i < n)
        {
            char c = pattern[i];
            switch (c)
            {
                case '\\':
                {
                    if (i + 1 >= n)
                    {
                        return [];
                    }
                    char e = pattern[i + 1];
                    // Escape của ký tự đặc biệt/dấu câu (\. \@ \- \/ ...) là ký tự thường; escape bằng chữ/số (\d \b \p{..} \1 ...) không phải
                    if (depth == 0 && IsPlainSymbol(e) && !ZeroAllowed(pattern, i + 2))
                    {
                        found.Add(e);
                    }
                    int len = EscapeLength(pattern, i);
                    if (len < 0)
                    {
                        return [];
                    }
                    i += len;
                    break;
                }
                case '[':
                {
                    int end = ClassEnd(pattern, i);
                    if (end < 0)
                    {
                        return [];
                    }
                    i = end + 1;
                    break;
                }
                case '(':
                {
                    // Cờ x (IgnorePatternWhitespace) đặt trong nhóm làm đổi cách đọc chuỗi regex → không phân tích
                    if (i + 1 < n && pattern[i + 1] == '?' && HasExtendedFlag(pattern, i + 2))
                    {
                        return [];
                    }
                    depth++;
                    i++;
                    break;
                }
                case ')':
                {
                    depth--;
                    if (depth < 0)
                    {
                        return [];
                    }
                    i++;
                    break;
                }
                case '|':
                {
                    if (depth == 0)
                    {
                        return []; // phép chọn ở cấp trên cùng: không ký tự nào chắc chắn bắt buộc
                    }
                    i++;
                    break;
                }
                case '{':
                {
                    // Lượng từ {n} {n,} {n,m}: bỏ qua cả cụm (dấu ',' bên trong KHÔNG phải ký tự bắt buộc)
                    // '{' KHÔNG đúng dạng lượng từ ("{,2}", "{" lẻ): .NET coi là ký tự thường — hành vi tinh vi, bỏ phân tích cho chắc
                    int q = QuantifierEnd(pattern, i);
                    if (q < 0)
                    {
                        return [];
                    }
                    i = q + 1;
                    break;
                }
                case '.':
                case '^':
                case '$':
                case '?':
                case '*':
                case '+':
                case '}':
                {
                    i++; // siêu ký tự/lượng từ: bỏ qua (không ghi nhận)
                    break;
                }
                default:
                {
                    if (depth == 0 && IsPlainSymbol(c) && !ZeroAllowed(pattern, i + 1))
                    {
                        found.Add(c);
                    }
                    i++;
                    break;
                }
            }
        }

        return depth == 0 ? found.Distinct().ToArray() : [];
    }

    // Vị trí '}' đóng lượng từ {n} {n,} {n,m} bắt đầu tại p[start] == '{'; -1 nếu không đúng dạng lượng từ
    private static int QuantifierEnd(string p, int start)
    {
        int j = start + 1;
        int digits = j;
        while (j < p.Length && char.IsAsciiDigit(p[j]))
        {
            j++;
        }
        if (j == digits)
        {
            return -1;
        }
        if (j < p.Length && p[j] == ',')
        {
            j++;
            while (j < p.Length && char.IsAsciiDigit(p[j]))
            {
                j++;
            }
        }
        return j < p.Length && p[j] == '}' ? j : -1;
    }

    // Ký tự ASCII in được, không phải chữ/số/khoảng trắng
    private static bool IsPlainSymbol(char c) => c is > ' ' and < (char)127 && !char.IsLetterOrDigit(c);

    // Ký tự đứng ngay trước vị trí pos có thể xuất hiện 0 lần không (lượng từ ? * {0} {0,n}). {,n} và dạng lạ → coi là "có thể 0" cho chắc.
    private static bool ZeroAllowed(string p, int pos)
    {
        if (pos >= p.Length)
        {
            return false;
        }
        char q = p[pos];
        if (q is '?' or '*')
        {
            return true;
        }
        if (q == '{')
        {
            int j = pos + 1;
            int start = j;
            while (j < p.Length && char.IsAsciiDigit(p[j]))
            {
                j++;
            }
            if (j == start)
            {
                return true; // không có số nhỏ nhất: "{,n}" hoặc "{" thường → không chắc
            }
            return int.TryParse(p.AsSpan(start, j - start), out int min) ? min == 0 : true;
        }
        return false;
    }

    // Độ dài một chuỗi escape bắt đầu tại p[i] == '\\'; -1 nếu không đọc được
    private static int EscapeLength(string p, int i)
    {
        char e = p[i + 1];
        switch (e)
        {
            case 'p':
            case 'P':
            {
                int close = p.IndexOf('}', i + 2);
                return i + 2 < p.Length && p[i + 2] == '{' && close > 0 ? close - i + 1 : -1;
            }
            case 'k':
            {
                if (i + 2 < p.Length && (p[i + 2] == '<' || p[i + 2] == '\''))
                {
                    char closer = p[i + 2] == '<' ? '>' : '\'';
                    int close = p.IndexOf(closer, i + 3);
                    return close > 0 ? close - i + 1 : -1;
                }
                return -1;
            }
            case 'x':
                return 4;
            case 'u':
                return 6;
            case 'c':
                return 3;
            default:
                return 2;
        }
    }

    // Vị trí ký tự ']' đóng lớp ký tự bắt đầu tại p[start] == '['; -1 nếu không thấy. Hiểu '\\', ']' đầu lớp là ký tự thường, lớp lồng (trừ lớp).
    private static int ClassEnd(string p, int start)
    {
        int j = start + 1;
        int nest = 1;
        if (j < p.Length && p[j] == '^')
        {
            j++;
        }
        if (j < p.Length && p[j] == ']')
        {
            j++;
        }
        while (j < p.Length)
        {
            char c = p[j];
            if (c == '\\')
            {
                j += 2;
                continue;
            }
            if (c == '[')
            {
                nest++;
            }
            else if (c == ']')
            {
                nest--;
                if (nest == 0)
                {
                    return j;
                }
            }
            j++;
        }
        return -1;
    }

    // Nhóm bắt đầu "(?" tại p[pos]: có cờ x trong phần cờ (ví dụ "(?x)", "(?ix:", "(?i-x)") không
    private static bool HasExtendedFlag(string p, int pos)
    {
        for (int j = pos; j < p.Length; j++)
        {
            char c = p[j];
            if (c == 'x' || c == 'X')
            {
                return true;
            }
            if (c is ')' or ':')
            {
                return false;
            }
            if (!char.IsLetter(c) && c != '-')
            {
                return false; // (?<..., (?!, (?=, (?'... — không phải nhóm cờ
            }
        }
        return false;
    }
}
