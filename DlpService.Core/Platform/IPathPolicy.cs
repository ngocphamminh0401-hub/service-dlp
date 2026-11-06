// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): quy tắc về đường dẫn khác nhau giữa các hệ điều hành — so sánh hoa/thường và
// file tạm nào phải bỏ qua. Trước đây rải rác: EscalationPolicy.IsExcluded (OrdinalIgnoreCase), FileReaderService.IsSupported (chặn "~$"),
// FileWatcherService.PathComparison. Windows/macOS thường không phân biệt hoa/thường; Linux có, và có file tạm khác (.swp, ~, .part...).
namespace DlpService.Platform;

/// <summary>Quy tắc đường dẫn theo hệ điều hành.</summary>
public interface IPathPolicy
{
    /// <summary>Cách so sánh đường dẫn (Windows: không phân biệt hoa/thường; Linux: có).</summary>
    StringComparison Comparison { get; }

    /// <summary>File này là file tạm/khoá của ứng dụng khác (không phải nội dung thật) và phải bị bỏ qua không?
    /// Chỉ xét tên file, không đụng đĩa.</summary>
    bool IsTemporaryFile(string filePath);
}
