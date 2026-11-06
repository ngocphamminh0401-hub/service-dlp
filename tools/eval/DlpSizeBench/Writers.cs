// [Thay đổi] File mới: ghi 1 đoạn text dài (nhiều dòng) ra 7 định dạng. Phần ZIP/XML (docx, xlsx, pptx), PDF (PdfPig + font
// Arial nhúng để có tiếng Việt) và EML (quoted-printable) rút gọn từ tools/tika-bench/DlpTestGen — chỉ giữ thân văn bản,
// không có header/footer/text box (bộ này đo kích thước, không đo độ phủ vị trí).
using System.IO.Compression;
using System.Security;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace DlpSizeBench;

internal static class Writers
{
    public static readonly string[] Formats = ["txt", "csv", "docx", "xlsx", "pptx", "pdf", "eml"];

    public static byte[] Write(string format, IReadOnlyList<string> lines, string title) => format switch
    {
        "txt" => new UTF8Encoding(false).GetBytes(string.Join("\n", lines)),
        "csv" => Csv(lines),
        "docx" => Docx(lines),
        "xlsx" => Xlsx(lines),
        "pptx" => Pptx(lines),
        "pdf" => Pdf(lines),
        "eml" => Eml(lines, title),
        _ => throw new ArgumentException(format)
    };

