// [Thay đổi] File mới (cài validator): hàm phụ dùng chung để validator tách phần GIÁ TRỊ ra khỏi chuỗi regex đã khớp.
// Nhiều regex bắt cả nhãn lẫn giá trị ("Mã số thuế cá nhân: 8012345678001", "password=Xk9#mP2v") nên validator
// không được kiểm tra thẳng match.Value.
using System.Text;

namespace DlpService.Scanning.Validation;

/// <summary>Hàm phụ tách giá trị cho các validator.</summary>
internal static class ValidatorText
{
    /// <summary>Chỉ giữ chữ số: "0100109106-001" → "0100109106001", "+84 912 345 678" → "84912345678".</summary>
    public static string DigitsOnly(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsAsciiDigit(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Phần giá trị sau dấu ':' hoặc '=' ĐẦU TIÊN, bỏ nháy và dấu câu thừa:
    /// "password = 'Xk9#mP2v'" → "Xk9#mP2v", "\"token\": \"abc\"" → "abc". Không có dấu phân cách thì trả cả chuỗi.</summary>
    /// <remarks>Lấy dấu ĐẦU TIÊN (không phải cuối cùng) vì giá trị có thể chứa ':' hoặc '=' — VD chuỗi kết nối
    /// "postgres://user:pass@host", hoặc chuỗi base64 kết thúc bằng "==".</remarks>
    public static string ValueAfterLabel(string match)
    {
        int separator = match.IndexOfAny([':', '=']);
        string value = separator >= 0 ? match[(separator + 1)..] : match;
        return value.Trim().Trim('"', '\'', '`').Trim().TrimEnd(',', ';', '}', ']', ')');
    }
}
