// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): quy tắc đường dẫn của Windows — đúng hành vi hiện tại của service, nên đây cũng là
// mặc định cho mọi nơi gọi cũ (test, tool đều chạy trên Windows). Thuần logic chuỗi, không gọi API Windows nào.
// Host Linux sẽ có CaseSensitivePathPolicy riêng (Ordinal + file tạm .swp/~/.part...) ở giai đoạn Linux.
namespace DlpService.Platform;

/// <summary>Đường dẫn không phân biệt hoa/thường; bỏ qua file khoá tạm của Office ("~$baocao.docx").</summary>
public sealed class CaseInsensitivePathPolicy : IPathPolicy
{
    /// <inheritdoc/>
    public StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    /// <inheritdoc/>
    public bool IsTemporaryFile(string filePath)
    {
        // Office tạo file "~$tên.docx" (vài trăm byte, chỉ chứa tên người đang mở) khi mở tài liệu — không phải nội dung thật,
        // đọc sẽ lỗi định dạng và làm rác log
        return Path.GetFileName(filePath).StartsWith("~$", StringComparison.Ordinal);
    }
}
