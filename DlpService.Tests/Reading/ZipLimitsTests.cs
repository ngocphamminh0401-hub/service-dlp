// [Thay đổi] File mới (H4): khoá ngân sách giải nén ZipLimits. ZIP tạo ngay trong test bằng System.IO.Compression (ghi), file tạm.
// Dữ liệu "nội dung" = bắt đầu bằng '<' (giống XML) rồi chữ ngẫu nhiên → không bị bắt bởi tỉ lệ nén, chứng minh đúng ngân sách
// kích thước/tổng chặn; "zip bomb" = toàn số 0 (nén > 1000 ×); "ảnh" = chữ ký PNG rồi byte ngẫu nhiên (không nén được, ≈ 1 ×).
using System.Buffers.Binary;
using System.IO.Compression;
using DlpService.Reading;
using DlpService.Reading.Extractors;

namespace DlpService.Tests.Reading;

public sealed class ZipLimitsTests : IDisposable
{
    private const int MB = 1024 * 1024;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-zip-" + Guid.NewGuid().ToString("N"));
    private static readonly (ZipBudget Dom, ZipBudget Streaming) Default = ZipLimits.Build(new ZipLimitOptions());

    public ZipLimitsTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private enum Fill { Text, Zeros, Random }

    private string Zip(string name, params (string entry, int megabytes, Fill fill)[] entries) =>
        Zip(name, CompressionLevel.Fastest, entries);

