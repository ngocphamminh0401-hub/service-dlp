// [Thay đổi] File mới (M6): đo bộ nhớ CẤP PHÁT khi đọc 1 email lớn có attachment nhị phân. Bản cũ giải mã cả attachment rồi mới bỏ (và cắt chuỗi
// con ở mỗi lớp multipart). Đo bằng GC.GetAllocatedBytesForCurrentThread() quanh việc duyệt HẾT các khối (Extract là iterator).
// Số đo bản cũ (trước M6, đo trên máy dev 17/11/2026): email 19 MB → cấp phát 635 MB. Bản mới: 19 MB (chỉ còn việc đọc file).
using System.Text;
using DlpService.Reading;
using Xunit.Abstractions;

namespace DlpService.Tests.Reading;

public sealed class EmlMemoryTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-eml-mem-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    // Email ~20 MB: 1 phần text ngắn + 1 attachment "ảnh đính kèm" ~14 MB (base64 → ~19 MB trong file)
    internal static byte[] BigEmailWithBinaryAttachment()
    {
        var rng = new Random(5);
        byte[] blob = new byte[14 * 1024 * 1024];
        rng.NextBytes(blob);
        blob[0] = 0xFF; blob[1] = 0xD8; blob[2] = 0xFF; // chữ ký JPEG
        string mail = "Subject: Lon\r\nContent-Type: multipart/mixed; boundary=BIG\r\n\r\n" +
                      "--BIG\r\nContent-Type: text/plain\r\n\r\nNoi dung ngan\r\n" +
                      "--BIG\r\nContent-Type: image/jpeg; name=\"anh.jpg\"\r\nContent-Disposition: attachment; filename=\"anh.jpg\"\r\n" +
                      "Content-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(blob, Base64FormattingOptions.InsertLineBreaks) +
                      "\r\n--BIG--\r\n";
        return Encoding.ASCII.GetBytes(mail);
    }

    internal long MeasureAllocatedBytes()
    {
        Directory.CreateDirectory(_work);
        string path = Path.Combine(_work, "big.eml");
        File.WriteAllBytes(path, BigEmailWithBinaryAttachment());
        var reader = new FileReaderService();
        var extractor = reader.FindExtractor(path)!;

        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int blocks = 0;
        foreach (var _ in extractor.Extract(path)) blocks++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"file {new FileInfo(path).Length / 1024 / 1024} MB → cấp phát {allocated / 1024 / 1024} MB, {blocks} khối");
        return allocated;
    }

    [Fact]
    public void Attachment_nhi_phan_khong_bi_giai_ma_bo_nho_khong_qua_2_lan_file()
    {
        long fileBytes = BigEmailWithBinaryAttachment().Length;

        long allocated = MeasureAllocatedBytes();

        // Bản cũ: ~33 × kích thước file (635 MB / 19 MB). Ngưỡng 2 × để có chỗ cho đọc file + header + phần text.
        Assert.True(allocated < 2 * fileBytes, $"cấp phát {allocated / 1024 / 1024} MB cho file {fileBytes / 1024 / 1024} MB");
    }
}
