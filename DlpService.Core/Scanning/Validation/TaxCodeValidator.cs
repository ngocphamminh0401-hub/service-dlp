// [Thay đổi] File mới (cài validator): kiểm tra chữ số kiểm tra (checksum) của mã số thuế Việt Nam.
// Đăng ký trong Validators.ByName với tên "mst".
namespace DlpService.Scanning.Validation;

/// <summary>Kiểm tra mã số thuế 10 số (doanh nghiệp/cá nhân) hoặc 13 số (10 số + 3 số chi nhánh/phụ thuộc).</summary>
/// <remarks>
/// Số thứ 10 là CHỮ SỐ KIỂM TRA, tính từ 9 số đầu:
///   1. Nhân lần lượt 9 số đầu với trọng số 31, 29, 23, 19, 17, 13, 7, 5, 3 rồi cộng lại.
///   2. Chữ số kiểm tra = 10 − (tổng chia lấy dư cho 11). Nếu ra 10 thì mã đó không bao giờ được cấp.
/// Ví dụ 0100109106: 0×31 + 1×29 + 0×23 + 0×19 + 1×17 + 0×13 + 9×7 + 1×5 + 0×3 = 114; 114 mod 11 = 4; 10 − 4 = 6 ✓.
/// Gõ nhầm 1 chữ số bất kỳ hầu như luôn làm sai chữ số kiểm tra; số ngẫu nhiên chỉ có khoảng 1/11 cơ hội lọt qua.
/// </remarks>
public static class TaxCodeValidator
{
    private static readonly int[] Weights = [31, 29, 23, 19, 17, 13, 7, 5, 3];

    /// <summary>true nếu có 10 hoặc 13 chữ số và chữ số thứ 10 đúng checksum (13 số: 3 số cuối khác "000").</summary>
    /// <param name="value">Chuỗi regex đã khớp, có thể kèm nhãn hoặc dấu '-' ("0100109106-001").</param>
    public static bool IsValid(string value)
    {
        string digits = ValidatorText.DigitsOnly(value);
        if (digits.Length != 10 && digits.Length != 13)
        {
            return false;
        }

        // Mã chi nhánh/phụ thuộc đánh số từ 001, không có "000"
        if (digits.Length == 13 && digits[10..] == "000")
        {
            return false;
        }

        int sum = 0;
        for (int i = 0; i < Weights.Length; i++)
        {
            sum += (digits[i] - '0') * Weights[i];
        }

        int checkDigit = 10 - sum % 11;
        return checkDigit < 10 && checkDigit == digits[9] - '0';
    }
}
