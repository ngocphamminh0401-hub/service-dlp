// [Thay đổi] File mới (tích hợp Tika native): kiểm tra các điều kiện AN TOÀN của bộ đọc Tika native mà KHÔNG cần có DLL thật:
// mặc định tắt, thiếu DLL, thiếu/sai mã băm đều phải "từ chối êm" (service chạy tiếp), và cách tách dòng không làm hỏng số thẻ/CCCD.
// Việc đọc file thật qua DLL được kiểm bằng tools/tika-bench (lệnh reader-native), vì cần DLL ~100 MB.
using DlpService.Reading;
using DlpService.Reading.Extractors;
using DlpService.Reading.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Tests.Reading;

// TikaNativeRuntime là trạng thái static dùng chung → các test trong lớp này không được chạy song song với nhau
// (xUnit mặc định chạy tuần tự các test TRONG CÙNG 1 lớp)
// [Thay đổi] Magic bytes: + collection chung với FileTypeSnifferTests (lớp đó cũng tạo FileReaderService, phụ thuộc trạng thái này)
[Collection("TikaNativeRuntime")]
public class TikaNativeTests
{
    // 1 file có thật nhưng KHÔNG phải DLL native: dùng để thử đường "có file nhưng bị từ chối" mà không bao giờ gọi NativeLibrary.Load
    private static readonly string DummyFile = typeof(TikaNativeTests).Assembly.Location;

    private static readonly string[] NewExtensions = [".doc", ".ppt", ".rtf"];

    [Fact]
    public void MacDinhTat_KhongDangKyDinhDangMoi()
    {
        TikaNativeRuntime.ResetForTests();
        var reader = new FileReaderService();

        Assert.Empty(new TikaNativeExtractor().Extensions);
        foreach (var ext in NewExtensions)
        {
            Assert.False(reader.SupportedExtensions.Contains(ext), $"{ext} không được đăng ký khi tắt");
        }
        Assert.Contains(".docx", reader.SupportedExtensions); // các định dạng cũ không bị ảnh hưởng
    }

    [Fact]
    public void BatNhungThieuDll_KhongNem_KhongDangKy()
    {
        TikaNativeRuntime.ResetForTests();
        TikaNativeRuntime.Configure(new TikaNativeOptions { Enabled = true, DllPath = Path.Combine("khong-ton-tai", "tika_native.dll"), Sha256 = "abc" }, NullLogger.Instance);

        var reader = new FileReaderService(); // không được ném exception

        Assert.False(TikaNativeRuntime.IsAvailable);
        Assert.DoesNotContain(".doc", reader.SupportedExtensions); // [Thay đổi] GĐ2: sửa cảnh báo xUnit2017
        TikaNativeRuntime.ResetForTests();
    }

    [Fact]
    public void BatNhungKhongGhimMaBam_TuChoiNap()
    {
        TikaNativeRuntime.ResetForTests();
        TikaNativeRuntime.Configure(new TikaNativeOptions { Enabled = true, DllPath = DummyFile, Sha256 = "", RequireSha256 = true }, NullLogger.Instance);

        Assert.False(TikaNativeRuntime.IsAvailable);
        TikaNativeRuntime.ResetForTests();
    }

    [Fact]
    public void GhimSaiMaBam_TuChoiNap()
    {
        TikaNativeRuntime.ResetForTests();
        TikaNativeRuntime.Configure(new TikaNativeOptions { Enabled = true, DllPath = DummyFile, Sha256 = new string('0', 64) }, NullLogger.Instance);

        Assert.False(TikaNativeRuntime.IsAvailable);
        Assert.DoesNotContain(".rtf", new FileReaderService().SupportedExtensions); // [Thay đổi] GĐ2: sửa cảnh báo xUnit2017
        TikaNativeRuntime.ResetForTests();
    }

    [Fact]
    public void TachDong_GhepLaiBangXuongDong_RaDungNoiDungGoc()
    {
        // FileReaderService ghép các block bằng "\n": chỉ được cắt ở ranh giới dòng sẵn có, nếu không số thẻ/CCCD bị xuống dòng chen giữa
        string original = "Khách hàng A, CCCD 079123456789\r\nSố thẻ 4111 1111 1111 1111\n\nDòng cuối không có xuống dòng";

        string joined = string.Join("\n", TikaNativeExtractor.ToBlocks(original).Select(b => b.Text));

        Assert.Equal(original, joined);
        Assert.All(TikaNativeExtractor.ToBlocks(original), b => Assert.Equal(BlockKind.Paragraph, b.Kind));
    }

    // [Thay đổi] Sửa lỗi Windows-1258: chỉ giải mã lại khi là txt/csv CÓ dung lượng mà Tika trả rỗng / không hỗ trợ / toàn khoảng trắng
    [Theory]
    [InlineData(".txt", 5, "", 100, true)]       // Tika trả "rỗng" (file ANSI bị coi là nhị phân)
    [InlineData(".CSV", 2, "", 100, true)]       // không phân biệt hoa thường; "không hỗ trợ" cũng giải mã lại
    [InlineData(".txt", 0, "  \n ", 100, true)]  // "thành công" nhưng toàn khoảng trắng
    [InlineData(".txt", 0, "có chữ", 100, false)] // Tika đọc được → giữ kết quả của Tika
    [InlineData(".txt", 5, "", 0, false)]        // file 0 byte → rỗng thật, không giải mã
    [InlineData(".docx", 5, "", 100, false)]     // không phải văn bản thuần → không giải mã (sẽ là file rỗng/cần OCR)
    [InlineData(".txt", 3, "", 100, false)]      // file hỏng/bị khoá → đi nhánh lỗi, không giải mã
    public void GiaiMaLaiVanBanThuan_DungTruongHop(string ext, int status, string text, long size, bool expected)
    {
        Assert.Equal(expected, TikaNativeExtractor.NeedsPlainTextFallback(ext, status, text, size));
    }

