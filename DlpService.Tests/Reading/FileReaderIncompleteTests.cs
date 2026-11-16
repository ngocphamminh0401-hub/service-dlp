// [Thay đổi] File mới (H3): khoá cờ "đọc chưa hết" của FileReaderService — bị cắt (bounded append), cần OCR, PDF lẫn trang scan.
// PDF test tạo ngay trong bài bằng PdfDocumentBuilder của PdfPig (gói Core đang dùng), font chuẩn Helvetica + 1 ảnh PNG 1×1.
using DlpService.Reading;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DlpService.Tests.Reading;

public sealed class FileReaderIncompleteTests : IDisposable
{
    private const int MaxChars = 2_000_000; // = FileReaderService.MaxExtractedChars
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-incomplete-" + Guid.NewGuid().ToString("N"));
    private readonly FileReaderService _reader = new();

    // PNG 1×1 hợp lệ — đủ để trang PDF "có ảnh"
    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public FileReaderIncompleteTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    // pages: mỗi phần tử = (chữ trên trang hoặc null, có ảnh không)
    private string Pdf(params (string? text, bool image)[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var (text, image) in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (text != null) page.AddText(text, 12, new PdfPoint(50, 750), font);
            if (image) page.AddPng(Png1x1, new PdfRectangle(50, 100, 400, 600));
        }
        string path = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    [Fact]
    public void Mot_khoi_rat_lon_chi_noi_dung_ngan_sach()
    {
        string path = Path.Combine(_work, "dai.txt");
        File.WriteAllText(path, new string('a', 3_000_000)); // 1 dòng = 1 khối 3 triệu ký tự

        var r = _reader.TryReadFile(path);

        Assert.True(r.Success);
        Assert.Equal(MaxChars, r.Content!.Length);
        Assert.Equal(IncompleteReasons.Truncated, r.IncompleteReason);
        Assert.False(r.IsComplete);
    }

    [Fact]
    public void Vua_dung_gioi_han_thi_van_tron_ven()
    {
        string path = Path.Combine(_work, "vua.txt");
        File.WriteAllText(path, new string('a', MaxChars));

        var r = _reader.TryReadFile(path);

        Assert.Equal(MaxChars, r.Content!.Length);
        Assert.True(r.IsComplete);
    }

    [Fact]
    public void PDF_toan_chu_la_tron_ven()
    {
        var r = _reader.TryReadFile(Pdf(("Trang mot", false), ("Trang hai", false)));

        Assert.True(r.Success);
        Assert.True(r.IsComplete);
        Assert.Contains("Trang hai", r.Content);
    }

    [Fact]
    public void PDF_lan_trang_scan_bao_unreadable_pages()
    {
        var r = _reader.TryReadFile(Pdf(("Trang bia", false), (null, true), (null, true)));

        Assert.True(r.Success);
        Assert.Equal(IncompleteReasons.UnreadablePages, r.IncompleteReason);
        Assert.False(r.NeedsOcr);
        Assert.Contains("Trang bia", r.Content);
    }

    [Fact]
    public void PDF_toan_anh_van_la_can_OCR()
    {
        var r = _reader.TryReadFile(Pdf((null, true), (null, true)));

        Assert.True(r.Success);
        Assert.True(r.NeedsOcr);
        Assert.Equal(IncompleteReasons.NeedsOcr, r.IncompleteReason);
    }

    [Fact]
    public void PDF_co_trang_trang_khong_tinh_la_thieu()
    {
        // Trang trắng (không chữ, không ảnh) không phải nội dung bị thiếu
        var r = _reader.TryReadFile(Pdf(("Noi dung", false), (null, false)));

        Assert.True(r.IsComplete);
    }
}
