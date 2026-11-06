// [Thay đổi] File mới (bước 4.5): các hàm kiểm tra bổ sung sau khi regex khớp, để giảm báo động giả
using DlpService.Scanning.Validation; // [Thay đổi] Cài validator: thuật toán của từng validator nằm ở Scanning/Validation/

namespace DlpService.Scanning;

// [Thay đổi] Cài validator: tách 2 loại validator. Trước đây MỌI rule có validator đều được RegexScanner coi là "đủ bằng
// chứng" để qua level_gate — đúng với Luhn (checksum, số ngẫu nhiên khó qua), nhưng SAI với bộ lọc email/SĐT/entropy
// (chỉ loại giá trị chắc chắn giả): nếu vẫn coi là bằng chứng, email/SĐT sẽ không bao giờ bị gate hạ mức nữa → báo nhầm tăng.
/// <summary>1 validator đã đăng ký: hàm kiểm tra + cờ cho biết đây là checksum (bằng chứng mạnh cho level_gate) hay
/// chỉ là bộ lọc.</summary>
/// <param name="Check">Hàm kiểm tra: nhận chuỗi regex đã khớp, trả false thì bỏ chỗ khớp đó.</param>
/// <param name="IsChecksum">true = có quy luật toán học (Luhn, MST, IBAN, CCCD), được tính là bằng chứng củng cố.</param>
public sealed record ValidatorEntry(Func<string, bool> Check, bool IsChecksum);

/// <summary>Các hàm kiểm tra bổ sung sau khi regex khớp, để giảm báo động giả.</summary>
public static class Validators
{
    // [Thay đổi] Cài validator: value đổi từ Func<string, bool> sang ValidatorEntry (thêm cờ IsChecksum), thêm 6 validator
    // mới, và không phân biệt hoa thường khi tra tên
    /// <summary>Bảng tra: tên validator trong file rule JSON -> validator tương ứng. Thêm validator mới chỉ cần thêm 1 dòng.</summary>
    public static readonly Dictionary<string, ValidatorEntry> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["luhn"] = new(Luhn, IsChecksum: true),
        ["vn_cccd_prefix"] = new(CccdValidator.IsValid, IsChecksum: true),
        ["mst"] = new(TaxCodeValidator.IsValid, IsChecksum: true),
        ["iban"] = new(IbanValidator.IsValid, IsChecksum: true),
        ["secret_entropy"] = new(SecretEntropyValidator.IsLikelySecret, IsChecksum: false),
        ["email_personal"] = new(EmailValidator.IsPersonal, IsChecksum: false),
        ["vn_phone"] = new(PhoneValidator.IsValid, IsChecksum: false)
    };

    /// <summary>Kiểm tra 1 chuỗi số có thoả thuật toán Luhn hay không (dùng để xác nhận số thẻ tín dụng).</summary>
    /// <param name="value">Chuỗi số, có thể lẫn dấu cách hoặc dấu '-'.</param>
    /// <returns>true nếu có 13-19 chữ số và tổng Luhn chia hết cho 10.</returns>
    public static bool Luhn(string value)
    {
        int sum = 0;
        int digitCount = 0; // đếm số chữ số đã duyệt, cũng là vị trí tính từ phải sang trái (bắt đầu từ 0)

        // Duyệt từ phải sang trái
        for (int i = value.Length - 1; i >= 0; i--)
        {
            char c = value[i];

            // Bỏ qua dấu cách, dấu '-' và mọi ký tự không phải chữ số
            if (!char.IsAsciiDigit(c))
            {
                continue;
            }

            int digit = c - '0'; // đổi ký tự '7' thành số 7

            // Chữ số ở vị trí lẻ (thứ 2, 4, 6... tính từ phải) thì nhân đôi, nếu > 9 thì trừ 9
            if (digitCount % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            digitCount++;
        }

        // Thẻ tín dụng có 13-19 chữ số, và tổng phải chia hết cho 10
        return digitCount >= 13 && digitCount <= 19 && sum % 10 == 0;
    }
}
