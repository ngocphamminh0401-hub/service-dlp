// [Thay đổi] File mới (GĐ3 bước 3.2): quy tắc bỏ thư mục khi theo dõi/quét — thư mục ẩn, danh sách tên, không xét chính thư mục gốc,
// liệt kê không đi vào thư mục bị loại và không theo symlink.
using DlpService.Platform;

namespace DlpService.Tests.Platform;

public sealed class WatchExclusionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dlp-excl-" + Guid.NewGuid().ToString("N"));
    private readonly WatchExclusion _excl = new(skipHidden: true, ["node_modules", "__pycache__"]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(".cache", true)]
    [InlineData(".git", true)]
    [InlineData("node_modules", true)]
    [InlineData("Node_Modules", false)] // mặc định phân biệt hoa/thường (Linux)
    [InlineData("Documents", false)]
    [InlineData(".", false)]
    public void Ten_thu_muc(string name, bool excluded)
    {
        Assert.Equal(excluded, _excl.IsExcludedName(name));
    }

    [Fact]
    public void Khong_xet_chinh_thu_muc_goc_va_ten_file()
    {
        string hiddenRoot = Path.Combine(_root, ".an-goc");
        // Gốc là thư mục ẩn nhưng người dùng chủ động cấu hình → vẫn theo dõi
        Assert.False(_excl.IsExcludedPath(hiddenRoot, Path.Combine(hiddenRoot, "a.txt"), isDirectory: false));
        // File ẩn bình thường (không phải thư mục) không bị loại
        Assert.False(_excl.IsExcludedPath(_root, Path.Combine(_root, ".ghi-chu.txt"), isDirectory: false));
        Assert.True(_excl.IsExcludedPath(_root, Path.Combine(_root, "du-an", "node_modules", "x.txt"), isDirectory: false));
        Assert.True(_excl.IsExcludedPath(_root, Path.Combine(_root, ".cache"), isDirectory: true));
    }

    [Fact]
    public void None_khong_loai_gi()
    {
        Assert.True(WatchExclusion.None.IsEmpty);
        Assert.False(WatchExclusion.None.IsExcludedPath(_root, Path.Combine(_root, ".git", "x.txt"), isDirectory: false));
    }

    [Fact]
    public void Liet_ke_bo_thu_muc_bi_loai_va_symlink()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Documents", "con"));
        Directory.CreateDirectory(Path.Combine(_root, ".cache"));
        Directory.CreateDirectory(Path.Combine(_root, "du-an", "node_modules", "goi"));
        File.WriteAllText(Path.Combine(_root, "Documents", "con", "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, ".cache", "b.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "du-an", "node_modules", "goi", "c.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "du-an", "d.txt"), "x");
        string outside = _root + "-ngoai";
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "e.txt"), "x");
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(Path.Combine(_root, "lien-ket"), outside); // không được đi theo
            }

            var files = _excl.EnumerateFiles(_root).Select(f => Path.GetRelativePath(_root, f)).Order(StringComparer.Ordinal).ToList();
            var dirs = _excl.EnumerateDirectories(_root).Select(f => Path.GetRelativePath(_root, f)).Order(StringComparer.Ordinal).ToList();

            Assert.Equal([Path.Combine("Documents", "con", "a.txt"), Path.Combine("du-an", "d.txt")], files);
            Assert.Equal(["Documents", Path.Combine("Documents", "con"), "du-an"], dirs);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