    private static string X(string s) => SecurityElement.Escape(s) ?? "";
    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    private static byte[] Zip(IEnumerable<(string Name, string Content)> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    // CSV: 2 cột STT, Nội dung (mỗi dòng text 1 hàng), luôn đặt nội dung trong ngoặc kép
    private static byte[] Csv(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder("STT,Noi dung\n");
        for (int i = 0; i < lines.Count; i++) sb.Append(i + 1).Append(",\"").Append(lines[i].Replace("\"", "\"\"")).Append("\"\n");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static byte[] Docx(IReadOnlyList<string> lines)
    {
        var body = new StringBuilder();
        foreach (var l in lines) body.Append("<w:p><w:r><w:t xml:space=\"preserve\">").Append(X(l)).Append("</w:t></w:r></w:p>");
        body.Append("<w:sectPr/>");
        return Zip([
            ("[Content_Types].xml", $"{Xml}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", $"{Xml}<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"{Rel}\"><w:body>{body}</w:body></w:document>")
        ]);
    }

    // XLSX: 1 sheet, cột A = STT, cột B = 1 dòng text (shared strings)
    private static byte[] Xlsx(IReadOnlyList<string> lines)
    {
        var sheet = new StringBuilder($"{Xml}<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        var sst = new StringBuilder();
        for (int r = 0; r < lines.Count; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\"><c r=\"A{r + 1}\"><v>{r + 1}</v></c><c r=\"B{r + 1}\" t=\"s\"><v>{r}</v></c></row>");
            sst.Append("<si><t xml:space=\"preserve\">").Append(X(lines[r])).Append("</t></si>");
        }
        sheet.Append("</sheetData></worksheet>");
        return Zip([
            ("[Content_Types].xml", $"{Xml}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>"),
            ("_rels/.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"{Xml}<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{Rel}\"><sheets><sheet name=\"NoiDung\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>"),
            ("xl/worksheets/sheet1.xml", sheet.ToString()),
            ("xl/sharedStrings.xml", $"{Xml}<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{lines.Count}\" uniqueCount=\"{lines.Count}\">{sst}</sst>")
        ]);
    }

    // PPTX: mỗi slide ~20 dòng trong 1 hộp text (tối giản: master + layout + theme)
    private static byte[] Pptx(IReadOnlyList<string> lines)
    {
        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        string ns = $"xmlns:a=\"{A}\" xmlns:r=\"{Rel}\" xmlns:p=\"{P}\"";
        const string tree0 = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";
        const string clrMap = "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";
        string theme = $"{Xml}<a:theme xmlns:a=\"{A}\" name=\"T\"><a:themeElements><a:clrScheme name=\"c\">" +
            "<a:dk1><a:srgbClr val=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"1F1F1F\"/></a:dk2><a:lt2><a:srgbClr val=\"EEEEEE\"/></a:lt2>" +
            "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>" +
            "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
            "<a:fontScheme name=\"f\"><a:majorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
            "<a:fmtScheme name=\"x\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
            "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
            "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
            "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>";

        var parts = new List<(string, string)>();
        var ct = new StringBuilder(
            "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
            "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
            "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
            "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
        var presRels = new StringBuilder($"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/><Relationship Id=\"rId3\" Type=\"{Rel}/theme\" Target=\"theme/theme1.xml\"/>");
        var sldIds = new StringBuilder();
        const int perSlide = 20;
        int slides = Math.Max(1, (lines.Count + perSlide - 1) / perSlide);
        for (int s = 0; s < slides; s++)
        {
            int no = s + 1;
            var paras = new StringBuilder();
            foreach (var l in lines.Skip(s * perSlide).Take(perSlide))
            {
                paras.Append("<a:p><a:r><a:rPr lang=\"vi-VN\"/><a:t>").Append(X(l)).Append("</a:t></a:r></a:p>");
            }
            parts.Add(($"ppt/slides/slide{no}.xml", $"{Xml}<p:sld {ns}><p:cSld><p:spTree>{tree0}" +
                "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Body\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"300000\" y=\"300000\"/><a:ext cx=\"8500000\" cy=\"6200000\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
                $"<p:txBody><a:bodyPr/><a:lstStyle/>{paras}</p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));
            parts.Add(($"ppt/slides/_rels/slide{no}.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/></Relationships>"));
            ct.Append($"<Override PartName=\"/ppt/slides/slide{no}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
            presRels.Append($"<Relationship Id=\"rId{10 + s}\" Type=\"{Rel}/slide\" Target=\"slides/slide{no}.xml\"/>");
            sldIds.Append($"<p:sldId id=\"{256 + s}\" r:id=\"rId{10 + s}\"/>");
        }
        parts.Insert(0, ("[Content_Types].xml", $"{Xml}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>{ct}</Types>"));
        parts.Add(("_rels/.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>"));
        parts.Add(("ppt/presentation.xml", $"{Xml}<p:presentation {ns}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>{sldIds}</p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>"));
        parts.Add(("ppt/_rels/presentation.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\">{presRels}</Relationships>"));
        parts.Add(("ppt/slideMasters/slideMaster1.xml", $"{Xml}<p:sldMaster {ns}><p:cSld><p:spTree>{tree0}</p:spTree></p:cSld>{clrMap}<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>"));
        parts.Add(("ppt/slideMasters/_rels/slideMaster1.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"../theme/theme1.xml\"/></Relationships>"));
        parts.Add(("ppt/slideLayouts/slideLayout1.xml", $"{Xml}<p:sldLayout {ns} type=\"blank\"><p:cSld name=\"Blank\"><p:spTree>{tree0}</p:spTree></p:cSld></p:sldLayout>"));
        parts.Add(("ppt/slideLayouts/_rels/slideLayout1.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>"));
        parts.Add(("ppt/theme/theme1.xml", theme));
        return Zip(parts);
    }

    // PDF: font Arial nhúng (tiếng Việt); ngắt dòng ~95 ký tự, 52 dòng/trang A4
    private static byte[]? _arial;
    private static byte[] Pdf(IReadOnlyList<string> lines)
    {
        _arial ??= File.ReadAllBytes(@"C:\Windows\Fonts\arial.ttf");
        var builder = new PdfDocumentBuilder();
        var font = builder.AddTrueTypeFont(_arial);
        var wrapped = lines.SelectMany(l => WrapLine(l, 95)).ToList();
        const int perPage = 52;
        for (int p = 0; p * perPage < Math.Max(1, wrapped.Count); p++)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            double y = 805;
            foreach (var l in wrapped.Skip(p * perPage).Take(perPage))
            {
                if (l.Length > 0) page.AddText(l, 9, new PdfPoint(36, y), font);
                y -= 15;
            }
        }
        return builder.Build();
    }

    private static IEnumerable<string> WrapLine(string line, int width)
    {
        if (line.Length <= width) { yield return line; yield break; }
        int start = 0;
        while (start < line.Length)
        {
            int len = Math.Min(width, line.Length - start);
            if (start + len < line.Length)
            {
                int space = line.LastIndexOf(' ', start + len - 1, len);
                if (space > start) len = space - start; // ngắt ở khoảng trắng, không cắt đôi số/từ
            }
            yield return line.Substring(start, len).Trim();
            start += len;
        }
    }

    // EML: thân text/plain UTF-8 quoted-printable
    private static byte[] Eml(IReadOnlyList<string> lines, string title)
    {
        var sb = new StringBuilder();
        sb.Append("From: Phong Hanh chinh <sender@example.com>\r\nTo: receiver@example.com\r\n");
        sb.Append("Subject: =?UTF-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(title))).Append("?=\r\n");
        sb.Append("Date: Mon, 12 Oct 2026 09:00:00 +0700\r\nMIME-Version: 1.0\r\n");
        sb.Append("Content-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n");
        foreach (var line in lines)
        {
            int col = 0;
            foreach (byte b in Encoding.UTF8.GetBytes(line))
            {
                string tok = b is >= 33 and <= 126 && b != '=' || b == ' ' ? ((char)b).ToString() : $"={b:X2}";
                if (col + tok.Length > 70) { sb.Append("=\r\n"); col = 0; }
                sb.Append(tok);
                col += tok.Length;
            }
            sb.Append("\r\n");
        }
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }
}
