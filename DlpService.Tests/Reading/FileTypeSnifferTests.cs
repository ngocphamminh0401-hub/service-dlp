// [Thay đổi] File mới (magic bytes cho bộ đọc .NET): kiểm tra nhận diện định dạng thật + FileReaderService điều hướng file bị đổi đuôi
// sang đúng parser. File thử được TẠO NGAY TRONG TEST (docx bằng OpenXml, pdf bằng PdfPig, zip bằng ZipArchive) — không phụ thuộc
// file ngoài; riêng corpus tools/tika-bench/corpus (có .xls thật + 6 file đổi đuôi) chỉ kiểm thêm khi có trong repo.
using System.IO.Compression;
using System.Text;
using DlpService.Reading;
using DlpService.Reading.Native;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace DlpService.Tests.Reading;

// Cùng collection với TikaNativeTests: FileReaderService đọc trạng thái static TikaNativeRuntime khi được tạo → không chạy song song
[Collection("TikaNativeRuntime")]
public sealed class FileTypeSnifferTests : IDisposable
{
    // Chuỗi đánh dấu giả (không phải dữ liệu thật) — dùng để kiểm tra text đã được trích đúng
    private const string Marker = "MA-KIEM-TRA-48213 bảng lương";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-sniffer-" + Guid.NewGuid().ToString("N"));

    public FileTypeSnifferTests()
    {
        TikaNativeRuntime.ResetForTests(); // luôn đo bộ đọc .NET, không dính cấu hình Tika của test khác
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    // ---------- Nhận diện ----------

    [Fact]
    public void NhanDien_DungDinhDangThat_KhongPhuThuocDuoi()
    {
        Assert.Equal(DetectedFormat.Docx, FileTypeSniffer.Detect(WriteDocx("a.txt")));
        Assert.Equal(DetectedFormat.Pdf, FileTypeSniffer.Detect(WritePdf("b.docx")));
        Assert.Equal(DetectedFormat.OtherZip, FileTypeSniffer.Detect(WriteZip("c.txt")));
        Assert.Equal(DetectedFormat.Text, FileTypeSniffer.Detect(Write("d.pdf", Encoding.UTF8.GetBytes(Marker))));
        Assert.Equal(DetectedFormat.Empty, FileTypeSniffer.Detect(Write("e.txt", [])));
        Assert.Equal(DetectedFormat.Rtf, FileTypeSniffer.Detect(Write("f.txt", Encoding.ASCII.GetBytes(@"{\rtf1\ansi abc}"))));
    }

    [Fact]
    public void NhanDien_Utf16CoBom_LaVanBan_DuCoNhieuByte0()
    {
        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(Marker)];
        Assert.Equal(DetectedFormat.Text, FileTypeSniffer.Detect(Write("u16.txt", utf16)));
    }

    [Fact]
    public void NhanDien_NhiPhanLa_KhongNhamLaVanBan()
    {
        Assert.Equal(DetectedFormat.UnknownBinary, FileTypeSniffer.Detect(Write("img.txt", [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 0x0D])));
    }

