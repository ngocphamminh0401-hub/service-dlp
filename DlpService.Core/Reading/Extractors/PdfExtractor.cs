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
        // [Thay đổi] M6: mở file rồi dùng chung đường đọc Stream (attachment trong email đi CÙNG đường này) → hành vi file thường không đổi
        using var stream = ExtractorHelpers.OpenShared(filePath);
        foreach (var block in Extract(stream, Path.GetFileName(filePath)))
        {
            yield return block;
        }
    }

    // [Thay đổi] M6: đọc từ Stream (attachment email giải mã trong bộ nhớ — KHÔNG ghi file tạm ra đĩa). Stream phải seek được.
    /// <inheritdoc/>
    public bool SupportsStream => true;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(Stream stream, string fileName)
    {
        // PDF có mật khẩu mở file → PdfPig ném exception, FileReaderService bắt và báo "có mật khẩu"
        using var document = PdfDocument.Open(stream);

        // [Thay đổi] H3 + L1: bỏ lượt duyệt trước "GetPages().Any(...)" (duyệt mọi trang 2 lần), giờ chỉ 1 vòng. Yêu cầu giữ nguyên:
        //  (a) PDF không trang nào có chữ (scan toàn ảnh) → KHÔNG trả khối chữ nào → FileReaderService ra "cần OCR" như trước;
        //  (b) PDF có chữ → chuỗi text Y HỆT trước (trang không chữ vẫn có SectionMarker + dòng rỗng) → file_hash/offset không đổi
        //      (đối chiếu 320 file: nếu bỏ hẳn trang trắng thì 3 PDF đổi hash);
        // và thêm mới:
        //  (c) trang KHÔNG có chữ nhưng CÓ ảnh (trang scan) → chèn thêm 1 khối Unreadable (FileReaderService không nối khối này vào text)
        //      — trước đây trang này bị bỏ qua im lặng, PDF "1 trang bìa có chữ + 9 trang scan hợp đồng" bị kết luận Public/final.
        //      Trang trắng (không chữ, không ảnh) không tính là thiếu.
        // Để làm (a) mà vẫn 1 lượt: các trang KHÔNG chữ ở ĐẦU tài liệu được giữ tạm trong `leading` (chỉ vài khối nhỏ); gặp trang có chữ
        // đầu tiên thì phát hết ra rồi stream bình thường; hết tài liệu mà chưa gặp chữ → bỏ `leading`, giống "yield break" cũ.
        bool anyText = false;
        var leading = new List<ContentBlock>();
        foreach (var page in document.GetPages())
        {
            bool hasText = page.Letters.Count > 0;
            var blocks = PageBlocks(page, hasText);

            if (!anyText && !hasText)
            {
                leading.AddRange(blocks); // chưa biết tài liệu có chữ hay không → giữ tạm
                continue;
            }
            if (!anyText)
            {
                anyText = true;
                foreach (var b in leading) yield return b;
                leading.Clear();
            }
            foreach (var b in blocks) yield return b;
        }
    }

    // [Thay đổi] H3: các khối của 1 trang — đúng thứ tự cũ (SectionMarker rồi từng dòng); trang scan có thêm Unreadable ở đầu
    private static List<ContentBlock> PageBlocks(UglyToad.PdfPig.Content.Page page, bool hasText)
    {
        var blocks = new List<ContentBlock>();
        IReadOnlyList<string> pagePath = [$"Trang {page.Number}"];
        if (!hasText && page.GetImages().Any())
        {
            blocks.Add(new ContentBlock(BlockKind.Unreadable, pagePath[0], pagePath, SourceIndex: page.Number));
        }
        blocks.Add(new ContentBlock(BlockKind.SectionMarker, pagePath[0], pagePath));

        // ContentOrderTextExtractor sắp chữ theo THỨ TỰ ĐỌC (trái→phải, trên→dưới, giữ xuống dòng) — page.Text
        // thô có thể dính các từ/dòng vào nhau, làm hỏng proximity (khoảng cách, ranh giới câu).
        string pageText = ContentOrderTextExtractor.GetText(page);
        foreach (var line in pageText.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            blocks.Add(new ContentBlock(BlockKind.Paragraph, line, pagePath, SourceIndex: page.Number));
        }
        return blocks;
    }
}
