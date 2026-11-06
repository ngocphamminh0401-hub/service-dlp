// [Thay đổi] File mới (cài validator): kiểm tra IBAN theo chuẩn ISO 13616 (thuật toán mod-97, ISO 7064).
// Đăng ký trong Validators.ByName với tên "iban".
using System.Text;

namespace DlpService.Scanning.Validation;

/// <summary>Kiểm tra số IBAN quốc tế bằng phép chia lấy dư cho 97.</summary>
/// <remarks>
/// IBAN = [2 chữ cái quốc gia] [2 số kiểm tra] [số tài khoản nội địa BBAN], dài 15-34 ký tự.
///   1. Chuyển 4 ký tự đầu xuống cuối:   GB82WEST12345698765432 → WEST12345698765432GB82
///   2. Đổi chữ cái thành số (A=10, B=11 ... Z=35): W=32, E=14, S=28, T=29, G=16, B=11...
///   3. Coi cả dãy là 1 số rất lớn, chia lấy dư cho 97 → IBAN hợp lệ khi số dư = 1.
/// Số quá lớn để lưu trong biến số nguyên, nên tính dư dần từng chữ số (giống chia tay trên giấy).
/// Chuỗi ngẫu nhiên chỉ có khoảng 1/97 (~1%) cơ hội lọt qua.
/// </remarks>
public static class IbanValidator
{
    /// <summary>true nếu chuỗi (bỏ khoảng trắng) là IBAN đúng định dạng và đúng mod-97.</summary>
    /// <param name="value">Chuỗi regex đã khớp, dạng liền hoặc nhóm 4 ký tự cách nhau bởi khoảng trắng.</param>
    public static bool IsValid(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }
        string iban = sb.ToString();

        if (iban.Length < 15 || iban.Length > 34
            || !char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1])
            || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]))
        {
            return false;
        }

        // Bước 1: chuyển 4 ký tự đầu xuống cuối
        string rearranged = iban[4..] + iban[..4];

        // Bước 2 + 3: đổi chữ thành số và tính số dư dần, không cần dựng số lớn
        int remainder = 0;
        foreach (char c in rearranged)
        {
            if (char.IsAsciiDigit(c))
            {
                remainder = (remainder * 10 + (c - '0')) % 97;
            }
            else if (char.IsAsciiLetterUpper(c))
            {
                // Chữ cái thành số 2 chữ số (10-35) → "dịch" số dư sang trái 2 chữ số
                remainder = (remainder * 100 + (c - 'A' + 10)) % 97;
            }
            else
            {
                return false;
            }
        }

        return remainder == 1;
    }
}