    [Fact]
    public void NhanDien_OleCut_KhongNem()
    {
        // Chỉ có chữ ký OLE, phần còn lại thiếu → OtherOle, không được ném exception
        byte[] truncated = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 1, 2, 3];
        Assert.Equal(DetectedFormat.OtherOle, FileTypeSniffer.Detect(Write("cut.xls", truncated)));
    }

    // ---------- Điều hướng trong FileReaderService ----------

    [Fact]
    public void DocxDoiDuoiTxt_DocBangParserDocx()
    {
        var result = new FileReaderService().TryReadFile(WriteDocx("bangluong.txt"));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(Marker, result.Content);
        Assert.Contains(".docx", result.Warning);
    }

    [Fact]
    public void PdfDoiDuoiDocx_DocBangParserPdf()
    {
        var result = new FileReaderService().TryReadFile(WritePdf("hopdong.docx"));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("MA-KIEM-TRA-48213", result.Content); // font chuẩn của PDF không có dấu tiếng Việt → chỉ so phần ASCII
        Assert.Contains(".pdf", result.Warning);
    }

    [Fact]
    public void VanBanDoiDuoiDocx_DocNhuTxt()
    {
        var result = new FileReaderService().TryReadFile(Write("ghichu.docx", Encoding.UTF8.GetBytes(Marker)));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(Marker, result.Content);
        Assert.Contains("văn bản thuần", result.Warning);
    }

    [Fact]
    public void ZipThuongDoiDuoiTxt_BaoLoi_KhongDocRac()
    {
        var result = new FileReaderService().TryReadFile(WriteZip("dulieu.txt"));

        Assert.False(result.Success);
        Assert.Contains("zip", result.ErrorMessage);
    }

    [Fact]
    public void DungDuoi_KhongCanhBao()
    {
        var reader = new FileReaderService();

        var docx = reader.TryReadFile(WriteDocx("dung.docx"));
        Assert.True(docx.Success, docx.ErrorMessage);
        Assert.Null(docx.Warning);

        var csv = reader.TryReadFile(Write("dung.csv", Encoding.UTF8.GetBytes("ten,ma\nA," + Marker)));
        Assert.True(csv.Success, csv.ErrorMessage);
        Assert.Null(csv.Warning); // văn bản trong .csv là đúng loại, không bị chuyển sang bộ đọc .txt
    }

    [Fact]
    public void TxtCoGhiChuPdfODongDau_VanDocNhuVanBan()
    {
        // "%PDF-" không nằm ở byte 0 → không được coi là PDF
        var result = new FileReaderService().TryReadFile(Write("note.txt", Encoding.UTF8.GetBytes("Ghi chú: header %PDF-1.4 ...\n" + Marker)));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(Marker, result.Content);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void FindExtractor_CungChonTheoNoiDung()
    {
        var extractor = new FileReaderService().FindExtractor(WriteDocx("pass2.txt"));

        Assert.NotNull(extractor);
        Assert.Contains(".docx", extractor.Extensions);
    }

    // ---------- Corpus thật (chỉ khi có trong repo) ----------

    [Fact]
    public void CorpusDoiDuoi_DocDuocTatCa()
    {
        string? renamed = FindCorpus("renamed");
        if (renamed == null)
        {
            return; // không có corpus (VD máy CI) → bỏ qua
        }

        var reader = new FileReaderService();
        foreach (var file in Directory.GetFiles(renamed))
        {
            var result = reader.TryReadFile(file);
            Assert.True(result.Success, $"{Path.GetFileName(file)}: {result.ErrorMessage}");
            Assert.False(string.IsNullOrWhiteSpace(result.Content), $"{Path.GetFileName(file)}: không trích được chữ nào");
        }
    }

    [Fact]
    public void CorpusXls_NhanDienQuaThuMucOle()
    {
        string? normal = FindCorpus(Path.Combine("normal", "xls"));
        if (normal == null)
        {
            return;
        }

        foreach (var file in Directory.GetFiles(normal, "*.xls"))
        {
            Assert.Equal(DetectedFormat.Xls, FileTypeSniffer.Detect(file));
        }
    }

    // ---------- Hàm phụ tạo file thử ----------

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WriteDocx(string name)
    {
        string path = Path.Combine(_folder, name);
        using (var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text(Marker)))));
        }
        return path;
    }

    private string WritePdf(string name)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("MA-KIEM-TRA-48213", 12, new PdfPoint(50, 700), font);
        return Write(name, builder.Build());
    }

    private string WriteZip(string name)
    {
        string path = Path.Combine(_folder, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("ben-trong.txt").Open());
            writer.Write(Marker);
        }
        return path;
    }

    // Tìm tools/tika-bench/corpus/<sub> bằng cách đi ngược lên từ thư mục chạy test
    private static string? FindCorpus(string sub)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "tika-bench", "corpus", sub);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
