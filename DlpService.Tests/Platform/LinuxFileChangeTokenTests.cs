// [Thay đổi] File mới (H2): kiểm tra dấu thay đổi Linux (statx: dev + inode + ctime) trên file THẬT. Chỉ chạy trên Linux (nơi khác return ngay).
// Các lần thay đổi cách nhau 50 ms: kernel đóng dấu ctime bằng đồng hồ "coarse" (~1 jiffy, 4 ms) — 2 thao tác trong cùng 1 nhịp có thể ra
// cùng ctime. Đây là giới hạn đã biết của lượt đối soát; luồng sự kiện không dựa vào token (xem FileProcessor.ProcessFile, fromChangeEvent).
using DlpService.Platform.Linux;

namespace DlpService.Tests.Platform;

public sealed class LinuxFileChangeTokenTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-token-" + Guid.NewGuid().ToString("N"));
    private readonly LinuxFileChangeToken _token = new();

    public LinuxFileChangeTokenTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private string NewFile(string content = "abc")
    {
        string path = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Token_on_dinh_khi_khong_doi_gi()
    {
        if (!OperatingSystem.IsLinux()) return;
        string file = NewFile();

        string? a = _token.Get(file);

        Assert.NotNull(a);
        Assert.Matches(@"^\d+:\d+:\d+:\d+\.\d{9}$", a);
        Assert.Equal(a, _token.Get(file));
    }

    [Fact]
    public void Dat_lai_mtime_van_doi_token()
    {
        if (!OperatingSystem.IsLinux()) return;
        string file = NewFile();
        DateTime mtime = File.GetLastWriteTimeUtc(file);
        string? before = _token.Get(file);

        Thread.Sleep(50);
        File.WriteAllText(file, "xyz");       // cùng size
        File.SetLastWriteTimeUtc(file, mtime); // giống `touch -r`

        Assert.Equal(mtime, File.GetLastWriteTimeUtc(file)); // mtime đã bị làm giả thành công...
        Assert.NotEqual(before, _token.Get(file));          // ...nhưng ctime do kernel đặt thì không
    }

    [Fact]
    public void Thay_file_bang_rename_doi_inode()
    {
        if (!OperatingSystem.IsLinux()) return;
        string file = NewFile();
        string? before = _token.Get(file);

        string temp = NewFile("abc");          // trình soạn thảo: ghi file tạm rồi rename đè
        File.Move(temp, file, overwrite: true);

        string? after = _token.Get(file);
        Assert.NotNull(after);
        Assert.NotEqual(before!.Split(':')[2], after!.Split(':')[2]); // inode khác
    }

    [Fact]
    public void File_khong_ton_tai_tra_null()
    {
        if (!OperatingSystem.IsLinux()) return;

        Assert.Null(_token.Get(Path.Combine(_work, "khong-co.txt")));
    }
}
