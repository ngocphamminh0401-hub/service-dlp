// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): TEST HỢP ĐỒNG cho IFileChangeSource. Mọi nguồn sự kiện (Windows, Linux inotify, macOS) phải:
// báo FileChanged khi tạo/sửa file đúng đuôi, KHÔNG báo file sai đuôi, và báo FileDeleted khi xoá. Thư mục test đặt trong thư mục tạm
// của hệ điều hành (trên WSL2 phải là ext4, không phải /mnt/c — inotify không chạy trên đó).
using System.Collections.Concurrent;
using DlpService.Platform;

namespace DlpService.Tests.Platform;

public abstract class FileChangeSourceContractTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dlp-source-" + Guid.NewGuid().ToString("N"));
    private readonly IFileChangeSource _source;

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
    private static bool WaitFor(Func<bool> condition)
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
}

public class FileSystemWatcherSourceTests : FileChangeSourceContractTests
{
    protected override IFileChangeSource Create() => new FileSystemWatcherSource();
}
