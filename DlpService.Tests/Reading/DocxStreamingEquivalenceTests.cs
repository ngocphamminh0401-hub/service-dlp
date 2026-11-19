// [Thay đổi] File mới (Vòng 4b, tối ưu B3): bản SAX của DocxExtractor (thân văn bản) phải cho KẾT QUẢ GIỐNG HỆT bản DOM cũ (DocxDomReference):
// cùng ContentBlock (loại, chữ, đường dẫn, tên cột, số thứ tự), cùng thứ tự, và cùng lỗi khi lồng quá sâu.
// Ngoài các ca tổng hợp bên dưới, có test đối chiếu trên bộ docx thật: đặt biến môi trường DLP_DOCX_CORPUS=<thư mục chứa .docx> rồi chạy test.
using System.IO.Compression;
using System.Text;
using DlpService.Reading;
using DlpService.Reading.Extractors;

namespace DlpService.Tests.Reading;

public class DocxStreamingEquivalenceTests
{
    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    private static byte[] BuildDocx(string bodyXml, string extraNamespaces = "")
    {
        string document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {W} {extraNamespaces}><w:body>{bodyXml}</w:body></w:document>";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/document.xml", document);
        }
        return ms.ToArray();
    }

    private sealed record Snap(BlockKind Kind, string Text, string Path, string? Columns, int? Index);

    private static (List<Snap> Blocks, string? Error) Run(IContentExtractor extractor, byte[] docx)
    {
        var list = new List<Snap>();
        try
        {
            foreach (var b in extractor.Extract(new MemoryStream(docx), "t.docx"))
            {
                list.Add(new Snap(b.Kind, b.Text, string.Join(">", b.ContextPath), b.ColumnNames is null ? null : string.Join("|", b.ColumnNames), b.SourceIndex));
            }
            return (list, null);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("lồng cấu trúc quá sâu"))
        {
            return (list, "too-deep");
        }
        catch (ExtractionLimitException)
        {
            return (list, "zip-limit"); // vượt ngân sách giải nén (VD docx XML 30 MB) — cả hai bản phải cùng ném
        }
    }

    private static void AssertSame(byte[] docx, string label)
    {
        var expected = Run(new DocxDomReference(), docx);
        var actual = Run(new DocxExtractor(), docx);
        Assert.True(expected.Error == actual.Error, $"{label}: lỗi khác nhau (DOM={expected.Error ?? "không"}, SAX={actual.Error ?? "không"})");
        Assert.True(expected.Blocks.Count == actual.Blocks.Count, $"{label}: số khối khác nhau (DOM={expected.Blocks.Count}, SAX={actual.Blocks.Count})");
        for (int i = 0; i < expected.Blocks.Count; i++)
        {
            Assert.True(expected.Blocks[i] == actual.Blocks[i], $"{label}: khối {i} khác nhau\nDOM: {expected.Blocks[i]}\nSAX: {actual.Blocks[i]}");
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "doan-van-don", "<w:p><w:r><w:t>Số CCCD: 012345678901</w:t></w:r></w:p>" };
        yield return new object[] { "heading-va-title", "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Tiêu đề</w:t></w:r></w:p><w:p><w:pPr><w:pStyle w:val=\"title\"/></w:pPr><w:r><w:t>T</w:t></w:r></w:p><w:p><w:pPr><w:pStyle w:val=\"Normal\"/></w:pPr><w:r><w:t>N</w:t></w:r></w:p>" };
        yield return new object[] { "doan-trong-va-chi-khoang-trang", "<w:p/><w:p><w:r><w:t xml:space=\"preserve\">   </w:t></w:r></w:p><w:p><w:r><w:t> </w:t></w:r></w:p><w:p><w:r><w:t/></w:r></w:p>" };
        yield return new object[] { "khoang-trang-dau-cuoi", "<w:p><w:r><w:t xml:space=\"preserve\"> a </w:t></w:r><w:r><w:t xml:space=\"preserve\"> b</w:t></w:r></w:p><w:p><w:r><w:t> khong preserve </w:t></w:r></w:p>" };
        yield return new object[] { "tab-br-cr-delText", "<w:p><w:r><w:t>Họ tên:</w:t><w:tab/><w:t>Nguyễn</w:t><w:br/><w:t>A</w:t><w:cr/><w:t>B</w:t></w:r><w:del><w:r><w:delText>đã xoá</w:delText></w:r></w:del></w:p>" };
        yield return new object[] { "tab-stop-khong-phai-ky-tu-tab", "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:t>x</w:t><w:tab/><w:t>y</w:t></w:r></w:p>" };
        yield return new object[] { "entity-cdata", "<w:p><w:r><w:t>A &amp; B &lt;C&gt; &#x1F600; é</w:t></w:r></w:p>" };
        yield return new object[] { "bang-co-header-va-hang-trong", "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Tên</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>CCCD</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:p/></w:tc><w:tc><w:p/></w:tc></w:tr><w:tr><w:tc><w:p><w:r><w:t>An</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>012345678901</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" };
        yield return new object[] { "bang-long-nhau", "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Ngoài</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>Trong1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Trong2</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:p><w:r><w:t>T3</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:tc></w:tr></w:tbl>" };
        yield return new object[] { "bang-hang-boc-trong-sdt-khong-duoc-tinh", "<w:tbl><w:sdt><w:sdtContent><w:tr><w:tc><w:p><w:r><w:t>An</w:t></w:r></w:p></w:tc></w:tr></w:sdtContent></w:sdt><w:tr><w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>" };
        yield return new object[] { "content-control-khoi-va-hyperlink", "<w:sdt><w:sdtPr><w:alias w:val=\"x\"/></w:sdtPr><w:sdtContent><w:p><w:r><w:t>Trong sdt</w:t></w:r></w:p><w:p><w:hyperlink><w:r><w:t>Link</w:t></w:r></w:hyperlink></w:p></w:sdtContent></w:sdt>" };
        yield return new object[] { "alternate-content", "<w:p><w:r><mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"wps\"><w:t>Choice</w:t></mc:Choice><mc:Fallback><w:t>Fallback</w:t></mc:Fallback></mc:AlternateContent></w:r></w:p>" };
        yield return new object[] { "pPr-khong-phai-con-dau-va-pStyle-thu-hai", "<w:p><w:r><w:t>a</w:t></w:r><w:pPr><w:pStyle w:val=\"Heading2\"/></w:pPr></w:p><w:p><w:pPr><w:pStyle w:val=\"Normal\"/><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>b</w:t></w:r></w:p>" };
        yield return new object[] { "long-sau-vua-du-20-lop", Nest(20, "<w:p><w:r><w:t>Sâu vừa đủ</w:t></w:r></w:p>") };
        yield return new object[] { "long-qua-sau-21-lop", Nest(21, "<w:p><w:r><w:t>Quá sâu</w:t></w:r></w:p>") };
        yield return new object[] { "long-qua-sau-chi-co-la-van-ban", Nest(25, "chữ trần") };
        yield return new object[] { "bang-trong-cac-lop-sdt-sau", Nest(19, "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>X</w:t></w:r></w:p></w:tc></w:tr></w:tbl>") };
        yield return new object[] { "bang-qua-sau-o-trong", Nest(20, "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>X</w:t></w:r></w:p></w:tc></w:tr></w:tbl>") };
        yield return new object[] { "tai-lieu-rong", "" };
    }

    // KHÁC BIỆT CÓ CHỦ ĐÍCH: OpenXml SDK (bản DOM cũ) không nạp vào cây những phần tử nó không biết (phần tử ngoài lược đồ như <foo:Khac>, hoặc
    // w:txbxContent đặt trực tiếp dưới w:pict) nên chữ trong đó bị bỏ qua IM LẶNG. Bản SAX đọc được chữ đó → quét NHIỀU hơn, không bao giờ ít hơn.
    // Trên 12.800 docx thật không có khác biệt nào (xem BoDocxThat_GiongBanDomCu), nên chỉ ghi nhận hành vi, không ép giống hệt.
    [Fact]
    public void PhanTuNgoaiLuocDo_BanSaxDocDuocChuMaDomBoQua()
    {
        var foo = BuildDocx("<foo:Khac xmlns:foo=\"urn:foo\"><w:p><w:r><w:t>Trong phần tử lạ</w:t></w:r></w:p></foo:Khac>");
        Assert.Empty(Run(new DocxDomReference(), foo).Blocks);
        Assert.Equal(["Trong phần tử lạ"], Run(new DocxExtractor(), foo).Blocks.Select(b => b.Text));

        var pict = BuildDocx("<w:p><w:r><w:t>Ngoài </w:t></w:r><w:r><w:pict><w:txbxContent><w:p><w:r><w:t>Trong hộp</w:t></w:r></w:p></w:txbxContent></w:pict></w:r><w:r><w:t> hết</w:t></w:r></w:p>");
        Assert.Equal("Ngoài  hết", Run(new DocxDomReference(), pict).Blocks.Single().Text);
        Assert.Equal("Ngoài Trong hộp hết", Run(new DocxExtractor(), pict).Blocks.Single().Text);
    }

    private static string Nest(int layers, string inner)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < layers; i++) sb.Append("<w:sdt><w:sdtContent>");
        sb.Append(inner);
        for (int i = 0; i < layers; i++) sb.Append("</w:sdtContent></w:sdt>");
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ThanVanBan_GiongBanDomCu(string label, string bodyXml)
    {
        AssertSame(BuildDocx(bodyXml), label);
    }

    [Fact]
    public void BoDocxThat_GiongBanDomCu()
    {
        // Chỉ chạy khi đặt DLP_DOCX_CORPUS=<thư mục>; bỏ qua (pass) khi không đặt, để dotnet test bình thường không phụ thuộc dữ liệu ngoài
        string? dir = Environment.GetEnvironmentVariable("DLP_DOCX_CORPUS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }
        int checkedCount = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.docx", SearchOption.AllDirectories))
        {
            AssertSame(File.ReadAllBytes(file), Path.GetRelativePath(dir, file));
            checkedCount++;
        }
        Assert.True(checkedCount > 0, "Không có file .docx nào trong DLP_DOCX_CORPUS");
    }
}
