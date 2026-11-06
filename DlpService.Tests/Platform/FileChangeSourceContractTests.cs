// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): TEST HỢP ĐỒNG cho IFileChangeSource. Mọi nguồn sự kiện (Windows, Linux inotify, macOS) phải:
// báo FileChanged khi tạo/sửa file đúng đuôi, KHÔNG báo file sai đuôi, và báo FileDeleted khi xoá. Thư mục test đặt trong thư mục tạm
// của hệ điều hành (trên WSL2 phải là ext4, không phải /mnt/c — inotify không chạy trên đó).
using System.Collections.Concurrent;
using DlpService.Platform;

namespace DlpService.Tests.Platform;

public abstract class FileChangeSourceContractTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    protected readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-source-" + Guid.NewGuid().ToString("N"));
    protected readonly IFileChangeSource _source;

    protected FileChangeSourceContractTests()
    {
        Directory.CreateDirectory(_folder);
        _source = Create();
    }

    public void Dispose()
    {
        _source.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    protected abstract IFileChangeSource Create();

    /// <summary>Chờ tới khi điều kiện đúng hoặc hết hạn; trả điều kiện cuối cùng.</summary>
    protected static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [Fact]
    public void Tao_file_dung_duoi_thi_bao_FileChanged()
    {
        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"]);

        string file = Path.Combine(_folder, "a.txt");
        File.WriteAllText(file, "xin chao");

        Assert.True(WaitFor(() => changed.Contains(file)), "không nhận được FileChanged");
    }

    [Fact]
    public void File_sai_duoi_thi_khong_bao()
    {
        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"]);

        File.WriteAllText(Path.Combine(_folder, "b.zzz"), "x");
        string good = Path.Combine(_folder, "c.txt");
        File.WriteAllText(good, "x");

        // Chờ file đúng đuôi (đã có sự kiện) rồi mới kết luận file sai đuôi không được báo
        Assert.True(WaitFor(() => changed.Contains(good)));
        Assert.DoesNotContain(Path.Combine(_folder, "b.zzz"), changed);
    }

    [Fact]
    public void File_trong_thu_muc_con_cung_duoc_bao()
    {
        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"]);

        string sub = Path.Combine(_folder, "con", "chau");
        Directory.CreateDirectory(sub);
        string file = Path.Combine(sub, "d.txt");
        File.WriteAllText(file, "x");

        Assert.True(WaitFor(() => changed.Contains(file)), "không nhận được FileChanged ở thư mục con");
    }

    // [Thay đổi] Bước 7: giả lập "cp -r" / giải nén — tạo cây 3 cấp, mỗi cấp nhiều file, ghi dồn dập không nghỉ.
    // Trên Linux đây là tình huống dễ mất sự kiện nhất (file ghi trước khi .NET kịp đăng ký watch cho thư mục con).
    [Fact]
    public void Chep_ca_cay_thu_muc_thi_bao_du_moi_file()
    {
        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"]);

        var expected = new List<string>();
        string dir = _folder;
        foreach (var level in new[] { "a", "b", "c" })
        {
            dir = Path.Combine(dir, level);
            Directory.CreateDirectory(dir);
            for (int i = 0; i < 10; i++)
            {
                string file = Path.Combine(dir, $"f{i}.txt");
                File.WriteAllText(file, "x");
                expected.Add(file);
            }
        }

        Assert.True(WaitFor(() => expected.All(changed.Contains)),
            "thiếu FileChanged cho: " + string.Join(", ", expected.Where(f => !changed.Contains(f))));
    }

    // [Thay đổi] Bước 7: chuyển (mv) 1 thư mục đã có sẵn file từ NGOÀI vào thư mục đang theo dõi — không có sự kiện tạo file nào,
    // chỉ có 1 sự kiện cho thư mục → nguồn phải tự liệt kê file bên trong.
    [Fact]
    public void Chuyen_thu_muc_tu_ngoai_vao_thi_bao_file_ben_trong()
    {
        string outside = _folder + "-ngoai";
        string nested = Path.Combine(outside, "sau");
        Directory.CreateDirectory(nested);
        string file1 = Path.Combine(outside, "g.txt");
        string file2 = Path.Combine(nested, "h.txt");
        File.WriteAllText(file1, "x");
        File.WriteAllText(file2, "x");

        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"]);

        string target = Path.Combine(_folder, "chuyen-vao");
        Directory.Move(outside, target);

        string moved1 = Path.Combine(target, "g.txt");
        string moved2 = Path.Combine(target, "sau", "h.txt");
        Assert.True(WaitFor(() => changed.Contains(moved1) && changed.Contains(moved2)),
            "không nhận được FileChanged cho file trong thư mục chuyển vào");
    }

    [Fact]
    public void Xoa_file_thi_bao_FileDeleted()
    {
        string file = Path.Combine(_folder, "e.txt");
        File.WriteAllText(file, "x");
        var deleted = new ConcurrentBag<string>();
        _source.FileDeleted += deleted.Add;
        _source.Watch(_folder, [".txt"]);

        File.Delete(file);

        Assert.True(WaitFor(() => deleted.Contains(file)), "không nhận được FileDeleted");
    }

    [Fact]
    public void Doi_ten_thi_bao_duong_dan_moi_la_FileChanged()
    {
        string tmp = Path.Combine(_folder, "ghi-do.tmp");
        File.WriteAllText(tmp, "x");
        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt", ".tmp"]);

        // Trình soạn thảo ghi file tạm rồi đổi tên thành tên thật — chỉ có sự kiện Renamed
        string real = Path.Combine(_folder, "that.txt");
        File.Move(tmp, real);

        Assert.True(WaitFor(() => changed.Contains(real)), "không nhận được FileChanged cho đường dẫn mới sau khi đổi tên");
    }

    [Fact]
    public void Sau_Dispose_khong_con_bao_nua()
    {
        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"]);
        _source.Dispose();

        File.WriteAllText(Path.Combine(_folder, "f.txt"), "x");
        Thread.Sleep(500);

        Assert.Empty(changed);
    }
    // [Thay đổi] GĐ3 (R4): các hợp đồng mới về xoá / đổi tên / chuyển đi, loại trừ thư mục, Unwatch

    [Fact]
    public void Doi_ten_file_thi_bao_xoa_duong_dan_cu()
    {
        string oldPath = Path.Combine(_folder, "cu.txt");
        File.WriteAllText(oldPath, "x");
        var deleted = new ConcurrentBag<string>();
        var changed = new ConcurrentBag<string>();
        _source.FileDeleted += deleted.Add;
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"]);

        string newPath = Path.Combine(_folder, "moi.txt");
        File.Move(oldPath, newPath);

        Assert.True(WaitFor(() => deleted.Contains(oldPath) && changed.Contains(newPath)), "đổi tên phải báo xoá đường dẫn cũ + đổi đường dẫn mới");
    }

    [Fact]
    public void Chuyen_thu_muc_ra_ngoai_thi_bao_xoa_thu_muc_hoac_file_ben_trong()
    {
        string sub = Path.Combine(_folder, "se-chuyen-di");
        Directory.CreateDirectory(sub);
        string file = Path.Combine(sub, "a.txt");
        File.WriteAllText(file, "x");
        var deleted = new ConcurrentBag<string>();
        _source.FileDeleted += deleted.Add;
        _source.Watch(_folder, [".txt"]);

        string outside = _folder + "-ra-ngoai";
        Directory.Move(sub, outside);
        try
        {
            // Chỉ có 1 sự kiện cho thư mục; nguồn không biết đó là thư mục → báo đường dẫn thư mục, người nghe xoá cả cây con
            Assert.True(WaitFor(() => deleted.Contains(sub) || deleted.Contains(file)), "chuyển thư mục ra ngoài phải báo FileDeleted");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Thu_muc_an_va_bi_loai_tru_khong_bao()
    {
        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"], new WatchExclusion(skipHidden: true, ["node_modules"]));

        string hidden = Path.Combine(_folder, ".cache");
        string modules = Path.Combine(_folder, "du-an", "node_modules");
        Directory.CreateDirectory(hidden);
        Directory.CreateDirectory(modules);
        Thread.Sleep(300); // để thư mục cha kịp được theo dõi (nguồn tự quét bù nên không bắt buộc, nhưng giúp bài test rõ ràng)
        File.WriteAllText(Path.Combine(hidden, "an.txt"), "x");
        File.WriteAllText(Path.Combine(modules, "goi.txt"), "x");
        string ok = Path.Combine(_folder, "du-an", "binh-thuong.txt");
        File.WriteAllText(ok, "x");

        Assert.True(WaitFor(() => changed.Contains(ok)), "file bình thường phải được báo");
        Thread.Sleep(300);
        Assert.DoesNotContain(changed, p => p.Contains(".cache", StringComparison.Ordinal) || p.Contains("node_modules", StringComparison.Ordinal));
    }

    [Fact]
    public void Unwatch_thi_ngung_bao()
    {
        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"]);
        _source.Unwatch(_folder);

        File.WriteAllText(Path.Combine(_folder, "sau-unwatch.txt"), "x");
        Thread.Sleep(500);

        Assert.Empty(changed);
    }
}

public class FileSystemWatcherSourceTests : FileChangeSourceContractTests
{
    protected override IFileChangeSource Create() => new FileSystemWatcherSource();
}

/// <summary>[Thay đổi] GĐ3: nguồn inotify riêng của Linux — chạy ĐỦ bộ hợp đồng ở trên + các kiểm tra riêng (số watch, gốc bị xoá).
/// Trên Windows/macOS không có inotify → dùng FileSystemWatcherSource để lớp test vẫn chạy được (các test riêng tự bỏ qua).</summary>
public class InotifyFileChangeSourceTests : FileChangeSourceContractTests
{
    protected override IFileChangeSource Create() =>
        OperatingSystem.IsLinux() ? new DlpService.Platform.Linux.InotifyFileChangeSource() : new FileSystemWatcherSource();

    [Fact]
    public void Thu_muc_bi_loai_tru_khong_ton_watch()
    {
        if (_source is not IWatchStatistics stats) return;
        Directory.CreateDirectory(Path.Combine(_folder, "a", "b"));
        Directory.CreateDirectory(Path.Combine(_folder, ".git", "objects", "00"));
        Directory.CreateDirectory(Path.Combine(_folder, "node_modules", "x", "y"));

        _source.Watch(_folder, [".txt"], new WatchExclusion(skipHidden: true, ["node_modules"]));

        Assert.Equal(3, stats.WatchCount); // gốc + a + a/b; .git/** và node_modules/** không gắn
        Assert.Equal(0, stats.FailedWatchCount);
    }

    [Fact]
    public void Thu_muc_con_moi_duoc_gan_watch_va_thu_muc_bi_xoa_duoc_go()
    {
        if (_source is not IWatchStatistics stats) return;
        _source.Watch(_folder, [".txt"]);
        Assert.Equal(1, stats.WatchCount);

        string sub = Path.Combine(_folder, "moi", "sau");
        Directory.CreateDirectory(sub);
        Assert.True(WaitFor(() => stats.WatchCount == 3), $"phải gắn watch cho 2 thư mục mới (đang {stats.WatchCount})");

        Directory.Delete(Path.Combine(_folder, "moi"), recursive: true);
        Assert.True(WaitFor(() => stats.WatchCount == 1), $"thư mục bị xoá phải được gỡ khỏi bảng (đang {stats.WatchCount})");
    }

    [Fact]
    public void Goc_bi_xoa_thi_bao_Failed_khong_phai_tran()
    {
        if (_source is not IWatchStatistics) return;
        var failed = new ConcurrentBag<(string Folder, bool Overflow)>();
        _source.Failed += (f, o, _) => failed.Add((f, o));
        _source.Watch(_folder, [".txt"]);

        Directory.Delete(_folder, recursive: true);

        Assert.True(WaitFor(() => failed.Any(x => x.Folder == _folder && !x.Overflow)), "xoá thư mục gốc phải báo Failed (không tràn)");
        Assert.Single(failed); // chỉ báo 1 lần dù kernel gửi cả IN_DELETE_SELF lẫn IN_IGNORED
    }

    [Fact]
    public void File_ghi_nhieu_lan_roi_dong_chi_bao_1_lan()
    {
        if (_source is not IWatchStatistics) return;
        var changed = new ConcurrentBag<string>();
        _source.FileChanged += changed.Add;
        _source.Watch(_folder, [".txt"]);

        string file = Path.Combine(_folder, "lon.txt");
        using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 1))
        {
            var chunk = new byte[4096];
            for (int i = 0; i < 200; i++)
            {
                stream.Write(chunk); // 200 lần ghi riêng (bufferSize 1 → mỗi Write là 1 lệnh write) — FileSystemWatcher sẽ báo ~200 lần
                stream.Flush();
            }
        }

        Assert.True(WaitFor(() => changed.Contains(file)));
        Thread.Sleep(300);
        Assert.Single(changed); // IN_CLOSE_WRITE: 1 lần khi đóng file
    }
}
