// [Thay đổi] File mới (mở rộng định dạng - Bước 4): trích xuất text từ file .pdf bằng PdfPig (giấy phép Apache-2.0)
// [Thay đổi] Streaming + giữ cấu trúc (Bước 7): Extract() yield return từng ContentBlock, mỗi trang → 1
// SectionMarker (ContextPath = ["Trang N"]) rồi tới các dòng Paragraph của trang đó kèm SourceIndex = số trang.
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DlpService.Reading.Extractors;

/// <summary>Đọc file .pdf có lớp text (PDF xuất từ Word/Excel, in ra PDF...). PDF scan toàn ảnh không có text —
/// không trả block nào, FileReaderService sẽ ghi cảnh báo (chưa hỗ trợ OCR).</summary>
public class PdfExtractor : IContentExtractor
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    /// <inheritdoc/>
    public long MaxFileSizeBytes => 50 * 1024 * 1024;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(string filePath)
    {
        using var stream = ExtractorHelpers.OpenShared(filePath);
        // PDF có mật khẩu mở file → PdfPig ném exception, FileReaderService bắt và báo "có mật khẩu"
        using var document = PdfDocument.Open(stream);

        // Giữ đúng logic cũ: nếu KHÔNG trang nào có chữ thật (PDF scan toàn ảnh) thì coi cả file là rỗng —
        // kiểm tra trước 1 lượt rồi mới yield, để không trả về nửa chừng rồi mới biết là "rỗng".
        bool hasRealText = document.GetPages().Any(page => page.Letters.Count > 0);
        if (!hasRealText)
        {
            yield break;
        }

        foreach (var page in document.GetPages())
        {
            IReadOnlyList<string> pagePath = [$"Trang {page.Number}"];
            yield return new ContentBlock(BlockKind.SectionMarker, pagePath[0], pagePath);

            // ContentOrderTextExtractor sắp chữ theo THỨ TỰ ĐỌC (trái→phải, trên→dưới, giữ xuống dòng) — page.Text
            // thô có thể dính các từ/dòng vào nhau, làm hỏng proximity (khoảng cách, ranh giới câu).
            string pageText = ContentOrderTextExtractor.GetText(page);
            foreach (var line in pageText.Split(["\r\n", "\n"], StringSplitOptions.None))
            {
                yield return new ContentBlock(BlockKind.Paragraph, line, pagePath, SourceIndex: page.Number);
            }
        }
    }
}
