// [Thay đổi] File mới (bước 5.7 cấu trúc): nhóm từ khoá primary/secondary, đọc được cả dạng mảng cũ và dạng object mới
namespace DlpService.Scanning;

/// <summary>Nhóm từ khoá của 1 rule: primary (chính) và secondary (phụ).</summary>
public class KeywordGroup
{
    public List<string> Primary { get; init; } = new();
    public List<string> Secondary { get; init; } = new();

    /// <summary>Gộp cả 2 nhóm lại, dùng cho logic keyword-required hiện tại (chưa phân biệt primary/secondary).</summary>
    public List<string> All => Primary.Concat(Secondary).ToList();
}
