// [Thay đổi] Tối ưu B3 (Vòng 4b): đọc THÂN file .docx kiểu SAX (XmlReader) thay vì nạp cả cây DOM bằng OpenXml SDK.
// Lý do: docx có XML 20 MB làm RSS tăng ~+176 MB cho MỖI file (đo 19/11/2026: RSS đỉnh 235 MB so với nền 59 MB) — 2 worker cùng đọc 2 file như vậy
// vượt chỉ tiêu RAM 300 MB. XmlReader chỉ giữ 1 đoạn văn trong bộ nhớ.
// YÊU CẦU: kết quả GIỐNG HỆT bản DOM cũ (cùng ContentBlock, cùng thứ tự, cùng lỗi lồng quá sâu) — bản DOM cũ được giữ trong project test
// (DocxDomReference) và so từng block trên bộ docx thật. Header/footer/chú thích (nhỏ) vẫn đọc bằng DOM như cũ.
using System.Text;
using System.Xml;

namespace DlpService.Reading.Extractors;

/// <summary>Đọc thân (w:body) của document.xml theo kiểu luồng, trả ContentBlock giống DocxExtractor bản DOM.</summary>
internal static class DocxBodyStreamReader
{
    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // Giống DocxExtractor.MaxDepth: chặn file lồng sâu bất thường
    private const int MaxDepth = 20;

