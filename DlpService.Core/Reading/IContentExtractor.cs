// [Thay đổi] File mới (mở rộng định dạng - Bước 1): "hợp đồng" chung cho mọi bộ trích xuất nội dung theo định dạng file.
// FileReaderService tự tìm mọi class implement interface này qua reflection (giống ClassificationEngine tìm module) —
// thêm định dạng mới chỉ cần thêm 1 file trong Reading/Extractors/, không sửa core.
//
// [Thay đổi] Streaming + giữ cấu trúc (Bước 1): đổi Extract() từ "trả 1 chuỗi duy nhất" sang "trả dần từng
// ContentBlock" (dùng yield return bên trong từng extractor). Lý do:
//   - Streaming: không cần đọc hết file khổng lồ vào RAM rồi mới xử lý, có thể dừng sớm khi đã đủ dữ liệu.
//   - Giữ cấu trúc: mỗi block biết nó là tiêu đề/dòng bảng/đoạn văn, và biết nó thuộc sheet/mục nào — thay vì
//     làm phẳng mọi thứ thành 1 chuỗi text như trước, làm mất khả năng báo cáo "phát hiện ở đâu".
namespace DlpService.Reading;

/// <summary>Loại nội dung của 1 khối trích xuất — giúp downstream biết ý nghĩa của Text, không chỉ nhìn thấy chữ.</summary>
public enum BlockKind
{
    /// <summary>Tiêu đề/tiêu mục (heading trong docx, ô in đậm đầu bảng...).</summary>
    Heading,

    /// <summary>Đoạn văn bản thường.</summary>
    Paragraph,

    /// <summary>Dòng tiêu đề cột của 1 bảng/sheet (dòng đầu csv, header row của Excel...).</summary>
    TableHeader,

    /// <summary>1 dòng dữ liệu trong bảng/sheet.</summary>
    TableRow,

    /// <summary>Đánh dấu ranh giới lớn: bắt đầu 1 sheet, 1 slide, 1 trang PDF... (Text = tên/số thứ tự).</summary>
    SectionMarker
}

/// <summary>1 khối nội dung được trích ra, kèm ngữ cảnh nó thuộc về — để không bị "mất gốc" khi tách nhỏ file lớn
/// thành nhiều khối streaming.</summary>
/// <param name="Kind">Loại khối (tiêu đề, dòng bảng, đoạn văn...).</param>
/// <param name="Text">Nội dung text thật để đem đi quét. Với TableRow: các ô nối bằng " | " (giữ như quy ước cũ).</param>
/// <param name="ContextPath">Breadcrumb ngữ cảnh, ví dụ ["Sheet1"] hoặc ["Chương 2", "2.1 Lương"]; rỗng nếu không có.</param>
/// <param name="ColumnNames">Chỉ có ý nghĩa khi Kind = TableRow: tên cột ứng với từng ô đã tách trong Text; null nếu không xác định được (ví dụ không có header row).</param>
/// <param name="SourceIndex">Số dòng/số trang/số slide trong file gốc, dùng để báo cáo "phát hiện ở đâu"; null nếu không áp dụng.</param>
public sealed record ContentBlock(
    BlockKind Kind,
    string Text,
    IReadOnlyList<string> ContextPath,
    IReadOnlyList<string>? ColumnNames = null,
    int? SourceIndex = null);

/// <summary>Trích xuất nội dung có cấu trúc từ 1 loại file (txt, docx, pdf...), theo kiểu streaming.</summary>
public interface IContentExtractor
{
    /// <summary>Các đuôi file xử lý được, viết thường, có dấu chấm, ví dụ [".xlsx", ".xls"].</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>Kích thước file tối đa (byte) chấp nhận đọc.</summary>
    long MaxFileSizeBytes { get; }

    // [Thay đổi] Tika native chế độ Replace: khi 2 extractor cùng nhận 1 đuôi, extractor có Priority CAO hơn thắng.
    // Mặc định 0 → các extractor cũ không phải sửa gì; FileReaderService không cần biết extractor cụ thể nào.
    /// <summary>Độ ưu tiên khi trùng đuôi file (cao hơn thắng). Mặc định 0.</summary>
    int Priority => 0;

    /// <summary>Đọc file và trả dần từng ContentBlock (dùng yield return), không đọc hết file vào RAM trước.
    /// Được phép ném exception (file hỏng, có mật khẩu...) — FileReaderService sẽ bắt. Vì dùng yield return,
    /// exception có thể chỉ được ném ra khi bên gọi duyệt (foreach) tới phần tử gây lỗi, không phải ngay lúc gọi hàm.</summary>
    /// <param name="filePath">Đường dẫn file cần đọc.</param>
    IEnumerable<ContentBlock> Extract(string filePath);
}
