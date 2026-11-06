// [Thay đổi] File mới (mở rộng định dạng - Bước 2): trích xuất text từ file PowerPoint .pptx bằng DocumentFormat.OpenXml
// [Thay đổi] Streaming + giữ cấu trúc (Bước 8): Extract() yield return từng ContentBlock. Mỗi slide → ContextPath
// ["Slide N"]; ghi chú người thuyết trình → ContextPath ["Slide N", "Ghi chú"]; bảng trong slide → TableHeader/
// TableRow kèm ColumnNames — thay cho các dòng nhãn "--- Slide N ---"/"[Ghi chú]" chèn thẳng vào text như cũ.
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace DlpService.Reading.Extractors;

/// <summary>Đọc file .pptx: chữ trên từng slide (kể cả slide ẩn, bảng trong slide) và ghi chú người thuyết trình.</summary>
public class PptxExtractor : IContentExtractor
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = [".pptx"];

    /// <inheritdoc/>
    public long MaxFileSizeBytes => 50 * 1024 * 1024;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(string filePath)
    {
        ExtractorHelpers.EnsureNotZipBomb(filePath); // [Thay đổi] Bước 12: soi trước, chặn zip bomb

        using var stream = ExtractorHelpers.OpenShared(filePath);
        using var document = PresentationDocument.Open(stream, isEditable: false);
        var presentation = document.PresentationPart ?? throw new InvalidDataException("File .pptx không có phần trình chiếu");

        var slideIds = presentation.Presentation?.SlideIdList?.Elements<P.SlideId>() ?? [];

        int slideNumber = 0;
        foreach (var slideId in slideIds)
        {
            slideNumber++;
            if (slideId.RelationshipId?.Value is not { } relId || presentation.GetPartById(relId) is not SlidePart slidePart)
            {
                continue;
            }

            IReadOnlyList<string> slidePath = [$"Slide {slideNumber}"];
            if (slidePart.Slide is { } slide)
            {
                foreach (var block in AppendDrawingText(slide, slidePath))
                {
                    yield return block;
                }
            }

            // Ghi chú người thuyết trình: người trình bày thấy, khán giả không thấy — hay chứa số liệu/ghi chú nội bộ
            if (slidePart.NotesSlidePart?.NotesSlide is { } notes)
            {
                IReadOnlyList<string> notesPath = [.. slidePath, "Ghi chú"];
                foreach (var block in AppendDrawingText(notes, notesPath))
                {
                    yield return block;
                }
            }
        }
    }

    // Duyệt theo thứ tự trong slide: bảng → dòng đầu là TableHeader, các dòng sau là TableRow kèm ColumnNames;
    // đoạn chữ ngoài bảng → 1 Paragraph block
    private static IEnumerable<ContentBlock> AppendDrawingText(OpenXmlElement root, IReadOnlyList<string> contextPath)
    {
        foreach (var element in root.Descendants())
        {
            if (element is A.Table table)
            {
                IReadOnlyList<string>? columnNames = null;
                int rowIndex = 0;
                foreach (var row in table.Elements<A.TableRow>())
                {
                    var cells = row.Elements<A.TableCell>()
                        .Select(cell => string.Join(" ", cell.Descendants<A.Paragraph>().Select(ParagraphText)))
                        .ToList();
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
            }
            else if (element is A.Paragraph paragraph && !paragraph.Ancestors<A.Table>().Any())
            {
                // Đoạn nằm TRONG bảng đã được xử lý ở nhánh trên — bỏ qua để không ghi 2 lần
                string line = ParagraphText(paragraph);
                if (line.Trim().Length > 0)
                {
                    yield return new ContentBlock(BlockKind.Paragraph, line, contextPath);
                }
            }
        }
    }

    // Ghép chữ trong 1 đoạn: các run (A.Text, gồm cả chữ trong field như số trang) + xuống dòng mềm
    private static string ParagraphText(A.Paragraph paragraph)
    {
        var line = new StringBuilder();
        foreach (var element in paragraph.Descendants())
        {
            switch (element)
            {
                case A.Text t: line.Append(t.Text); break;
                case A.Break: line.Append('\n'); break;
            }
        }
        return line.ToString();
    }
}
