// [Thay đổi] File mới (H3): file chỉ đọc được một phần KHÔNG được ghi Public/final. Dùng ResultStore THẬT + 1 rule CCCD tự dựng
// (12 chữ số, mức RESTRICTED) để có/không có chỗ khớp theo ý test. File > 2.000.000 ký tự dùng chuỗi lặp cho nhanh.
using System.Text.RegularExpressions;
using DlpService.Classification;
using DlpService.Pipeline;
using DlpService.Platform;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Tests.Pipeline;

public sealed class FileProcessorIncompleteTests : IDisposable
{
    private const string Cccd = "001099012345";
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-h3-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly ResultStore _store;

    public FileProcessorIncompleteTests()
    {
        Directory.CreateDirectory(_work);
        _dbPath = Path.Combine(_work, "data", "dlp.db");
        _store = new ResultStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // [Thay đổi] M4: + validatorSleepMs (mô phỏng mỗi chỗ khớp tốn thời gian), maxScanTime, rulesVer
    // [Thay đổi] Lỗi 1: + readTimeout (thời gian đọc tối đa của FileReaderService; null = 30 s)
    private FileProcessor Processor(int validatorSleepMs = 0, TimeSpan? maxScanTime = null, string rulesVer = "rv1", TimeSpan? readTimeout = null)
    {
        var cccd = new SensitivePattern
        {
            Name = "CCCD",
            RuleId = "pii_cccd_test",
            Regex = new Regex(@"\b\d{12}\b", RegexOptions.None, TimeSpan.FromSeconds(1)),
            Severity = "RESTRICTED",
            Category = "pii",
            Confidence = 0.95,
            Validator = validatorSleepMs > 0 ? _ => { Thread.Sleep(validatorSleepMs); return true; } : null
        };
        IPathPolicy policy = new CaseInsensitivePathPolicy();
        return new FileProcessor(new FileReaderService(policy, readTimeout), new RegexScanner([cccd], rulesVer, maxScanTime: maxScanTime),
            new ClassificationEngine(NullLogger.Instance),
            NullLogger.Instance, new RouterOptions(), _store, new EscalationOptions(), fileConfidence: null, Path.GetFileName, policy);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_work, name);
        File.WriteAllText(path, content);
        return path;
    }

