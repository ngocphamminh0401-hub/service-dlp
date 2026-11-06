// [Thay đổi] File mới (ghi log qua hàng đợi + luồng nền): kiểm tra không mất/không lẫn dòng khi nhiều luồng ghi cùng lúc,
// xoay vòng theo kích thước, đọc được file trong lúc service đang ghi, và khởi động lại ghi tiếp đúng phần mới nhất.
using DlpService.Logging;
using Microsoft.Extensions.Logging;

namespace DlpService.Tests.Logging;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-log-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string[] AllLines() => Directory.GetFiles(_folder, "dlpservice-*.log")
        .OrderBy(PartOf)
        .SelectMany(File.ReadAllLines)
        .ToArray();

    // "dlpservice-2026-10-21.log" → 0, "dlpservice-2026-10-21.3.log" → 3
    private static int PartOf(string path)
    {
        string middle = Path.GetFileNameWithoutExtension(path)["dlpservice-2026-10-21".Length..];
        return middle.Length == 0 ? 0 : int.Parse(middle[1..]);
    }

    [Fact]
    public void NhieuLuongGhiCungLuc_KhongMatKhongLanDong()
    {
        var provider = new FileLoggerProvider(_folder);
        var logger = provider.CreateLogger("Test");
        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 2_000; i++)
            {
                logger.LogInformation("luong {t} dong {i} ket-thuc", t, i);
            }
        });
        provider.Dispose(); // xả hết hàng đợi

        var lines = AllLines();
        Assert.Equal(16_000, lines.Length);
        Assert.All(lines, l => Assert.EndsWith("ket-thuc", l)); // không dòng nào bị cắt / chèn lẫn
        // Trong cùng 1 luồng, thứ tự dòng được giữ nguyên
        for (int t = 0; t < 8; t++)
        {
            var mine = lines.Where(l => l.Contains($"luong {t} dong ")).Select(l => int.Parse(l.Split(' ')[^2])).ToList();
            Assert.Equal(Enumerable.Range(0, 2_000), mine);
        }
    }

    [Fact]
    public void VuotKichThuoc_XoayVongSangPhanMoi_KhongMatDong()
    {
        var provider = new FileLoggerProvider(_folder, maxFileSizeBytes: 4_000);
        var logger = provider.CreateLogger("Test");
        for (int i = 0; i < 500; i++)
        {
            logger.LogInformation("dong {i}", i);
        }
        provider.Dispose();

        var files = Directory.GetFiles(_folder, "dlpservice-*.log");
        Assert.True(files.Length > 5, $"mong đợi nhiều phần log, thực tế {files.Length}");
        // Mỗi phần không vượt quá giới hạn quá 1 dòng
        Assert.All(files, f => Assert.True(new FileInfo(f).Length < 4_000 + 200));
        Assert.Equal(Enumerable.Range(0, 500), AllLines().Select(l => int.Parse(l.Split(' ')[^1])));
    }

    [Fact]
    public void DangGhi_ChuongTrinhKhacVanDocDuoc_VaThayDongMoiNgay()
    {
        var provider = new FileLoggerProvider(_folder);
        var logger = provider.CreateLogger("Test");
        logger.LogInformation("dong dau tien");

        // Luồng nền flush sau mỗi lô → dòng xuất hiện gần như ngay; chờ tối đa 2 giây
        string? content = null;
        for (int i = 0; i < 40 && (content == null || !content.Contains("dong dau tien")); i++)
        {
            Thread.Sleep(50);
            var file = Directory.GetFiles(_folder, "dlpservice-*.log").FirstOrDefault();
            if (file != null)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                content = new StreamReader(stream).ReadToEnd();
            }
        }
        Assert.Contains("dong dau tien", content);
        provider.Dispose();
    }

    [Fact]
    public void KhoiDongLai_GhiTiepVaoPhanMoiNhat()
    {
        var first = new FileLoggerProvider(_folder, maxFileSizeBytes: 2_000);
        var logger = first.CreateLogger("Test");
        for (int i = 0; i < 100; i++) logger.LogInformation("lan 1 dong {i}", i);
        first.Dispose();
        int partsBefore = Directory.GetFiles(_folder, "dlpservice-*.log").Length;

        var second = new FileLoggerProvider(_folder, maxFileSizeBytes: 2_000);
        second.CreateLogger("Test").LogInformation("lan 2");
        second.Dispose();

        // "lan 2" nằm ở phần có số lớn nhất (không quay lại ghi vào phần 0 đã đầy)
        var last = Directory.GetFiles(_folder, "dlpservice-*.log").OrderBy(PartOf).Last();
        Assert.Contains("lan 2", File.ReadAllText(last));
        Assert.True(Directory.GetFiles(_folder, "dlpservice-*.log").Length >= partsBefore);
    }

    [Fact]
    public void SauKhiDispose_GhiLogKhongNemLoi()
    {
        var provider = new FileLoggerProvider(_folder);
        var logger = provider.CreateLogger("Test");
        provider.Dispose();
        logger.LogInformation("sau khi dung"); // không được ném lỗi (service đang tắt)
    }
}