    // [Thay đổi] Sửa triệt để lỗi Windows-1258: chỉ văn bản 8-bit KHÔNG phải UTF-8 mới đi đường giải mã riêng; UTF-8/BOM và file
    // nhị phân (kể cả đặt đuôi .txt) vẫn đi qua Tika
    [Fact]
    public void VanBanBangMaCu_NhanDienDung()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var cp1258 = System.Text.Encoding.GetEncoding(1258);

        Assert.True(TikaNativeExtractor.IsLegacyEncodedText(cp1258.GetBytes("Customer record, account active. Ho ten: Nguyên")));
        Assert.False(TikaNativeExtractor.IsLegacyEncodedText(System.Text.Encoding.UTF8.GetBytes("Họ và tên: Nguyễn Văn Ánh")));   // UTF-8 hợp lệ
        Assert.False(TikaNativeExtractor.IsLegacyEncodedText(System.Text.Encoding.ASCII.GetBytes("ASCII thuần cũng là UTF-8")));
        Assert.False(TikaNativeExtractor.IsLegacyEncodedText([0xEF, 0xBB, 0xBF, 0xC0, 0xC1]));                                   // BOM UTF-8
        Assert.False(TikaNativeExtractor.IsLegacyEncodedText([0xFF, 0xFE, (byte)'A', 0x00]));                                    // BOM UTF-16
        Assert.False(TikaNativeExtractor.IsLegacyEncodedText([(byte)'P', (byte)'K', 3, 4, 0xC0, 0xFF]));                         // docx/zip đặt đuôi .txt
        Assert.False(TikaNativeExtractor.IsLegacyEncodedText([(byte)'%', (byte)'P', (byte)'D', (byte)'F', 0xE2, 0xE3]));         // pdf
        Assert.False(TikaNativeExtractor.IsLegacyEncodedText([0xC0, 0x41, 0x00, 0x42]));                                         // có byte 0 → nhị phân
        Assert.False(TikaNativeExtractor.IsLegacyEncodedText([]));                                                               // rỗng
    }

    // [Thay đổi] Sửa lỗi "file bị khoá báo là hỏng": file bị khoá độc quyền phải ném IOException (FileReaderService đổi thành
    // "File đang bị khóa"); file không bị khoá thì không ném gì
    [Fact]
    public void FileBiKhoa_NemIOException_FileTuDo_KhongNem()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dlp-lock-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "nội dung thử");
        try
        {
            TikaNativeExtractor.ThrowIfLockedOrDenied(path); // không khoá → không ném

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => TikaNativeExtractor.ThrowIfLockedOrDenied(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    // [Thay đổi] Kiểm tra THẬT qua DLL (chỉ chạy khi đặt biến môi trường DLP_TIKA_DLL = đường dẫn tika_native.dll; không có thì
    // bỏ qua để máy không có DLL ~100 MB vẫn chạy được bộ test): ở chế độ Replace, file bị khoá phải báo "File đang bị khóa"
    // (trước khi sửa: "Không trích xuất được nội dung (TikaNativeException) — file hỏng...")
    [Fact]
    public void TikaThat_FileBiKhoa_BaoDangBiKhoa()
    {
        string? dll = Environment.GetEnvironmentVariable("DLP_TIKA_DLL");
        if (string.IsNullOrEmpty(dll) || !File.Exists(dll))
        {
            return; // không có DLL → không kiểm được, coi như bỏ qua
        }

        TikaNativeRuntime.ResetForTests();
        // [Thay đổi] Tối ưu 1a: dùng 2 isolate để kiểm luôn đường chọn isolate theo vòng tròn
        TikaNativeRuntime.Configure(new TikaNativeOptions { Enabled = true, Mode = TikaNativeMode.Replace, DllPath = dll, RequireSha256 = false, Isolates = 2 },
            NullLogger.Instance);
        string path = Path.Combine(Path.GetTempPath(), $"dlp-lock-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "nội dung thử");
        try
        {
            var reader = new FileReaderService();
            Assert.True(TikaNativeRuntime.ReplacesAll);
            Assert.True(reader.TryReadFile(path).Success); // không khoá → đọc được (isolate 1)
            Assert.True(reader.TryReadFile(path).Success); // [Thay đổi] lần 2 → isolate 2 (vòng tròn)

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var locked = reader.TryReadFile(path);
                Assert.False(locked.Success);
                Assert.Equal("File đang bị khóa", locked.ErrorMessage);
            }
        }
        finally
        {
            File.Delete(path);
            TikaNativeRuntime.ResetForTests();
        }
    }

    [Fact]
    public void TuyChonMacDinh_AnToan()
    {
        var o = new TikaNativeOptions();

        Assert.False(o.Enabled);          // tắt
        Assert.True(o.RequireSha256);     // bật thì bắt buộc ghim
        Assert.Equal(30_000, o.TimeoutMs);
        Assert.Equal(2_000_000, o.MaxChars);
        Assert.Equal(1, o.Isolates);          // [Thay đổi] Tối ưu 1a: mặc định 1 isolate = hành vi cũ
    }
}