    // Test-only: đọc thẳng 1 cột scan_result (+ đếm llm_job) của 1 file
    private string? Col(string path, string column)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = column == "jobs"
            ? "SELECT COUNT(*) FROM llm_job j JOIN file_state f ON f.file_id = j.file_id WHERE f.path = $path;"
            : $"SELECT s.{column} FROM scan_result s JOIN file_state f ON f.file_id = s.file_id WHERE f.path = $path;";
        cmd.Parameters.AddWithValue("$path", path);
        return Convert.ToString(cmd.ExecuteScalar());
    }

    private static string Filler(int chars) => string.Concat(Enumerable.Repeat("van ban thuong lap lai. ", chars / 24 + 1))[..chars];

    [Fact]
    public void Bi_cat_va_CCCD_nam_sau_moc_cat_thi_Unknown_incomplete_khong_gui_LLM()
    {
        // Tên chứa "luong" (từ khoá P2 mặc định) → nếu không có H3, file "sạch" này sẽ bị đẩy LLM
        string file = Write("luong-2026.txt", Filler(2_100_000) + " " + Cccd);

        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(file));

        Assert.Equal(ResultStore.StatusIncomplete, Col(file, "status"));
        Assert.Equal(IncompleteReasons.Truncated, Col(file, "incomplete_reason"));
        Assert.Contains("\"Unknown\"", Col(file, "labels_json"));
        Assert.Equal("1", Col(file, "needs_review"));
        Assert.Equal("0", Col(file, "jobs"));
    }

    [Fact]
    public void Bi_cat_nhung_co_match_o_dau_thi_giu_muc_toi_thieu_va_van_incomplete()
    {
        string file = Write("bao-cao.txt", "CCCD " + Cccd + " " + Filler(2_100_000));

        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(file));

        Assert.Equal(ResultStore.StatusIncomplete, Col(file, "status"));
        string labels = Col(file, "labels_json")!;
        Assert.DoesNotContain("\"Unknown\"", labels);
        Assert.DoesNotContain("\"Public\"", labels); // có match RESTRICTED → mức tối thiểu cao hơn Public
        Assert.Equal("1", Col(file, "needs_review"));
    }

    [Fact]
    public void File_doc_tron_khong_match_van_la_Public_final()
    {
        string file = Write("ghi-chu.txt", "noi dung binh thuong");

        Processor().ProcessFile(file);

        Assert.Equal(ResultStore.StatusFinal, Col(file, "status"));
        Assert.Contains("\"Public\"", Col(file, "labels_json"));
        Assert.Equal("", Col(file, "incomplete_reason")); // NULL trong DB (Convert.ToString(DBNull) = "")
    }

    [Fact]
    public void File_incomplete_khong_doi_thi_khong_doc_lai()
    {
        string file = Write("lon.txt", Filler(2_100_000));
        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(file));

        // Cache tầng 1 vẫn dùng được (labels_json khác NULL) → không đọc lại 2 triệu ký tự mỗi lượt đối soát
        Assert.Equal(ProcessOutcome.Unchanged, Processor().ProcessFile(file));
    }

    // [Thay đổi] H4: zip bomb không còn "đọc thất bại" (không ghi DB, lọt khỏi kết quả) mà được ghi incomplete + Unknown, có cache
    [Fact]
    public void Zip_bomb_duoc_ghi_incomplete_limit_exceeded_va_khong_mo_lai()
    {
        string file = Path.Combine(_work, "luong-bomb.docx"); // tên chứa "luong" → chứng minh không đẩy LLM
        using (var zip = System.IO.Compression.ZipFile.Open(file, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("word/document.xml", System.IO.Compression.CompressionLevel.Optimal).Open();
            byte[] zeros = new byte[1024 * 1024];
            for (int i = 0; i < 250; i++) s.Write(zeros); // 250 MB toàn số 0 → nén ~1000 ×
        }

        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(file));

        Assert.Equal(ResultStore.StatusIncomplete, Col(file, "status"));
        Assert.Equal(IncompleteReasons.LimitExceeded, Col(file, "incomplete_reason"));
        Assert.Contains("\"Unknown\"", Col(file, "labels_json"));
        Assert.Equal("1", Col(file, "needs_review"));
        Assert.Equal("0", Col(file, "jobs"));

        Assert.Equal(ProcessOutcome.Unchanged, Processor().ProcessFile(file)); // không mở lại mỗi lượt đối soát
    }

    // [Thay đổi] M4: hết ngân sách quét → incomplete (scan_budget) CÓ cache, thay cho provisional + labels NULL (bị quét lại mọi lượt đối soát)
    [Fact]
    public void Het_ngan_sach_quet_thi_ghi_incomplete_co_cache_va_quet_lai_khi_rule_doi()
    {
        string file = Write("luong-cham.txt", string.Join("\n", Enumerable.Range(0, 60).Select(i => $"CCCD {100000000000 + i}")));
        var budget = TimeSpan.FromMilliseconds(100); // 60 chỗ khớp × 20 ms ≈ 1,2 s > 100 ms

        Assert.Equal(ProcessOutcome.Scanned, Processor(20, budget).ProcessFile(file));

        Assert.Equal(ResultStore.StatusIncomplete, Col(file, "status"));
        Assert.Equal(IncompleteReasons.ScanBudget, Col(file, "incomplete_reason"));
        Assert.NotEqual("", Col(file, "labels_json")); // khác NULL → có cache
        Assert.DoesNotContain("\"Public\"", Col(file, "labels_json"));
        Assert.Equal("1", Col(file, "needs_review"));
        Assert.Equal("0", Col(file, "jobs"));

        Assert.Equal(ProcessOutcome.Unchanged, Processor(20, budget).ProcessFile(file));             // không quét lại liên tục
        Assert.Equal(ProcessOutcome.Scanned, Processor(20, budget, rulesVer: "rv2").ProcessFile(file)); // rule đổi → quét lại (H1)
    }

    // [Thay đổi] M6: email kèm .docx chứa CCCD — trước M6 attachment bị bỏ im lặng → Public/final
    [Fact]
    public void Email_kem_docx_chua_CCCD_khong_con_la_Public()
    {
        byte[] docx = DlpService.Tests.Reading.EmlAttachmentTests.Docx("So CCCD " + Cccd);
        string file = Write("thu.eml", "Subject: Gui ho so\r\nContent-Type: multipart/mixed; boundary=B\r\n\r\n" +
            "--B\r\nContent-Type: text/plain\r\n\r\nAnh xem file\r\n" +
            "--B\r\nContent-Type: application/vnd.openxmlformats-officedocument.wordprocessingml.document; name=\"hs.docx\"\r\n" +
            "Content-Disposition: attachment; filename=\"hs.docx\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
            Convert.ToBase64String(docx, Base64FormattingOptions.InsertLineBreaks) + "\r\n--B--\r\n");

        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(file));

        string labels = Col(file, "labels_json")!;
        Assert.DoesNotContain("\"Public\"", labels);
        Assert.DoesNotContain("\"Unknown\"", labels);
        Assert.Equal(ResultStore.StatusFinal, Col(file, "status")); // đọc trọn (attachment đọc được) → kết luận được
    }

    // [Thay đổi] Điểm A–D: CCCD giấu ở chỗ trình đọc email KHÔNG hiển thị / parser cũ bỏ qua → trước đây Public/final
    public static TheoryData<string, string> HiddenInEml => new()
    {
        { "A_thieu_dong_dong", "Subject: A\r\nContent-Type: multipart/mixed; boundary=MB\r\n\r\n--MB\r\nContent-Type: text/plain\r\n\r\nPhan 1\r\n" +
                               "--MB\r\nContent-Type: text/plain\r\n\r\nCCCD " + Cccd + "\r\n" },
        { "B_khong_co_marker", "Subject: B\r\nContent-Type: multipart/mixed; boundary=XX\r\n\r\nCCCD " + Cccd + "\r\n" },
        { "C_khong_co_boundary", "Subject: C\r\nContent-Type: multipart/mixed\r\n\r\nCCCD " + Cccd + "\r\n" },
        { "D_preamble", "Subject: D\r\nContent-Type: multipart/mixed; boundary=PE\r\n\r\nCCCD " + Cccd + "\r\n--PE\r\nContent-Type: text/plain\r\n\r\nThan\r\n--PE--\r\n" },
        { "D_epilogue", "Subject: D\r\nContent-Type: multipart/mixed; boundary=PE\r\n\r\n--PE\r\nContent-Type: text/plain\r\n\r\nThan\r\n--PE--\r\nCCCD " + Cccd + "\r\n" },
    };

    [Theory]
    [MemberData(nameof(HiddenInEml))]
    public void CCCD_giau_trong_cau_truc_email_van_bi_phat_hien(string name, string eml)
    {
        string file = Write(name + ".eml", eml);

        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(file));

        string labels = Col(file, "labels_json")!;
        Assert.DoesNotContain("\"Public\"", labels);
        Assert.DoesNotContain("\"Unknown\"", labels);
    }

    [Fact]
    public void Unknown_nho_hon_Public_va_khong_doi_gia_tri_mac_dinh()
    {
        Assert.True(SensitivityLevel.Unknown < SensitivityLevel.Public);
        Assert.Equal(SensitivityLevel.Public, default(SensitivityLevel));
        Assert.Equal(SensitivityLevel.Public, "muc-la".ParseSensitivityLevel());
        Assert.Equal(SensitivityLevel.Public, new[] { SensitivityLevel.Unknown, SensitivityLevel.Public }.Max());
    }

    [Fact]
    public void Mo_DB_cu_khong_co_cot_incomplete_reason_thi_tu_them()
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
                CREATE TABLE scan_result (file_id INTEGER PRIMARY KEY, file_hash TEXT NOT NULL, s1 REAL NOT NULL, s_final REAL NOT NULL,
                  status TEXT NOT NULL, source TEXT NOT NULL, labels_json TEXT, needs_review INTEGER NOT NULL DEFAULT 0,
                  updated_utc INTEGER NOT NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new ResultStore(oldDb);
        store.SaveScan(new ScanRecord("/x.txt", 1, DateTime.UtcNow, "h", 0, ResultStore.StatusIncomplete, true,
            "{\"level\":\"Unknown\"}", [], IncompleteReason: IncompleteReasons.Truncated));

        Assert.True(store.TryGetFileState("/x.txt")!.HasConclusion);
    }

    // ===== [Thay đổi] Lỗi 1: file đọc thất bại trước đây trả Failed, KHÔNG có bản ghi DB (lọt khỏi kết quả DLP) =====

    [Fact]
    public void Loi1_File_qua_lon_co_ban_ghi_incomplete_Unknown_va_duoc_cache()
    {
        string file = Path.Combine(_work, "dem-cho-qua-lon.txt");
        using (var f = File.Create(file)) f.SetLength(10L * 1024 * 1024 + 1); // > 10 MB của TxtExtractor

        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(file)); // trước Lỗi 1: Failed

        Assert.Equal(ResultStore.StatusIncomplete, Col(file, "status"));
        Assert.Equal(IncompleteReasons.TooLarge, Col(file, "incomplete_reason"));
        Assert.Contains("\"Unknown\"", Col(file, "labels_json"));
        Assert.Equal("1", Col(file, "needs_review"));
        Assert.Equal("0", Col(file, "jobs"));

        // Lỗi không tạm thời → có cache: lượt đối soát sau (processor mới = restart) bỏ qua, không mở lại file
        Assert.Equal(ProcessOutcome.Unchanged, Processor().ProcessFile(file));
    }

    [Fact]
    public void Loi1_Doc_qua_gio_ghi_incomplete_va_lan_sau_duoc_doc_lai()
    {
        string file = Write("cham.txt", "CCCD " + Cccd);

        // Máy bận: đọc quá giờ ngay khối đầu
        Assert.Equal(ProcessOutcome.Scanned, Processor(readTimeout: TimeSpan.FromTicks(1)).ProcessFile(file));
        Assert.Equal(ResultStore.StatusIncomplete, Col(file, "status"));
        Assert.Equal(IncompleteReasons.ReadTimeout, Col(file, "incomplete_reason"));
        Assert.Equal(IncompleteReasons.ReadTimeout, _store.TryGetFileState(file)!.IncompleteReason);

        // Lượt sau (size/mtime không đổi): read_timeout là lỗi TẠM THỜI → không dùng cache, đọc lại → đọc trọn → kết luận thật
        Assert.Equal(ProcessOutcome.Scanned, Processor().ProcessFile(file));
        Assert.Equal(ResultStore.StatusFinal, Col(file, "status"));
        Assert.Null(_store.TryGetFileState(file)!.IncompleteReason);
        Assert.DoesNotContain("\"Unknown\"", Col(file, "labels_json"));
    }

    [Fact]
    public void Loi1_Zip_doi_duoi_txt_va_docx_hong_deu_co_ban_ghi_incomplete()
    {
        string zip = Path.Combine(_work, "giau.txt");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("cccd.txt").Open());
            w.Write("CCCD " + Cccd);
        }
        string broken = Path.Combine(_work, "hong.docx");
        File.WriteAllBytes(broken, [.. "PK\u0003\u0004"u8.ToArray(), .. Enumerable.Repeat((byte)0x41, 4096)]);

        var processor = Processor();
        Assert.Equal(ProcessOutcome.Scanned, processor.ProcessFile(zip));
        Assert.Equal(ProcessOutcome.Scanned, processor.ProcessFile(broken));

        Assert.Equal(IncompleteReasons.UnsupportedFormat, Col(zip, "incomplete_reason"));
        Assert.Equal(IncompleteReasons.ExtractFailed, Col(broken, "incomplete_reason"));
        Assert.Equal(ResultStore.StatusIncomplete, Col(broken, "status"));
    }

    [Fact]
    public void Loi1_File_khong_ton_tai_van_la_Failed_khong_ghi_DB()
    {
        string file = Path.Combine(_work, "khong-co.txt");

        Assert.Equal(ProcessOutcome.Failed, Processor().ProcessFile(file));
        Assert.Null(_store.TryGetFileState(file));
    }
}
