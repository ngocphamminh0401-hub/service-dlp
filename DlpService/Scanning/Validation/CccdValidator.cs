// [Thay đổi] File mới (cài validator): kiểm tra cấu trúc số CCCD 12 chữ số theo Thông tư 07/2016/TT-BCA
// (sửa đổi bởi Thông tư 59/2021/TT-BCA). Đăng ký trong Validators.ByName với tên "vn_cccd_prefix" (tên trong thiết kế gốc).
namespace DlpService.Scanning.Validation;

/// <summary>Kiểm tra 1 dãy 12 số có đúng cấu trúc số CCCD hay không.</summary>
/// <remarks>
/// Cấu trúc 12 số:  [3 số mã tỉnh nơi khai sinh] [1 số giới tính + thế kỷ] [2 số cuối năm sinh] [6 số ngẫu nhiên]
/// Ví dụ 001 1 82 999069: 001 = Hà Nội, 1 = nữ sinh thế kỷ 20, 82 = năm 1982.
/// Mã giới tính + thế kỷ: 0/1 = nam/nữ 1900-1999, 2/3 = nam/nữ 2000-2099 (4-9 dành cho thế kỷ 22 trở đi — chưa có người
/// nào mang số này nên coi là không hợp lệ).
/// Số ngẫu nhiên hoàn toàn chỉ có khoảng 63/1000 × 4/10 ≈ 2,5% cơ hội lọt qua.
/// </remarks>
public static class CccdValidator
{
    // 63 mã tỉnh/thành (theo địa giới trước sáp nhập 2025 — số CCCD đã cấp KHÔNG đổi khi sáp nhập tỉnh)
    private static readonly HashSet<string> ProvinceCodes =
    [
        "001", "002", "004", "006", "008", "010", "011", "012", "014", "015", "017", "019", "020", "022", "024", "025",
        "026", "027", "030", "031", "033", "034", "035", "036", "037", "038", "040", "042", "044", "045", "046", "048",
        "049", "051", "052", "054", "056", "058", "060", "062", "064", "066", "067", "068", "070", "072", "074", "075",
        "077", "079", "080", "082", "083", "084", "086", "087", "089", "091", "092", "093", "094", "095", "096"
    ];

    /// <summary>true nếu chuỗi có đúng 12 chữ số, mã tỉnh hợp lệ, mã giới tính/thế kỷ 0-3 và năm sinh không ở tương lai.</summary>
    /// <param name="value">Chuỗi regex đã khớp (chỉ các chữ số được xét).</param>
    public static bool IsValid(string value)
    {
        string digits = ValidatorText.DigitsOnly(value);
        if (digits.Length != 12)
        {
            return false;
        }

        // Bước 1: 3 số đầu phải là 1 trong 63 mã tỉnh
        if (!ProvinceCodes.Contains(digits[..3]))
        {
            return false;
        }

        // Bước 2: số thứ 4 cho biết giới tính + thế kỷ sinh
        int genderCentury = digits[3] - '0';
        if (genderCentury > 3)
        {
            return false;
        }
        int century = genderCentury <= 1 ? 1900 : 2000;

        // Bước 3: ghép thế kỷ với 2 số năm sinh → năm sinh đầy đủ, không được lớn hơn năm hiện tại
        int birthYear = century + (digits[4] - '0') * 10 + (digits[5] - '0');
        return birthYear <= DateTime.Now.Year;
    }
}
