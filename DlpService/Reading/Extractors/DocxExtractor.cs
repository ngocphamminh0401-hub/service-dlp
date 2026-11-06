// [Thay đổi] File mới (mở rộng định dạng - Bước 2): trích xuất text từ file Word .docx bằng DocumentFormat.OpenXml
// [Thay đổi] Streaming + giữ cấu trúc (Bước 5): Extract() giờ yield return từng ContentBlock thay vì gộp 1 chuỗi.
// Đoạn văn có style "Heading"/"Title" → BlockKind.Heading. Bảng → TableHeader (dòng đầu) + TableRow (kèm
// ColumnNames). Header/footer/footnote/comment gắn ContextPath (vd ["Header"]) thay vì chèn nhãn "[Header]" vào
// text như bản cũ — thông tin "khối này thuộc phần nào" giờ đi kèm dữ liệu, không cần đoán qua text.
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DlpService.Reading.Extractors;

/// <summary>Đọc file .docx: thân văn bản (đoạn + bảng), header/footer, chú thích cuối trang, comment.</summary>
public class DocxExtractor : IContentExtractor
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = [".docx"];

    /// <inheritdoc/>
    public long MaxFileSizeBytes => 50 * 1024 * 1024;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(string filePath)
    {
        ExtractorHelpers.EnsureNotZipBomb(filePath); // [Thay đổi] Bước 12: soi trước, chặn zip bomb

        using var stream = ExtractorHelpers.OpenShared(filePath);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);
        var main = document.MainDocumentPart ?? throw new InvalidDataException("File .docx không có phần nội dung chính");

        if (main.Document?.Body is { } body)
        {
            foreach (var block in AppendBlocks(body, [], depth: 0))
            {
                yield return block;
            }
        }

        // Phần "ẩn" với người đọc lướt nhưng hay chứa dữ liệu nhạy cảm (tên người soạn, số hợp đồng, ghi chú nội bộ)
        foreach (var header in main.HeaderParts.Select(p => p.Header).OfType<W.Header>())
        {
            foreach (var block in AppendBlocks(header, ["Header"]))
            {
                yield return block;
            }
        }
        foreach (var footer in main.FooterParts.Select(p => p.Footer).OfType<W.Footer>())
        {
            foreach (var block in AppendBlocks(footer, ["Footer"]))
            {
                yield return block;
            }
        }
        if (main.FootnotesPart?.Footnotes is { } footnotes)
        {
            foreach (var block in AppendBlocks(footnotes, ["Chú thích"]))
            {
                yield return block;
            }
        }
        if (main.EndnotesPart?.Endnotes is { } endnotes)
        {
            foreach (var block in AppendBlocks(endnotes, ["Chú thích cuối"]))
            {
                yield return block;
            }
        }
        if (main.WordprocessingCommentsPart?.Comments is { } comments)
        {
            foreach (var block in AppendBlocks(comments, ["Comment"]))
            {
                yield return block;
            }
        }
    }

    // [Thay đổi] Chặn đệ quy quá sâu (Bước 11): tham số depth đếm số lớp đã đi vào; file cố tình lồng content
    // control/bảng hàng chục nghìn lớp có thể làm tràn stack (StackOverflowException không bắt được bằng try/catch)
    // — phải chặn TRƯỚC khi đệ quy tới lớp đó, không thể "bắt lỗi rồi bỏ qua" như các lỗi khác.
    private const int MaxDepth = 20;

    // Duyệt các khối theo đúng thứ tự trong tài liệu: đoạn văn → 1 block, bảng → dòng đầu là TableHeader, các dòng
    // sau là TableRow kèm ColumnNames; khối khác (content control, comment, footnote...) → đi sâu vào bên trong.
    private static IEnumerable<ContentBlock> AppendBlocks(OpenXmlElement container, IReadOnlyList<string> contextPath, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException($"File .docx lồng cấu trúc quá sâu (> {MaxDepth} lớp) — nghi ngờ file bất thường");
        }

        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case W.Paragraph paragraph:
                    string line = ParagraphText(paragraph);
                    if (line.Trim().Length > 0)
                    {
                        var kind = IsHeadingStyle(paragraph) ? BlockKind.Heading : BlockKind.Paragraph;
                        yield return new ContentBlock(kind, line, contextPath);
                    }
                    break;

                case W.Table table:
                    IReadOnlyList<string>? columnNames = null;
                    int rowIndex = 0;
                    foreach (var row in table.Elements<W.TableRow>())
                    {
                        var cells = row.Elements<W.TableCell>().Select(cell => CellText(cell, depth + 1)).ToList();
                        string rowText = ExtractorHelpers.JoinRow(cells);
                        if (rowText.Length > 0)
                        {
                            if (columnNames == null)
                            {
                                columnNames = cells; // dòng đầu tiên có dữ liệu của bảng làm tên cột
                                yield return new ContentBlock(BlockKind.TableHeader, rowText, contextPath);
                            }
                            else
                            {
                                yield return new ContentBlock(BlockKind.TableRow, rowText, contextPath, columnNames, rowIndex);
                            }
                        }
                        rowIndex++;
                    }
                    break;

                default:
                    if (child.HasChildren)
                    {
                        foreach (var block in AppendBlocks(child, contextPath, depth + 1))
                        {
                            yield return block;
                        }
                    }
                    break;
            }
        }
    }

    // Đoạn văn có style "Heading1".."Heading9" hoặc "Title" (Word gán style này khi người dùng chọn cấp tiêu đề)
    private static bool IsHeadingStyle(W.Paragraph paragraph)
    {
        string? styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        return styleId != null &&
               (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ||
                styleId.Equals("Title", StringComparison.OrdinalIgnoreCase));
    }

    // Nội dung 1 ô bảng (có thể gồm nhiều đoạn, thậm chí bảng lồng) gộp thành 1 chuỗi — nối các block con bằng " "
    // (ô bảng chỉ cần gộp phẳng, không cần giữ Heading/TableRow lồng bên trong nó)
    private static string CellText(W.TableCell cell, int depth) =>
        string.Join(" ", AppendBlocks(cell, [], depth).Select(b => b.Text));

    // Ghép chữ trong 1 đoạn. Không dùng InnerText vì nó làm dính chữ ở chỗ có tab/xuống dòng ("Họ tên:<tab>Nguyễn"
    // thành "Họ tên:Nguyễn"). Lấy cả DeletedText (chữ đã xoá khi bật Track Changes) — vẫn nằm trong file, vẫn rò được.
    private static string ParagraphText(W.Paragraph paragraph)
    {
        var line = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case W.Text t: line.Append(t.Text); break;
                case W.DeletedText d: line.Append(d.Text); break;
                case W.TabChar: line.Append('\t'); break;
                case W.Break or W.CarriageReturn: line.Append('\n'); break;
            }
        }
        return line.ToString();
    }
}