    /// <summary>Tạo XmlReader an toàn (cấm DTD/XXE) trên stream của phần document.xml.</summary>
    public static XmlReader CreateReader(Stream partStream) => XmlReader.Create(partStream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = true,
    });

    /// <summary>Duyệt w:document/w:body (chỉ w:body đầu tiên, như <c>Document.Body</c> của SDK).</summary>
    public static IEnumerable<ContentBlock> ReadBody(XmlReader reader)
    {
        reader.MoveToContent();
        if (!IsW(reader, "document") || reader.IsEmptyElement)
        {
            yield break;
        }
        int d = reader.Depth;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == d))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == d + 1 && IsW(reader, "body"))
            {
                foreach (var block in Container(reader, [], depth: 0, checkAtEntry: true))
                {
                    yield return block;
                }
                yield break; // chỉ body đầu tiên
            }
            reader.Read();
        }
    }

    private static bool IsW(XmlReader r, string localName) =>
        r.NodeType == XmlNodeType.Element && r.LocalName == localName && r.NamespaceURI == WordNs;

    // Tương đương DocxExtractor.AppendBlocks. Reader đứng ở thẻ MỞ của container; khi xong, đứng ở thẻ ĐÓNG (hoặc chính thẻ nếu rỗng).
    // checkAtEntry=true: kiểm độ sâu ngay (như AppendBlocks kiểm đầu hàm); false: kiểm khi gặp phần tử con đầu tiên (như "chỉ gọi khi HasChildren").
    private static IEnumerable<ContentBlock> Container(XmlReader r, IReadOnlyList<string> path, int depth, bool checkAtEntry)
    {
        if (checkAtEntry)
        {
            CheckDepth(depth);
        }
        if (r.IsEmptyElement)
        {
            yield break;
        }
        bool depthChecked = checkAtEntry;
        int d = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == d))
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (!depthChecked)
                {
                    CheckDepth(depth);
                    depthChecked = true;
                }

                if (IsW(r, "p"))
                {
                    var (line, styleId) = ReadParagraph(r);
                    if (line.Trim().Length > 0)
                    {
                        bool heading = styleId != null &&
                            (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || styleId.Equals("Title", StringComparison.OrdinalIgnoreCase));
                        yield return new ContentBlock(heading ? BlockKind.Heading : BlockKind.Paragraph, line, path);
                    }
                }
                else if (IsW(r, "tbl"))
                {
                    foreach (var block in Table(r, path, depth))
                    {
                        yield return block;
                    }
                }
                else
                {
                    foreach (var block in Container(r, path, depth + 1, checkAtEntry: false))
                    {
                        yield return block;
                    }
                }
            }
            r.Read();
        }
    }

    private static void CheckDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException($"File .docx lồng cấu trúc quá sâu (> {MaxDepth} lớp) — nghi ngờ file bất thường");
        }
    }

    // Bảng: dòng đầu có dữ liệu = TableHeader, các dòng sau = TableRow kèm ColumnNames; chỉ xét w:tr là con trực tiếp, w:tc là con trực tiếp của w:tr
    private static IEnumerable<ContentBlock> Table(XmlReader r, IReadOnlyList<string> path, int depth)
    {
        if (r.IsEmptyElement)
        {
            yield break;
        }
        IReadOnlyList<string>? columnNames = null;
        int rowIndex = 0;
        int d = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == d))
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (IsW(r, "tr") && r.Depth == d + 1)
                {
                    var cells = ReadRowCells(r, depth);
                    string rowText = ExtractorHelpers.JoinRow(cells);
                    if (rowText.Length > 0)
                    {
                        if (columnNames == null)
                        {
                            columnNames = cells;
                            yield return new ContentBlock(BlockKind.TableHeader, rowText, path);
                        }
                        else
                        {
                            yield return new ContentBlock(BlockKind.TableRow, rowText, path, columnNames, rowIndex);
                        }
                    }
                    rowIndex++;
                }
                else
                {
                    SkipToEnd(r);
                }
            }
            r.Read();
        }
    }

    private static List<string> ReadRowCells(XmlReader r, int depth)
    {
        var cells = new List<string>();
        if (r.IsEmptyElement)
        {
            return cells;
        }
        int d = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == d))
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                if (IsW(r, "tc") && r.Depth == d + 1)
                {
                    // Ô: gộp phẳng mọi khối con bằng " " (như CellText); độ sâu +1 và kiểm ngay ở đầu
                    cells.Add(string.Join(" ", Container(r, [], depth + 1, checkAtEntry: true).Select(b => b.Text)));
                }
                else
                {
                    SkipToEnd(r);
                }
            }
            r.Read();
        }
        return cells;
    }

    // Ghép chữ 1 đoạn (như ParagraphText): w:t, w:delText, w:tab (KHÔNG phải tab stop trong w:tabs), w:br, w:cr — theo thứ tự tài liệu, mọi cấp con
    // (kể cả hộp văn bản lồng, giống Descendants() của bản DOM). Đồng thời lấy styleId của w:pPr/w:pStyle đầu tiên (con trực tiếp).
    private static (string Line, string? StyleId) ReadParagraph(XmlReader r)
    {
        var line = new StringBuilder();
        string? styleId = null;
        bool seenPPr = false;
        if (!r.IsEmptyElement)
        {
            int d = r.Depth;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == d))
            {
                if (r.NodeType == XmlNodeType.Element && r.NamespaceURI == WordNs)
                {
                    switch (r.LocalName)
                    {
                        case "pPr" when r.Depth == d + 1 && !seenPPr:
                            seenPPr = true;
                            styleId = ReadPStyle(r);
                            break;
                        case "tabs":
                            SkipToEnd(r); // w:tab bên trong là điểm dừng tab (TabStop), không phải ký tự Tab
                            break;
                        case "t":
                        case "delText":
                            line.Append(ReadLeafText(r));
                            break;
                        case "tab":
                            line.Append('\t');
                            break;
                        case "br":
                        case "cr":
                            line.Append('\n');
                            break;
                    }
                }
                r.Read();
            }
        }
        return (line.ToString(), styleId);
    }

    // Đứng ở w:pPr; trả val của w:pStyle đầu tiên (con trực tiếp), rồi đứng ở thẻ đóng w:pPr
    private static string? ReadPStyle(XmlReader r)
    {
        string? val = null;
        bool found = false;
        if (!r.IsEmptyElement)
        {
            int d = r.Depth;
            r.Read();
            while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == d))
            {
                if (!found && IsW(r, "pStyle") && r.Depth == d + 1)
                {
                    found = true;
                    val = r.GetAttribute("val", WordNs);
                }
                r.Read();
            }
        }
        return val;
    }

    // Đứng ở w:t / w:delText; trả nội dung chữ (gồm cả khoảng trắng), rồi đứng ở thẻ đóng
    private static string ReadLeafText(XmlReader r)
    {
        if (r.IsEmptyElement)
        {
            return string.Empty;
        }
        var text = new StringBuilder();
        int d = r.Depth;
        r.Read();
        while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == d))
        {
            switch (r.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    text.Append(r.Value);
                    break;
                case XmlNodeType.Element:
                    SkipToEnd(r);
                    break;
            }
            r.Read();
        }
        return text.ToString();
    }

    // Bỏ qua cả cây con; reader đứng ở thẻ đóng của phần tử (hoặc chính nó nếu rỗng)
    private static void SkipToEnd(XmlReader r)
    {
        if (r.IsEmptyElement)
        {
            return;
        }
        int d = r.Depth;
        while (r.Read() && !(r.NodeType == XmlNodeType.EndElement && r.Depth == d))
        {
        }
    }
}
