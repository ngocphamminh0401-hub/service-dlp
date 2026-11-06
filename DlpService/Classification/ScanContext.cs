using DlpService.Scanning;

namespace DlpService.Classification;

/// <summary>Gói dữ liệu đưa cho mỗi module domain để nó quyết định mức độ nhạy cảm.</summary>
public class ScanContext
{
    /// <summary>Đường dẫn file đang được phân loại.</summary>
    public required string FilePath { get; init; }

    /// <summary>Nội dung file, để module tự tìm thêm từ khoá nếu cần.</summary>
    public required string Content { get; init; }

    /// <summary>Các chỗ RegexScanner đã bắt được trong file này.</summary>
    public required List<ScanMatch> Matches { get; init; }
}
