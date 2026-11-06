// [Thay đổi] File mới (Bậc 3 #5 - placeholder_exclusion): bản "sẵn sàng" của PlaceholderExclusionDefinition, gắn vào SensitivePattern
using System.Text.RegularExpressions;

namespace DlpService.Scanning;

/// <summary>Placeholder exclusion đã sẵn sàng: regex blocklist đã biên dịch + tập giá trị test đã chuẩn hoá (chỉ chữ số).</summary>
public class PlaceholderRule
{
    /// <summary>Regex nhận diện giá trị mẫu (đã bọc ^(?:...)$ nếu JSON khai value_blocklist_full_match).</summary>
    public List<Regex> Blocklist { get; init; } = new();

    /// <summary>Giá trị test công khai, đã chuẩn hoá chỉ còn chữ số.</summary>
    public HashSet<string> KnownTestValues { get; init; } = new();

    // [Thay đổi] S1 giai đoạn 1: penalty theo ngữ cảnh tài liệu (đọc từ doc_context_discount_keywords/factor)
    /// <summary>Từ khoá "tài liệu hướng dẫn/mẫu"; rỗng = không áp penalty.</summary>
    public List<string> DocDiscountKeywords { get; init; } = new();

    /// <summary>Hệ số nhân điểm candidate khi tài liệu có từ khoá trên; 1.0 = không giảm.</summary>
    public double DocDiscountFactor { get; init; } = 1.0;
}
