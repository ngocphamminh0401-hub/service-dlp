// [Thay đổi] File mới (cài validator): kiểm tra số điện thoại Việt Nam theo quy hoạch đầu số/mã vùng hiện hành và loại số
// giả kiểu ví dụ (0900000000, 0912345678). Đăng ký trong Validators.ByName với tên "vn_phone".
namespace DlpService.Scanning.Validation;

/// <summary>Kiểm tra số di động (10 số) hoặc cố định (11 số) Việt Nam.</summary>
/// <remarks>
///   1. Chuẩn hoá: bỏ ký tự không phải số; "+84"/"84" ở đầu đổi thành "0" (+84912... → 0912...).
///   2. Di động: đúng 10 số, 3 số đầu thuộc danh sách đầu số nhà mạng đang cấp.
///      Cố định:  đúng 11 số, bắt đầu bằng 024 (Hà Nội), 028 (TP.HCM) hoặc 02xx là mã vùng tỉnh hợp lệ.
///   3. Loại số giả: 7 số thuê bao cuối giống hệt nhau (0900000000) hoặc tăng/giảm đều (0912345678, 0987654321) —
///      đây là số hay dùng làm VÍ DỤ trong tài liệu, gần như không phải số của người thật.
/// Đây là BỘ LỌC (regex đã giới hạn đầu số từ trước), không phải checksum, nên không tính là bằng chứng cho level_gate.
/// </remarks>
public static class PhoneValidator
{
    // Viettel 032-039/086/096-098; Mobifone 070/076-079/089/090/093; Vinaphone 081-085/088/091/094;
    // Vietnamobile 052/056/058/092; Gmobile 059/099; Reddi 055; Itel 087
    private static readonly HashSet<string> MobilePrefixes =
    [
        "032", "033", "034", "035", "036", "037", "038", "039", "086", "096", "097", "098",
        "070", "076", "077", "078", "079", "089", "090", "093",
        "081", "082", "083", "084", "085", "088", "091", "094",
        "052", "056", "058", "092", "059", "099", "055", "087"
    ];

    // Mã vùng cố định 4 số (0 + 2xx) của 61 tỉnh; Hà Nội (024) và TP.HCM (028) chỉ 3 số, xét riêng
    private static readonly HashSet<string> LandlineAreaCodes =
    [
        "0203", "0204", "0205", "0206", "0207", "0208", "0209", "0210", "0211", "0212", "0213", "0214", "0215", "0216",
        "0218", "0219", "0220", "0221", "0222", "0225", "0226", "0227", "0228", "0229", "0232", "0233", "0234", "0235",
        "0236", "0237", "0238", "0239", "0251", "0252", "0254", "0255", "0256", "0257", "0258", "0259", "0260", "0261",
        "0262", "0263", "0269", "0270", "0271", "0272", "0273", "0274", "0275", "0276", "0277", "0290", "0291", "0292",
        "0293", "0294", "0296", "0297", "0299"
    ];

    /// <summary>true nếu là số di động/cố định VN hợp lệ và không phải số giả kiểu ví dụ.</summary>
    /// <param name="value">Số điện thoại regex đã khớp (0912..., +84912..., 84912..., 024...).</param>
    public static bool IsValid(string value)
    {
        string digits = ValidatorText.DigitsOnly(value);

        // Bước 1: 84xxxxxxxxx (11 số di động) / 84xxxxxxxxxx (12 số cố định) → 0xxxxxxxxx
        if (digits.StartsWith("84", StringComparison.Ordinal) && (digits.Length == 11 || digits.Length == 12))
        {
            digits = "0" + digits[2..];
        }

        // Bước 2 + 3
        if (digits.Length == 10 && MobilePrefixes.Contains(digits[..3]))
        {
            return !LooksFake(digits[3..]);
        }

        if (digits.Length == 11
            && (digits.StartsWith("024", StringComparison.Ordinal) || digits.StartsWith("028", StringComparison.Ordinal)
                || LandlineAreaCodes.Contains(digits[..4])))
        {
            return !LooksFake(digits[^7..]);
        }

        return false;
    }

    // 7 số thuê bao giống hệt nhau, hoặc mỗi số hơn/kém số trước đúng 1 (tăng/giảm đều)
    private static bool LooksFake(string subscriber)
    {
        bool allSame = true, ascending = true, descending = true;
        for (int i = 1; i < subscriber.Length; i++)
        {
            int diff = subscriber[i] - subscriber[i - 1];
            allSame &= diff == 0;
            ascending &= diff == 1;
            descending &= diff == -1;
        }
        return allSame || ascending || descending;
    }
}
