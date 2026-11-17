// [Thay đổi] File mới (M6): quét attachment trong email (trong bộ nhớ, không ghi file tạm), giải mã lười, giới hạn cấu trúc.
// Attachment tạo ngay trong test: docx/xlsx bằng OpenXml, pdf bằng PdfPig (như test H3/H4), zip bằng System.IO.Compression.
using System.IO.Compression;
using System.Text;
using DlpService.Reading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DlpService.Tests.Reading;

public sealed class EmlAttachmentTests : IDisposable
{
    private const string Cccd = "001099012345";
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-eml-att-" + Guid.NewGuid().ToString("N"));
    private readonly FileReaderService _reader = new();

    public EmlAttachmentTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    // ---- Tạo attachment ----
    internal static byte[] Docx(string text)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            doc.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text(text)))));
        }
        return ms.ToArray();
    }

    private static byte[] Xlsx(string cell)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new S.Workbook();
            var ws = wb.AddNewPart<WorksheetPart>();
            ws.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(cell)) })));
            wb.Workbook.AppendChild(new S.Sheets(new S.Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Luong" }));
        }
        return ms.ToArray();
    }

    private static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText(text, 12, new PdfPoint(50, 750), font);
        return builder.Build();
    }

    private static byte[] ZipBombDocx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = zip.CreateEntry("word/document.xml", CompressionLevel.Optimal).Open();
            byte[] zeros = new byte[1024 * 1024];
            for (int i = 0; i < 100; i++) s.Write(zeros);
        }
        return ms.ToArray();
    }

    private static byte[] PlainZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("a.txt").Open());
            w.Write("CCCD " + Cccd);
        }
        return ms.ToArray();
    }

    // ---- Dựng email ----
    private static string Part(string contentType, string fileName, byte[] bytes, string disposition = "attachment") =>
        $"--B\r\nContent-Type: {contentType}; name=\"{fileName}\"\r\nContent-Disposition: {disposition}; filename=\"{fileName}\"\r\n" +
        $"Content-Transfer-Encoding: base64\r\n\r\n{Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks)}\r\n";

    private string Mail(params string[] parts)
    {
        string body = "Subject: Kem file\r\nContent-Type: multipart/mixed; boundary=B\r\n\r\n--B\r\nContent-Type: text/plain\r\n\r\nThan email\r\n" +
                      string.Concat(parts) + "--B--\r\n";
        string path = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".eml");
        File.WriteAllText(path, body, Encoding.ASCII);
        return path;
    }

    private List<ContentBlock> Blocks(string path) => _reader.FindExtractor(path)!.Extract(path).ToList();

    // ---- Attachment đọc được ----
    [Fact]
    public void Docx_dinh_kem_duoc_quet_kem_ngu_canh_ten_file()
    {
        string path = Mail(Part("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "hop-dong.docx", Docx("So CCCD " + Cccd)));

        var blocks = Blocks(path);
        var r = _reader.TryReadFile(path);

        Assert.Contains(blocks, b => b.Text.Contains(Cccd) && b.ContextPath[0] == "Đính kèm: hop-dong.docx");
        Assert.Contains(blocks, b => b.Kind == BlockKind.Heading && b.Text == "Đính kèm: hop-dong.docx"); // tên file cũng được quét
        Assert.True(r.IsComplete, r.IncompleteReason);
        Assert.Contains(Cccd, r.Content);
        Assert.Contains("Than email", r.Content); // thân email vẫn còn
    }

    [Fact]
    public void Xlsx_va_pdf_dinh_kem_duoc_quet()
    {
        string path = Mail(Part("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "luong.xlsx", Xlsx("Luong " + Cccd)),
                           Part("application/pdf", "ho-so.pdf", Pdf("Ho so 0912345678")));

        var r = _reader.TryReadFile(path);

        Assert.True(r.IsComplete, r.IncompleteReason);
        Assert.Contains(Cccd, r.Content);
        Assert.Contains("0912345678", r.Content);
    }

    [Fact]
    public void Dat_sai_duoi_van_doc_dung_nho_noi_dung()
    {
        string path = Mail(Part("application/octet-stream", "tai-lieu.bin", Docx("CCCD " + Cccd)));

        var r = _reader.TryReadFile(path);

        Assert.True(r.IsComplete, r.IncompleteReason);
        Assert.Contains(Cccd, r.Content);
    }

    [Fact]
    public void Email_chuyen_tiep_duoc_quet()
    {
        string path = Path.Combine(_work, "fwd.eml");
        File.WriteAllBytes(path, EmlSamples.All()["forwarded_rfc822"]);

        var blocks = Blocks(path);

        Assert.Contains(blocks, b => b.Text.Contains("Noi dung thu goc " + Cccd) && b.ContextPath[0] == "Đính kèm: email chuyển tiếp.eml");
        Assert.Contains(blocks, b => b.Text == "Subject: Thu goc"); // header của thư gốc
        Assert.True(_reader.TryReadFile(path).IsComplete);
    }

    [Fact]
    public void Email_chuyen_tiep_long_qua_3_lop_thi_lop_4_khong_doc_duoc()
    {
        string inner = "Subject: Lop 4\r\n\r\nSau nhat\r\n";
        for (int i = 3; i >= 1; i--)
        {
            inner = $"Subject: Lop {i}\r\nContent-Type: multipart/mixed; boundary=F{i}\r\n\r\n--F{i}\r\nContent-Type: message/rfc822\r\n\r\n{inner}--F{i}--\r\n";
        }
        string path = Path.Combine(_work, "fwd4.eml");
        File.WriteAllText(path, "Subject: Goc\r\nContent-Type: multipart/mixed; boundary=F0\r\n\r\n--F0\r\nContent-Type: message/rfc822\r\n\r\n" + inner + "--F0--\r\n");

        var r = _reader.TryReadFile(path);

        Assert.Equal(IncompleteReasons.UnreadablePages, r.IncompleteReason);
        Assert.DoesNotContain("Sau nhat", r.Content);
        Assert.Contains("Subject: Lop 3", r.Content);
    }

    // ---- Attachment không đọc được → Unreadable → incomplete ----
    [Fact]
    public void Anh_inline_logo_bi_bo_qua_khong_lam_incomplete()
    {
        string path = Path.Combine(_work, "logo.eml");
        File.WriteAllBytes(path, EmlSamples.All()["inline_image"]);

        Assert.True(_reader.TryReadFile(path).IsComplete);
    }

    public static TheoryData<string, string, string> UnreadableCases => new()
    {
        { "anh-cccd.jpg", "image/jpeg", "image" },
        { "nen.zip", "application/zip", "zip" },
        { "bomb.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "bomb" },
        { "hong.pdf", "application/pdf", "broken" },
    };

    [Theory]
    [MemberData(nameof(UnreadableCases))]
    public void Attachment_khong_doc_duoc_thi_email_la_incomplete_nhung_than_van_quet(string name, string type, string kind)
    {
        byte[] bytes = kind switch
        {
            "image" => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3],
            "zip" => PlainZip(),
            "bomb" => ZipBombDocx(),
            _ => Encoding.ASCII.GetBytes("%PDF-1.4 hong roi")
        };
        string path = Mail(Part(type, name, bytes));

        var blocks = Blocks(path);
        var r = _reader.TryReadFile(path);

        Assert.Contains(blocks, b => b.Kind == BlockKind.Unreadable && b.Text.StartsWith("Đính kèm: " + name));
        Assert.Equal(IncompleteReasons.UnreadablePages, r.IncompleteReason);
        Assert.Contains("Than email", r.Content);
    }

    // ---- Giới hạn cấu trúc ----
    [Fact]
    public void Qua_1000_phan_thi_limit_exceeded()
    {
        var sb = new StringBuilder("Subject: Nhieu\r\nContent-Type: multipart/mixed; boundary=P\r\n\r\n");
        for (int i = 0; i < 1001; i++) sb.Append("--P\r\nContent-Type: text/plain\r\n\r\nx\r\n");
        sb.Append("--P--\r\n");
        string path = Path.Combine(_work, "parts.eml");
        File.WriteAllText(path, sb.ToString());

        var r = _reader.TryReadFile(path);

        Assert.True(r.Success);
        Assert.Equal(IncompleteReasons.LimitExceeded, r.IncompleteReason);
    }

    [Fact]
    public void Long_multipart_qua_sau_thi_limit_exceeded_thay_vi_that_bai()
    {
        string inner = "Content-Type: text/plain\r\n\r\nTrong cung\r\n";
        for (int i = 30; i >= 1; i--)
        {
            // "N{i}Z": không boundary nào là tiền tố của boundary khác ("--N1" là tiền tố của "--N10" → tách sai, đúng chuẩn MIME)
            inner = $"Content-Type: multipart/mixed; boundary=N{i}Z\r\n\r\n--N{i}Z\r\n{inner}--N{i}Z--\r\n";
        }
        string path = Path.Combine(_work, "deep.eml");
        File.WriteAllText(path, "Subject: Sau\r\n" + inner);

        var r = _reader.TryReadFile(path);

        Assert.True(r.Success); // trước M6: Success = false (InvalidDataException) → không ghi DB
        Assert.Equal(IncompleteReasons.LimitExceeded, r.IncompleteReason);
    }

    // ---- Giải mã lười ----
    [Fact]
    public void Phan_sau_moc_2_trieu_ky_tu_khong_bi_giai_ma()
    {
        // Phần 2: 3 MB quoted-printable — bộ giải mã QP tạo vài chục byte rác cho MỖI ký tự (hàng trăm MB nếu bị giải mã).
        // FileReaderService dừng ở 2.000.000 ký tự trong phần 1 → phần 2 không được duyệt tới → không bị giải mã.
        string part1 = new string('a', 2_100_000);
        string part2 = string.Concat(Enumerable.Repeat("=41=42=43 ", 300_000));
        string path = Path.Combine(_work, "lazy.eml");
        File.WriteAllText(path, "Subject: Lazy\r\nContent-Type: multipart/mixed; boundary=L\r\n\r\n" +
                                "--L\r\nContent-Type: text/plain\r\n\r\n" + part1 + "\r\n" +
                                "--L\r\nContent-Type: text/plain\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" + part2 + "\r\n--L--\r\n");

        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var r = _reader.TryReadFile(path);
        long allocatedMb = (GC.GetAllocatedBytesForCurrentThread() - before) / 1024 / 1024;

        Assert.Equal(IncompleteReasons.Truncated, r.IncompleteReason);
        Assert.True(allocatedMb < 100, $"cấp phát {allocatedMb} MB — phần 2 có vẻ đã bị giải mã");
    }

    // ---- Extract(path) và Extract(stream) cho cùng kết quả ----
    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pdf")]
    [InlineData(".txt")]
    [InlineData(".csv")]
    public void Doc_tu_duong_dan_va_tu_stream_ra_cung_khoi(string ext)
    {
        byte[] bytes = ext switch
        {
            ".docx" => Docx("Noi dung " + Cccd),
            ".xlsx" => Xlsx("O " + Cccd),
            ".pdf" => Pdf("Trang " + Cccd),
            ".txt" => Encoding.UTF8.GetBytes("dong 1\r\ndong 2 " + Cccd),
            _ => Encoding.UTF8.GetBytes("ten,cccd\r\nAn," + Cccd)
        };
        string path = Path.Combine(_work, "mau" + ext);
        File.WriteAllBytes(path, bytes);
        var extractor = _reader.FindExtractor(path)!;

        var fromPath = extractor.Extract(path).ToList();
        using var ms = new MemoryStream(bytes);
        var fromStream = extractor.Extract(ms, "mau" + ext).ToList();

        Assert.True(extractor.SupportsStream);
        Assert.Equal(EmlCharacterizationTests.Serialize(fromPath), EmlCharacterizationTests.Serialize(fromStream));
        Assert.NotEmpty(fromPath);
    }
}
