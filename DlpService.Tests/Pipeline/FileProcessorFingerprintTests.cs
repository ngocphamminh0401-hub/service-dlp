// [Thay đổi] File mới (H2 — cache size/mtime có thể bỏ qua nội dung đã đổi): khoá 2 hành vi
//  1. Sự kiện ghi (fromChangeEvent) → không dùng cache tầng 1, luôn đọc + băm.
//  2. Quét khởi động / đối soát → tầng 1 so thêm dấu thay đổi (IFileChangeToken); token đổi thì đọc lại dù size/mtime trùng.
// Dùng ResultStore THẬT + scanner không có rule (như FileProcessorRulesVerTests) và 1 token giả đổi được giá trị theo ý test.
using DlpService.Classification;
using DlpService.Pipeline;
using DlpService.Platform;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Tests.Pipeline;

public sealed class FileProcessorFingerprintTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-fp-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly string _file;
    private readonly ResultStore _store;

    // Token giả: test tự đặt giá trị (null = nền tảng không hỗ trợ)
    private sealed class FakeToken : IFileChangeToken
    {
        public string? Value { get; set; }
        public string? Get(string path) => Value;
    }

    public FileProcessorFingerprintTests()
    {
        Directory.CreateDirectory(_work);
        _dbPath = Path.Combine(_work, "data", "dlp.db");
        _store = new ResultStore(_dbPath);
        _file = Path.Combine(_work, "bao-cao.txt");
        File.WriteAllText(_file, "noi dung AAAA");
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // Processor mới mỗi lần (cache hash trong RAM rỗng, chỉ còn cache DB — như service restart)
    private FileProcessor Processor(IFileChangeToken? token = null)
    {
        IPathPolicy policy = new CaseInsensitivePathPolicy();
        return new FileProcessor(new FileReaderService(policy), new RegexScanner([], "rv1"), new ClassificationEngine(NullLogger.Instance),
            NullLogger.Instance, new RouterOptions(), _store, new EscalationOptions(), fileConfidence: null, Path.GetFileName, policy, token);
    }

    // Kịch bản né: sửa nội dung GIỮ ĐÚNG size rồi đặt lại mtime cũ (giống `touch -r`)
    private void TamperKeepingSizeAndMtime()
    {
        DateTime mtime = File.GetLastWriteTimeUtc(_file);
        File.WriteAllText(_file, "noi dung BBBB");
        File.SetLastWriteTimeUtc(_file, mtime);
    }

    [Fact]
    public void Su_kien_ghi_luon_doc_lai_du_size_mtime_trung()
    {
        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(_file));
        string? oldHash = _store.TryGetFileState(_file)!.FileHash;

        TamperKeepingSizeAndMtime();

        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(_file, fromChangeEvent: true));
        Assert.NotEqual(oldHash, _store.TryGetFileState(_file)!.FileHash);
    }

    [Fact]
    public void Doi_soat_token_doi_thi_doc_lai_du_size_mtime_trung()
    {
        var token = new FakeToken { Value = "t1" };
        Assert.Equal(ProcessOutcome.Scanned, Processor(token).ProcessFile(_file));

        TamperKeepingSizeAndMtime();
        token.Value = "t2"; // ctime/inode do kernel đặt → đổi theo lần ghi

        Assert.Equal(ProcessOutcome.Scanned, Processor(token).ProcessFile(_file));
        Assert.Equal("t2", _store.TryGetFileState(_file)!.ChangeToken);
    }

    [Fact]
    public void Khong_co_token_thi_doi_soat_giu_hanh_vi_cu()
    {
        // Ghi lại giới hạn đã biết (Windows / nền tảng không có token): đối soát không phát hiện được kịch bản né — chỉ luồng sự kiện bắt được
        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(_file));
        TamperKeepingSizeAndMtime();

        Assert.Equal(ProcessOutcome.Unchanged, Processor().ProcessFile(_file));
    }

    [Fact]
    public void Khong_doi_gi_thi_tang1_van_bo_qua()
    {
        var token = new FakeToken { Value = "t1" };
        Assert.Equal(ProcessOutcome.Scanned, Processor(token).ProcessFile(_file));

        Assert.Equal(ProcessOutcome.Unchanged, Processor(token).ProcessFile(_file));
    }

    [Fact]
    public void Su_kien_ghi_ma_noi_dung_y_het_thi_dung_o_tang2()
    {
        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(_file));
        File.WriteAllText(_file, "noi dung AAAA"); // ghi lại đúng nội dung cũ

        Assert.Equal(ProcessOutcome.Unchanged, Processor().ProcessFile(_file, fromChangeEvent: true));
    }

    [Fact]
    public void DB_cu_chua_co_token_thi_doc_lai_mot_lan_roi_luu_token()
    {
        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(_file)); // quét bằng bản cũ: token null
        Assert.Null(_store.TryGetFileState(_file)!.ChangeToken);

        var token = new FakeToken { Value = "t1" };
        // Tầng 1 trượt (token đã lưu null ≠ "t1") → đọc + băm → nội dung y hệt → dừng ở tầng 2 và lưu token mới
        Assert.Equal(ProcessOutcome.Unchanged, Processor(token).ProcessFile(_file));
        Assert.Equal("t1", _store.TryGetFileState(_file)!.ChangeToken);

        Assert.Equal(ProcessOutcome.Unchanged, Processor(token).ProcessFile(_file));
    }

    [Fact]
    public void FileStateInfo_SameFingerprint()
    {
        var mtime = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        var state = new FileStateInfo(10, ResultStore.ToTicks(mtime), "h", true, "rv1", ChangeToken: "t1");

        Assert.True(state.SameFingerprint(10, mtime, "t1"));
        Assert.True(state.SameFingerprint(10, mtime, null));   // nền tảng không có token → chỉ size/mtime
        Assert.False(state.SameFingerprint(10, mtime, "t2"));
        Assert.False(state.SameFingerprint(11, mtime, "t1"));
        Assert.False(new FileStateInfo(10, ResultStore.ToTicks(mtime), "h", true).SameFingerprint(10, mtime, "t1")); // đã lưu null
    }

    [Fact]
    public void Mo_DB_cu_khong_co_cot_change_token_thi_tu_them_cot()
    {
        string oldDb = Path.Combine(_work, "old", "dlp.db");
        Directory.CreateDirectory(Path.GetDirectoryName(oldDb)!);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = oldDb }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE file_state (file_id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, size INTEGER NOT NULL,
                  mtime_utc INTEGER NOT NULL, file_hash TEXT, last_scan_utc INTEGER);
                INSERT INTO file_state (path, size, mtime_utc, file_hash) VALUES ('/x', 1, 1, 'h');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new ResultStore(oldDb);

        var state = store.TryGetFileState("/x");
        Assert.NotNull(state);
        Assert.Null(state!.ChangeToken);
        store.MarkUnchanged("/x", 2, DateTime.UtcNow, "t1");
        Assert.Equal("t1", store.TryGetFileState("/x")!.ChangeToken);
    }
}