    private string Zip(string name, CompressionLevel level, params (string entry, int megabytes, Fill fill)[] entries)
    {
        string path = Path.Combine(_work, name);
        var rng = new Random(7);
        byte[] buffer = new byte[MB];
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (entry, megabytes, fill) in entries)
            {
                using var s = zip.CreateEntry(entry, level).Open();
                for (int i = 0; i < megabytes; i++)
                {
                    switch (fill)
                    {
                        case Fill.Text: for (int j = 0; j < MB; j++) buffer[j] = (byte)('a' + rng.Next(4)); break;
                        case Fill.Zeros: Array.Clear(buffer); break;
                        case Fill.Random: rng.NextBytes(buffer); break;
                    }
                    if (i == 0 && fill == Fill.Text) buffer[0] = (byte)'<';                       // giống XML
                    if (i == 0 && fill == Fill.Random) new byte[] { 0x89, 0x50, 0x4E, 0x47 }.CopyTo(buffer, 0); // chữ ký PNG
                    s.Write(buffer);
                }
            }
        }
        return path;
    }

    private static void Check(string path, ZipBudget budget)
    {
        using var stream = File.OpenRead(path);
        ZipLimits.EnsureWithin(stream, budget);
        Assert.Equal(0, stream.Position); // trả stream về đầu cho thư viện đọc
    }

    [Fact]
    public void Zip_nho_hop_le_thi_qua()
    {
        Check(Zip("ok.docx", ("word/document.xml", 1, Fill.Text)), Default.Dom);
    }

    [Fact]
    public void Mot_phan_toan_so_0_bi_chan_boi_ti_le_nen()
    {
        // Kẻ tấn công nén mức tối đa (Optimal ≈ 1000 × với toàn số 0; Fastest của zlib-ng chỉ ≈ 100 ×)
        var ex = Assert.Throws<ExtractionLimitException>(() =>
            Check(Zip("bomb.docx", CompressionLevel.Optimal, ("word/document.xml", 25, Fill.Zeros)), Default.Dom));
        Assert.Contains("nén", ex.Message);
    }

    [Fact]
    public void Nhieu_phan_duoi_nguong_nhung_tong_vuot_thi_bi_chan()
    {
        // Đúng lỗi review: mỗi phần 20 MB < 24 MB, nhưng 3 phần = 60 MB > 48 MB (trước H4: qua)
        string path = Zip("many.docx", ("word/a.xml", 20, Fill.Text), ("word/b.xml", 20, Fill.Text), ("word/c.xml", 20, Fill.Text));

        var ex = Assert.Throws<ExtractionLimitException>(() => Check(path, Default.Dom));
        Assert.Contains("tổng", ex.Message);
    }

    [Fact]
    public void Mot_phan_vuot_nguong_Dom_nhung_qua_nguong_Streaming()
    {
        string path = Zip("big.xlsx", ("xl/worksheets/sheet1.xml", 30, Fill.Text));

        Assert.Throws<ExtractionLimitException>(() => Check(path, Default.Dom));
        Check(path, Default.Streaming); // xlsx đọc theo hàng → sheet 30 MB được phép
    }

    [Fact]
    public void SharedStrings_co_nguong_rieng_trong_Streaming()
    {
        string path = Zip("ss.xlsx", ("xl/sharedStrings.xml", 30, Fill.Text)); // > 24 MB: bị nạp hết vào RAM

        Assert.Throws<ExtractionLimitException>(() => Check(path, Default.Streaming));
    }

    [Fact]
    public void Anh_lon_khong_nen_duoc_khong_tinh_vao_ngan_sach_noi_dung()
    {
        // 40 MB ảnh (≈ 1 ×) + 30 MB nội dung: nếu tính ảnh là nội dung thì 70 MB > 48 MB
        string path = Zip("photo.docx", ("word/media/big.png", 40, Fill.Random), ("word/document.xml", 20, Fill.Text), ("word/x.xml", 10, Fill.Text));

        Check(path, Default.Dom);
    }

    [Fact]
    public void Dat_ten_gia_la_anh_van_bi_tinh_la_noi_dung()
    {
        // XML chính đặt tên .png để lách kiểm tra theo tên — vẫn bị tính vì nén được (ngân sách xét theo tỉ lệ, không theo tên)
        string path = Zip("fake.docx", ("word/media/a.png", 20, Fill.Text), ("word/media/b.png", 20, Fill.Text), ("word/media/c.png", 20, Fill.Text));

        Assert.Throws<ExtractionLimitException>(() => Check(path, Default.Dom));
    }

    [Fact]
    public void XML_luu_khong_nen_van_bi_tinh_la_noi_dung()
    {
        // Chế độ stored: tỉ lệ = 1 như ảnh — nếu phân loại theo tỉ lệ nén thì 30 MB XML này lọt ngân sách Dom (24 MB) rồi bị nạp DOM
        string path = Zip("stored.docx", CompressionLevel.NoCompression, ("word/document.xml", 30, Fill.Text));

        Assert.Throws<ExtractionLimitException>(() => Check(path, Default.Dom));
    }

    [Fact]
    public void Qua_nhieu_phan_bi_chan_tu_EOCD()
    {
        string path = Path.Combine(_work, "entries.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            for (int i = 0; i < 10_001; i++) zip.CreateEntry($"p{i}.xml");
        }
        using var stream = File.OpenRead(path);

        Assert.Equal(10_001, ZipLimits.ReadDirectoryInfo(stream)!.Value.Entries);
        var ex = Assert.Throws<ExtractionLimitException>(() => ZipLimits.EnsureWithin(stream, Default.Dom));
        Assert.Contains("phần", ex.Message);
    }

    [Fact]
    public void Khong_phai_zip_thi_bo_qua()
    {
        string path = Path.Combine(_work, "old.xls");
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3, 4]);

        Check(path, Default.Streaming);
    }

    [Fact]
    public void Khai_bao_kich_thuoc_gian_doi_thi_NET_khong_giai_nen_vuot_so_khai_bao()
    {
        // Xác minh giả định nền tảng của ZipLimits: ngân sách dựa trên kích thước KHAI BÁO trong danh mục. Sửa số khai báo của 1 phần 10 MB
        // xuống 1.000 byte → đọc phần đó qua .NET phải ra tối đa 1.000 byte (hoặc ném lỗi), KHÔNG được ra 10 MB.
        string path = Zip("lie.docx", ("word/document.xml", 10, Fill.Zeros));
        byte[] bytes = File.ReadAllBytes(path);
        PatchUncompressedSize(bytes, 1000);
        File.WriteAllBytes(path, bytes);

        long read = 0;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal(1000, zip.Entries[0].Length);
            using var s = zip.Entries[0].Open();
            byte[] buf = new byte[64 * 1024];
            int n;
            while ((n = s.Read(buf)) > 0) read += n;
        }
        catch (InvalidDataException)
        {
            // .NET phát hiện lệch và từ chối — cũng an toàn
        }
        Assert.True(read <= 1000, $"đọc được {read} byte dù khai báo 1000");
    }

    // Sửa trường "uncompressed size" (4 byte) ở cả local header (+22) và central directory (+24) của phần đầu tiên
    private static void PatchUncompressedSize(byte[] zip, uint size)
    {
        for (int i = 0; i + 4 <= zip.Length; i++)
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i));
            if (sig == 0x04034b50) BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 22), size);
            else if (sig == 0x02014b50) { BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 24), size); break; }
        }
    }

    [Fact]
    public void Options_sai_ve_mac_dinh()
    {
        var o = new ZipLimitOptions { MaxEntries = 0, DomMaxEntryMB = -5, DomMaxTotalMB = 1, MaxCompressionRatio = 0.5 };

        var fixedItems = o.Validate();

        Assert.Equal(10_000, o.MaxEntries);
        Assert.Equal(24, o.DomMaxEntryMB);
        Assert.Equal(48, o.DomMaxTotalMB); // tổng < 1 phần là vô lý
        Assert.Equal(200, o.MaxCompressionRatio);
        Assert.Equal(4, fixedItems.Count);
    }

    [Fact]
    public void Cau_hinh_nguong_nho_co_tac_dung()
    {
        // Build là hàm thuần (không đổi ZipLimits.Dom dùng chung) → không ảnh hưởng test khác chạy song song
        var (dom, _) = ZipLimits.Build(new ZipLimitOptions { DomMaxEntryMB = 1, DomMaxTotalMB = 1 });
        string path = Zip("two.docx", ("word/document.xml", 2, Fill.Text));

        Assert.Throws<ExtractionLimitException>(() => Check(path, dom));
        Check(path, Default.Dom);
    }

    [Fact]
    public void FileReader_ghi_nhan_limit_exceeded_thay_vi_that_bai()
    {
        string path = Zip("many-reader.docx", ("word/a.xml", 20, Fill.Text), ("word/b.xml", 20, Fill.Text), ("word/c.xml", 20, Fill.Text));

        var r = new FileReaderService().TryReadFile(path);

        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(IncompleteReasons.LimitExceeded, r.IncompleteReason);
        Assert.Equal("", r.Content);
    }
}
