// [Thay đổi] File mới (cài validator): chỉ giữ email CÁ NHÂN — loại email sai cấu trúc và hộp thư chung của tổ chức
// (tuyendung@, hotro@, phongnhansu@...). Đăng ký trong Validators.ByName với tên "email_personal".
// Lý do: baseline 28/09 cho thấy email_001 có mặt trong 83% file PUBLIC bị xếp nhầm Confidential/Restricted, và email
// trong tài liệu public gần như toàn là hộp thư chung — không phải dữ liệu cá nhân.
namespace DlpService.Scanning.Validation;

/// <summary>Kiểm tra 1 email có đúng cấu trúc và có phải hộp thư của 1 cá nhân hay không.</summary>
/// <remarks>
/// Phần trước '@' (local-part) được so với 3 danh sách:
///   1. Từ chung DỄ TRÙNG tên/viết tắt (pr, hr, tuvan...) → chỉ tính khi khớp NGUYÊN VẸN, hoặc là từ đầu tiên
///      trước dấu phân cách ("hr.hanoi").
///   2. Từ chung AN TOÀN (tuyendung, hotro, info...) → tính khi local-part BẮT ĐẦU bằng từ đó ("hotrokhachhang").
///   3. Tiền tố phòng ban (phong, ban, bophan...) + tên phòng ban (nhansu, ketoan...) → "phongnhansu", "banquanly".
/// Không dùng "tiền tố phòng ban" đứng một mình vì trùng tên người Việt: "phong.le", "khoi.nguyen" là email cá nhân.
/// </remarks>
public static class EmailValidator
{
    // Dễ trùng tên người/viết tắt → chỉ so khớp nguyên vẹn
    private static readonly HashSet<string> ExactRoleWords =
    [
        "pr", "ir", "hr", "it", "qa", "cs", "all", "team", "staff", "office", "tuvan", "nhansu", "ketoan", "phaply",
        "legal", "research", "nhanvien", "allstaff", "tatcanhanvien", "toancongty", "toasoan", "sanpham", "vieclam",
        "daotao", "truyenthong", "hanhchinh"
    ];

    // Không trùng tên người Việt → so khớp phần đầu local-part
    private static readonly string[] PrefixRoleWords =
    [
        "info", "contact", "lienhe", "hotro", "support", "cskh", "chamsockhachhang", "noreply", "donotreply", "sales",
        "marketing", "tuyendung", "recruit", "careers", "jobs", "admin", "webmaster", "postmaster", "press", "baochi",
        "media", "hello", "feedback", "gopy", "customer", "service", "dichvu", "khachhang", "thongbao", "newsletter",
        "billing", "invoice", "hoadon", "accounting", "finance", "taichinh", "compliance", "privacy", "dpo", "security",
        "baomat", "helpdesk", "tongdai", "hotline", "investor", "quanhecodong", "tapchi"
    ];

    private static readonly string[] DepartmentPrefixes = ["phong", "bophan", "ban", "trungtam", "vanphong", "khoi", "to", "chinhanh", "vp"];

    private static readonly string[] DepartmentWords =
    [
        "nhansu", "hanhchinh", "ketoan", "taichinh", "kinhdoanh", "truyenthong", "phaply", "marketing", "cntt", "it",
        "dichvu", "khachhang", "quanly", "tonghop", "doingoai", "dautu", "kythuat", "kiemsoat", "ruiro", "tindung",
        "nguonvon", "daotao", "bientap", "giamdoc", "thuky", "tuyendung", "chatluong", "sanpham", "hotro", "yte"
    ];

    /// <summary>true nếu email đúng cấu trúc VÀ không phải hộp thư chung của tổ chức.</summary>
    /// <param name="value">Địa chỉ email regex đã khớp.</param>
    public static bool IsPersonal(string value)
    {
        int at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1)
        {
            return false;
        }

        string local = value[..at];
        string domain = value[(at + 1)..];
        return IsWellFormed(local, domain) && !IsRoleMailbox(local);
    }

    /// <summary>true nếu local-part là hộp thư chung (tuyendung, hotro, phongnhansu, hr.hanoi...).</summary>
    public static bool IsRoleMailbox(string local)
    {
        string lower = local.ToLowerInvariant();

        // "compact" = chỉ giữ chữ cái: "phong.nhan-su2024" → "phongnhansu"
        string compact = new(lower.Where(char.IsAsciiLetter).ToArray());
        if (compact.Length == 0)
        {
            return false; // toàn số → không đủ căn cứ nói là hộp thư chung
        }

        // Danh sách 1: khớp nguyên vẹn, hoặc là từ đầu tiên trước dấu phân cách ("hr.hanoi", "pr2024")
        string firstToken = new(lower.Split(['.', '_', '-', '+'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Where(char.IsAsciiLetter).ToArray() ?? []);
        if (ExactRoleWords.Contains(compact) || ExactRoleWords.Contains(firstToken))
        {
            return true;
        }

        // Danh sách 2: bắt đầu bằng từ chung an toàn
        if (PrefixRoleWords.Any(word => compact.StartsWith(word, StringComparison.Ordinal)))
        {
            return true;
        }

        // Danh sách 3: tiền tố phòng ban + tên phòng ban
        foreach (var prefix in DepartmentPrefixes)
        {
            if (compact.StartsWith(prefix, StringComparison.Ordinal)
                && DepartmentWords.Any(word => compact.AsSpan(prefix.Length).StartsWith(word, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    // Cấu trúc tối thiểu: local-part không bắt đầu/kết thúc bằng '.', không có ".."; mỗi phần tên miền không rỗng
    // và không bắt đầu/kết thúc bằng '-'
    private static bool IsWellFormed(string local, string domain)
    {
        if (local.StartsWith('.') || local.EndsWith('.') || local.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return domain.Split('.').All(label => label.Length > 0 && !label.StartsWith('-') && !label.EndsWith('-'));
    }
}
