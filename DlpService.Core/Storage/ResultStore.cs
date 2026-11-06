// [Thay đổi] File mới (ghi finding + scan_result, chưa có LLM): lưu kết quả quét vào SQLite theo lược đồ mục 3.1 của
// docs/kien-truc-trien-khai.md — 3 bảng file_state, scan_result, finding (bảng llm_* làm khi có LLM). Nguyên tắc P3: kết
// quả regex luôn được ghi trước. KHÔNG lưu giá trị khớp (CCCD, số tài khoản...) vào bất kỳ bảng nào.
using System.Globalization;
using DlpService.Platform; // [Thay đổi] Tách lõi bước 4: IDataFolderGuard
using Microsoft.Data.Sqlite;

namespace DlpService.Storage;

/// <summary>1 dòng finding: tổng hợp các chỗ khớp của 1 rule trong 1 file (không chứa giá trị thật).</summary>
/// <param name="RuleId">Mã rule (RuleId, không có thì tên rule).</param>
/// <param name="Score">Điểm của rule trong file (RuleScore.Score của S1; quét chưa trọn thì điểm candidate cao nhất).</param>
/// <param name="ValidatorPassed">true nếu chỗ khớp đã qua validator checksum; null nếu rule không có checksum.</param>
/// <param name="HardRule">true nếu rule có validator checksum (Luhn/MST/IBAN/CCCD) — sau này LLM không được bác bỏ.</param>
/// <param name="Count">Số chỗ khớp của rule.</param>
/// <param name="Location">Vị trí đại diện, VD "offset:1234" — chỉ vị trí, không có giá trị.</param>
public sealed record FindingRecord(string RuleId, double Score, bool? ValidatorPassed, bool HardRule, int Count, string Location);

/// <summary>Kết quả quét 1 file cần lưu.</summary>
/// <param name="Path">Đường dẫn file.</param>
/// <param name="Size">Kích thước file (byte).</param>
/// <param name="MtimeUtc">Thời điểm sửa file (UTC).</param>
/// <param name="FileHash">SHA-256 của nội dung text đã trích (dạng hex).</param>
/// <param name="S1">Điểm regex cấp file (độ tin cậy của mức đã gán).</param>
/// <param name="Status">"provisional" (chờ thẩm định) hoặc "final".</param>
/// <param name="NeedsReview">Cần LLM/người xem lại.</param>
/// <param name="LabelsJson">Nhãn cuối dạng JSON; null nếu chưa kết luận được.</param>
/// <param name="Findings">Các finding theo rule.</param>
/// <param name="LlmPriority">[Thay đổi] Hàng đợi llm_job: cần LLM thẩm định thì ghi mức ưu tiên (LlmJobPriority), SaveScan
/// tạo job pending trong cùng transaction; null = không cần LLM.</param>
/// <param name="NeedsOcr">[Thay đổi] mục 1.3/4.8: file cần OCR mới đọc được (PDF/ảnh scan) — không phải "sạch" thật.</param>
/// <param name="RulesVer">[Thay đổi] Merger mục 7.4 "Lưu vết": phiên bản bộ rule dùng để quét (RegexScanner.RulesVer).</param>
/// <param name="MergePolicyVer">[Thay đổi] Merger mục 7.4: phiên bản công thức merge tại thời điểm ghi (MergePolicyVersion.Current)
/// — ở Pass 1 chưa có LLM thì S_final vẫn = S1 (đúng "chính sách" hiện có: không có ý kiến LLM thì giữ nguyên).</param>
/// <param name="FileConf">[Thay đổi] FileConfidence 2 nhánh (shadow): điểm 0-1; null = không tính (quét chưa trọn / chưa nạp model).</param>
/// <param name="ConfBranch">[Thay đổi] FileConfidence 2 nhánh: "A" / "B" / "Short"; null khi FileConf null.</param>
public sealed record ScanRecord(string Path, long Size, DateTime MtimeUtc, string FileHash, double S1, string Status,
    bool NeedsReview, string? LabelsJson, IReadOnlyList<FindingRecord> Findings, int? LlmPriority = null, bool NeedsOcr = false,
    string RulesVer = "", string MergePolicyVer = "", double? FileConf = null, string? ConfBranch = null);

// [Thay đổi] Merger mục 7.4 "Lưu vết": đọc gộp lại đúng 8 trường mục 7.4 yêu cầu, vì chúng đang nằm ở 2 bảng khác
// nhau (scan_result và llm_result) — phục vụ debug/audit, không dùng trong luồng xử lý chính.
/// <summary>1 dòng lưu vết đầy đủ của 1 file (mục 7.4): S1/S_final/source/rules_ver/merge_policy_ver luôn có (từ
/// scan_result); S2/model_ver/prompt_ver null nếu file chưa từng gửi LLM (hoặc job chưa xong).</summary>
public sealed record ScanTrace(double S1, double? S2, double SFinal, string Source, string? ModelVer, string? PromptVer,
    string RulesVer, string MergePolicyVer);

/// <summary>[Thay đổi] Cache file_state: thông tin lần quét trước của 1 file, dùng để bỏ qua file không đổi.</summary>
/// <param name="Size">Kích thước file lúc quét trước (byte).</param>
/// <param name="MtimeTicks">Thời điểm sửa file lúc quét trước, đơn vị 100 nano giây (DateTime.Ticks, UTC).</param>
/// <param name="FileHash">SHA-256 của text đã trích lúc quét trước.</param>
/// <param name="HasConclusion">true nếu lần trước đã kết luận được (có scan_result và labels_json khác NULL). Quét chưa
/// trọn (timeout) thì false → lần sau vẫn phải quét lại, không được dùng lại kết quả.</param>
public sealed record FileStateInfo(long Size, long MtimeTicks, string? FileHash, bool HasConclusion)
{
    /// <summary>Size và mtime có giống hệt lần quét trước không (tầng 1 của cache, không cần mở file).</summary>
    public bool SameSizeAndMtime(long size, DateTime mtimeUtc) => Size == size && MtimeTicks == ResultStore.ToTicks(mtimeUtc);
}

/// <summary>Kho kết quả quét trên SQLite. An toàn khi nhiều luồng cùng gọi (dùng chung 1 kết nối, có khoá).</summary>
// [Thay đổi] partial: phần hàng đợi llm_job + máy trạng thái nằm ở ResultStore.LlmJobs.cs (dùng chung kết nối + khoá)
public sealed partial class ResultStore : IDisposable
{
    /// <summary>Trạng thái kết quả: chờ thẩm định (vùng xám / quét chưa trọn).</summary>
    public const string StatusProvisional = "provisional";

    /// <summary>Trạng thái kết quả: đã chốt.</summary>
    public const string StatusFinal = "final";

    // Chưa có LLM nên mọi kết quả đều từ regex
    private const string SourceRegex = "regex";

    private readonly SqliteConnection _connection;

    // Microsoft.Data.Sqlite: 1 SqliteConnection KHÔNG an toàn khi nhiều luồng dùng cùng lúc, mà ProcessFile chạy song
    // song trên nhiều luồng ThreadPool → khoá lại, mỗi lần chỉ 1 luồng ghi (ghi rất nhanh, không phải điểm nghẽn).
    private readonly Lock _writeLock = new();

    // [Thay đổi] Hạn ngạch job (dùng trong ResultStore.LlmJobs.cs)
    private readonly JobQuotaOptions _quota;

    // [Thay đổi] Tách hằng số để vừa dùng khi bật PRAGMA, vừa dùng khi đọc lại kiểm tra
    private const int BusyTimeoutMs = 5000;

    // [Thay đổi] Phiên bản lược đồ, ghi vào PRAGMA user_version — sau này nâng cấp DB cũ thì biết nó đang ở bản nào
    private const int SchemaVersion = 1;

    /// <summary>Mở (hoặc tạo) file DB, khoá ACL thư mục dữ liệu, bật WAL và tạo bảng nếu chưa có.</summary>
    /// <param name="dbPath">Đường dẫn file .db; tự tạo thư mục cha nếu chưa có. Thư mục cha phải là thư mục RIÊNG
    /// của service vì sẽ bị khoá quyền.</param>
    /// <param name="logger">[Thay đổi] Ghi cảnh báo khi ACL/PRAGMA không như mong muốn (không bắt buộc).</param>
    /// <param name="quota">[Thay đổi] Hạn ngạch job P1/P2/P3 + giới hạn hàng đợi; null = mặc định.</param>
    /// <param name="guard">[Thay đổi] Tách lõi bước 4: khoá quyền thư mục dữ liệu (Windows: ACL; Linux: chmod). null = KHÔNG khoá và ghi cảnh báo —
    /// chỉ chấp nhận cho test/tool; host của service thật phải truyền guard của hệ điều hành mình.</param>
    public ResultStore(string dbPath, ILogger? logger = null, JobQuotaOptions? quota = null, IDataFolderGuard? guard = null)
    {
        _quota = quota ?? new JobQuotaOptions(); // [Thay đổi] Hạn ngạch
        string? folder = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
            RestrictFolder(folder, guard, logger); // [Thay đổi] khoá quyền thư mục dữ liệu, trước khi tạo file DB
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _connection.Open();

        // Kiến trúc mục 3.1: WAL (đọc không chặn ghi), synchronous=NORMAL (nhanh, vẫn an toàn khi dùng WAL),
        // busy_timeout 5 giây (chờ thay vì lỗi ngay khi DB đang bị khoá), bật khoá ngoại
        Execute($"PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA foreign_keys=ON;");
        VerifyPragmas(logger); // [Thay đổi] đọc lại để chắc PRAGMA đã thật sự có hiệu lực

        // [Thay đổi] Giảm thời gian ghi DB: tắt checkpoint TỰ ĐỘNG. Mặc định cứ ~1000 trang WAL, SQLite chép WAL vào file DB + fsync
        // NGAY TRONG lần commit đó — lần commit này đang giữ _writeLock nên MỌI luồng xử lý file phải chờ (đo bằng DlpDbBench:
        // p99 ghi 123 ms, max ~0,8–1,4 giây). Checkpoint giờ chạy nền (StartBackgroundCheckpoint). journal_size_limit: khi WAL
        // được dùng lại từ đầu thì cắt file WAL về ≤ 64 MB, không để phình mãi.
        Execute("PRAGMA wal_autocheckpoint=0; PRAGMA journal_size_limit=67108864;");
        Execute("""
            CREATE TABLE IF NOT EXISTS file_state (
              file_id       INTEGER PRIMARY KEY,
              path          TEXT NOT NULL UNIQUE,
              size          INTEGER NOT NULL,
              mtime_utc     INTEGER NOT NULL,
              file_hash     TEXT,
              last_scan_utc INTEGER
            );
            CREATE TABLE IF NOT EXISTS scan_result (
              file_id      INTEGER PRIMARY KEY REFERENCES file_state(file_id),
              file_hash    TEXT NOT NULL,
              s1           REAL NOT NULL,
              s_final      REAL NOT NULL,
              status       TEXT NOT NULL,
              source       TEXT NOT NULL,
              labels_json  TEXT,
              needs_review INTEGER NOT NULL DEFAULT 0,
              needs_ocr    INTEGER NOT NULL DEFAULT 0,
              rules_ver        TEXT NOT NULL DEFAULT '',
              merge_policy_ver TEXT NOT NULL DEFAULT '',
              updated_utc  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS finding (
              id               INTEGER PRIMARY KEY,
              file_id          INTEGER NOT NULL REFERENCES file_state(file_id),
              file_hash        TEXT NOT NULL,
              rule_id          TEXT NOT NULL,
              score            REAL NOT NULL,
              validator_passed INTEGER,
              hard_rule        INTEGER NOT NULL DEFAULT 0,
              count            INTEGER NOT NULL,
              location         TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_finding_file ON finding(file_id);
            CREATE INDEX IF NOT EXISTS ix_scan_review ON scan_result(status, needs_review);
            """);

        // [Thay đổi] 4 bảng còn lại của lược đồ mục 3.1 (tầng LLM + nhật ký) — tạo sẵn để khi làm LLM không phải sửa
        // lược đồ. Chưa có code ghi vào các bảng này. Không bảng nào chứa nội dung/giá trị thật của file.
        Execute("""
            CREATE TABLE IF NOT EXISTS llm_job (
              job_id       TEXT PRIMARY KEY,
              file_id      INTEGER NOT NULL REFERENCES file_state(file_id),
              file_hash    TEXT NOT NULL,
              priority     INTEGER NOT NULL,
              status       TEXT NOT NULL,
              attempts     INTEGER NOT NULL DEFAULT 0,
              next_try_utc INTEGER NOT NULL DEFAULT 0,
              created_utc  INTEGER NOT NULL,
              updated_utc  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_job_pick ON llm_job(status, priority, next_try_utc);
            -- [Thay đổi] Giảm thời gian ghi DB: SaveScan (SyncLlmJob) tra llm_job theo file_id 2 lần mỗi file và đếm hạn ngạch
            -- theo priority — trước đây không có index phù hợp → quét toàn bảng mỗi lần ghi, chậm dần theo số job tích luỹ.
            -- "IF NOT EXISTS" → DB cũ được thêm index ngay lần khởi động đầu tiên.
            CREATE INDEX IF NOT EXISTS ix_job_file ON llm_job(file_id, file_hash);
            CREATE INDEX IF NOT EXISTS ix_job_prio_created ON llm_job(priority, created_utc);
            CREATE INDEX IF NOT EXISTS ix_job_prio_next ON llm_job(priority, next_try_utc);
            CREATE TABLE IF NOT EXISTS llm_result (
              job_id       TEXT PRIMARY KEY REFERENCES llm_job(job_id),
              json_out     TEXT NOT NULL,
              s2           REAL,
              confidence   REAL,
              model_ver    TEXT NOT NULL,
              prompt_ver   TEXT NOT NULL,
              latency_ms   INTEGER,
              created_utc  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS llm_cache (
              pack_hash    TEXT NOT NULL,
              prompt_ver   TEXT NOT NULL,
              model_ver    TEXT NOT NULL,
              json_out     TEXT NOT NULL,
              created_utc  INTEGER NOT NULL,
              PRIMARY KEY (pack_hash, prompt_ver, model_ver)
            );
            CREATE TABLE IF NOT EXISTS audit (
              id           INTEGER PRIMARY KEY,
              ts_utc       INTEGER NOT NULL,
              event        TEXT NOT NULL,
              job_id       TEXT,
              detail       TEXT
            );
            """);
        Execute($"PRAGMA user_version={SchemaVersion};");

        // [Thay đổi] mục 1.3/4.8: DB đã tạo TRƯỚC khi có cột needs_ocr thì "CREATE TABLE IF NOT EXISTS" ở trên không
        // sửa bảng đã có sẵn — phải ALTER TABLE thêm cột. DB mới (cột đã có sẵn từ CREATE TABLE) sẽ báo lỗi "trùng
        // tên cột" ở đây — bắt và bỏ qua, không phải lỗi thật.
        try
        {
            Execute("ALTER TABLE scan_result ADD COLUMN needs_ocr INTEGER NOT NULL DEFAULT 0;");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Cột đã có sẵn (DB mới, hoặc đã migrate ở lần chạy trước) — không phải lỗi
        }

        // [Thay đổi] Merger mục 7.4 "Lưu vết": DB tạo trước khi có rules_ver/merge_policy_ver cần ALTER TABLE thêm cột,
        // cùng cách xử lý "trùng tên cột thì bỏ qua" như needs_ocr ở trên. Tách 2 try/catch riêng để lỡ chỉ 1 cột đã
        // tồn tại (trạng thái nửa vời từ lần migrate trước) không làm cột còn lại bị bỏ sót.
        try
        {
            Execute("ALTER TABLE scan_result ADD COLUMN rules_ver TEXT NOT NULL DEFAULT '';");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Cột đã có sẵn — không phải lỗi
        }
        try
        {
            Execute("ALTER TABLE scan_result ADD COLUMN merge_policy_ver TEXT NOT NULL DEFAULT '';");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Cột đã có sẵn — không phải lỗi
        }

        // [Thay đổi] FileConfidence 2 nhánh (shadow): thêm 2 cột cho phép NULL (file quét trước khi có tính năng này, hoặc
        // không tính được điểm). Cùng cách "trùng tên cột thì bỏ qua", mỗi cột 1 try/catch riêng như trên.
        try
        {
            Execute("ALTER TABLE scan_result ADD COLUMN file_conf REAL;");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Cột đã có sẵn — không phải lỗi
        }
        try
        {
            Execute("ALTER TABLE scan_result ADD COLUMN conf_branch TEXT;");
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Cột đã có sẵn — không phải lỗi
        }

        // [Thay đổi] Giảm thời gian ghi DB: kết nối riêng cho checkpoint nền + kết nối CHỈ ĐỌC cho tra cache (mở SAU khi lược đồ
        // đã sẵn sàng). WAL cho phép đọc song song với ghi → tra cache không phải xếp hàng sau các lần ghi.
        string connectionString = _connection.ConnectionString;
        _checkpointConnection = new SqliteConnection(connectionString);
        _checkpointConnection.Open();
        _readConnection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString());
        _readConnection.Open();
        using (var cmd = _readConnection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs};";
            cmd.ExecuteNonQuery();
        }
        _checkpointTimer = new Timer(_ => CheckpointInBackground(), null, CheckpointIntervalMs, CheckpointIntervalMs);
    }

    // [Thay đổi] Giảm thời gian ghi DB: checkpoint nền ------------------------------------------------------------------------

    /// <summary>Chu kỳ checkpoint nền (ms).</summary>
    private const int CheckpointIntervalMs = 1000;

    private readonly SqliteConnection _checkpointConnection;
    private readonly SqliteConnection _readConnection;
    private readonly Timer _checkpointTimer;
    private readonly Lock _checkpointLock = new(); // timer có thể gọi chồng nhau nếu 1 lần checkpoint chạy > 1 giây
    private readonly Lock _readLock = new();       // 1 SqliteConnection không an toàn đa luồng → khoá riêng cho kết nối đọc

    // PASSIVE: chép càng nhiều trang WAL vào file DB càng tốt mà KHÔNG chờ/chặn luồng đang ghi hay đang đọc; chạy trên kết nối
    // riêng nên KHÔNG giữ _writeLock → luồng xử lý file không bị kẹt trong lúc fsync. Lỗi (DB bận, đang tắt) → bỏ qua, lần sau thử lại.
    private void CheckpointInBackground()
    {
        if (!_checkpointLock.TryEnter())
        {
            return;
        }
        try
        {
            if (_disposed)
            {
                return;
            }
            using var cmd = _checkpointConnection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // Checkpoint chỉ là tối ưu: thất bại thì WAL dài thêm chút, lần sau làm tiếp
        }
        finally
        {
            _checkpointLock.Exit();
        }
    }

    // [Thay đổi] Khoá quyền thư mục dữ liệu. Lỗi (không đủ quyền đổi quyền...) chỉ cảnh báo, không dừng service — cùng
    // nguyên tắc "lỗi lưu trữ không làm dừng luồng quét". Không khoá gốc ổ đĩa (VD DbPath = "D:\dlp.db") vì sẽ khoá
    // cả ổ với mọi người dùng khác.
    // [Thay đổi] Tách lõi bước 4: không còn gọi thẳng DataFolderSecurity (ACL Windows) sau OperatingSystem.IsWindows() — việc khoá do
    // IDataFolderGuard của hệ điều hành đảm nhiệm, nên lõi không phụ thuộc API Windows.
    private static void RestrictFolder(string folder, IDataFolderGuard? guard, ILogger? logger)
    {
        if (guard == null)
        {
            logger?.LogWarning("[DB] Chưa cấu hình IDataFolderGuard — thư mục dữ liệu {folder} KHÔNG được khoá quyền", folder);
            return;
        }

        if (string.Equals(Path.GetPathRoot(folder), folder, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning("[DB] Không đặt quyền cho {folder} vì là gốc ổ đĩa — hãy để DB trong thư mục riêng", folder);
            return;
        }

        try
        {
            guard.Restrict(folder);
            // [Thay đổi] GĐ2 bước 2.2e: câu cũ "Đã khoá quyền ... ((không khoá))" tự mâu thuẫn khi guard là NoOp → câu trung lập, nội dung do guard tự mô tả
            logger?.LogInformation("[DB] Quyền thư mục dữ liệu {folder}: {who}", folder, guard.Description);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[DB] Không đặt được quyền cho {folder}, DB vẫn dùng được nhưng tài khoản khác có thể đọc", folder);
        }
    }

    // [Thay đổi] Đọc lại PRAGMA vừa bật. SQLite KHÔNG báo lỗi khi không bật được WAL (VD ổ mạng không hỗ trợ) mà âm
    // thầm giữ chế độ cũ → phải đọc lại mới biết. Sai chỉ cảnh báo, DB vẫn dùng được (chậm hơn / dễ lỗi khoá hơn).
    private void VerifyPragmas(ILogger? logger)
    {
        string mode = Convert.ToString(Scalar("PRAGMA journal_mode;"), CultureInfo.InvariantCulture) ?? "";
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning("[DB] Không bật được WAL, journal_mode đang là '{mode}'", mode);
        }

        long timeout = Convert.ToInt64(Scalar("PRAGMA busy_timeout;"), CultureInfo.InvariantCulture);
        if (timeout != BusyTimeoutMs)
        {
            logger?.LogWarning("[DB] busy_timeout đang là {timeout} ms, mong đợi {expected} ms", timeout, BusyTimeoutMs);
        }
    }

    /// <summary>Ghi kết quả quét 1 file (3 bảng trong 1 transaction). Quét lại cùng file: scan_result bị ghi đè,
    /// finding cũ bị xoá và ghi mới → DB luôn phản ánh lần quét gần nhất.</summary>
    /// <exception cref="SqliteException">Lỗi DB (đĩa đầy, DB khoá quá 5 giây...) — đã ROLLBACK, nơi gọi tự quyết định xử lý.</exception>
    /// <returns>[Thay đổi] Hạn ngạch: kết quả xử lý hàng đợi llm_job (tạo / hoãn / bỏ...) để nơi gọi log.</returns>
    public JobEnqueueOutcome SaveScan(ScanRecord record)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long mtime = ToTicks(record.MtimeUtc); // [Thay đổi] Cache file_state: giây → 100 nano giây (xem ToTicks)

        lock (_writeLock)
        {
            // Service đang dừng: DB đã đóng nhưng watcher có thể còn xử lý nốt vài file → bỏ qua, không ném lỗi
            if (_disposed)
            {
                return JobEnqueueOutcome.None;
            }

            using var transaction = _connection.BeginTransaction();
            try
            {
                // 1) file_state: thêm mới hoặc cập nhật theo path, lấy file_id
                long fileId;
                using (var cmd = Command(transaction, """
                    INSERT INTO file_state (path, size, mtime_utc, file_hash, last_scan_utc)
                    VALUES ($path, $size, $mtime, $hash, $now)
                    ON CONFLICT(path) DO UPDATE SET
                      size = excluded.size, mtime_utc = excluded.mtime_utc,
                      file_hash = excluded.file_hash, last_scan_utc = excluded.last_scan_utc
                    RETURNING file_id;
                    """))
                {
                    cmd.Parameters.AddWithValue("$path", record.Path);
                    cmd.Parameters.AddWithValue("$size", record.Size);
                    cmd.Parameters.AddWithValue("$mtime", mtime);
                    cmd.Parameters.AddWithValue("$hash", record.FileHash);
                    cmd.Parameters.AddWithValue("$now", now);
                    fileId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                // 2) scan_result: chưa có LLM → s_final = s1, source = regex
                // [Thay đổi] Merger mục 7.4 "Lưu vết": ghi kèm rules_ver/merge_policy_ver ngay từ Pass 1
                using (var cmd = Command(transaction, """
                    INSERT INTO scan_result (file_id, file_hash, s1, s_final, status, source, labels_json, needs_review, needs_ocr, rules_ver, merge_policy_ver, file_conf, conf_branch, updated_utc)
                    VALUES ($id, $hash, $s1, $s1, $status, $source, $labels, $review, $ocr, $rulesver, $mergever, $fileconf, $confbranch, $now)
                    ON CONFLICT(file_id) DO UPDATE SET
                      file_hash = excluded.file_hash, s1 = excluded.s1, s_final = excluded.s_final, status = excluded.status,
                      source = excluded.source, labels_json = excluded.labels_json, needs_review = excluded.needs_review,
                      needs_ocr = excluded.needs_ocr, rules_ver = excluded.rules_ver, merge_policy_ver = excluded.merge_policy_ver,
                      file_conf = excluded.file_conf, conf_branch = excluded.conf_branch,
                      updated_utc = excluded.updated_utc;
                    """))
                {
                    cmd.Parameters.AddWithValue("$id", fileId);
                    cmd.Parameters.AddWithValue("$hash", record.FileHash);
                    cmd.Parameters.AddWithValue("$s1", record.S1);
                    cmd.Parameters.AddWithValue("$status", record.Status);
                    cmd.Parameters.AddWithValue("$source", SourceRegex);
                    cmd.Parameters.AddWithValue("$labels", (object?)record.LabelsJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$review", record.NeedsReview ? 1 : 0);
                    cmd.Parameters.AddWithValue("$ocr", record.NeedsOcr ? 1 : 0);
                    cmd.Parameters.AddWithValue("$rulesver", record.RulesVer);
                    cmd.Parameters.AddWithValue("$mergever", record.MergePolicyVer);
                    // [Thay đổi] FileConfidence 2 nhánh (shadow)
                    cmd.Parameters.AddWithValue("$fileconf", (object?)record.FileConf ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$confbranch", (object?)record.ConfBranch ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.ExecuteNonQuery();
                }

                // 3) finding: xoá của lần quét trước rồi ghi mới
                using (var cmd = Command(transaction, "DELETE FROM finding WHERE file_id = $id;"))
                {
                    cmd.Parameters.AddWithValue("$id", fileId);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = Command(transaction, """
                    INSERT INTO finding (file_id, file_hash, rule_id, score, validator_passed, hard_rule, count, location)
                    VALUES ($id, $hash, $rule, $score, $validator, $hard, $count, $location);
                    """))
                {
                    // Chuẩn bị tham số 1 lần, chỉ đổi giá trị cho từng finding
                    var pRule = cmd.Parameters.Add("$rule", SqliteType.Text);
                    var pScore = cmd.Parameters.Add("$score", SqliteType.Real);
                    var pValidator = cmd.Parameters.Add("$validator", SqliteType.Integer);
                    var pHard = cmd.Parameters.Add("$hard", SqliteType.Integer);
                    var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);
                    var pLocation = cmd.Parameters.Add("$location", SqliteType.Text);
                    cmd.Parameters.AddWithValue("$id", fileId);
                    cmd.Parameters.AddWithValue("$hash", record.FileHash);

                    foreach (var f in record.Findings)
                    {
                        pRule.Value = f.RuleId;
                        pScore.Value = f.Score;
                        pValidator.Value = f.ValidatorPassed is bool passed ? (passed ? 1 : 0) : DBNull.Value;
                        pHard.Value = f.HardRule ? 1 : 0;
                        pCount.Value = f.Count;
                        pLocation.Value = f.Location;
                        cmd.ExecuteNonQuery();
                    }
                }

                // 4) [Thay đổi] llm_job: job cũ của file mà nội dung đã đổi → stale; cần LLM → tạo job pending (không trùng)
                var outcome = SyncLlmJob(transaction, fileId, record.FileHash, record.LlmPriority, now);

                transaction.Commit();
                return outcome;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    // [Thay đổi] Cache file_state: mtime_utc lưu theo đơn vị 100 nano giây (đúng độ chính xác của NTFS) thay vì giây.
    // Lưu theo giây thì file sửa 2 lần trong cùng 1 giây mà size không đổi (VD đổi 1 chữ số CCCD) sẽ bị cache coi là
    // "không đổi" → lọt. DB cũ (lưu theo giây) sẽ lệch đơn vị → mọi file bị quét lại đúng 1 lần rồi bình thường.
    internal static long ToTicks(DateTime mtimeUtc) => DateTime.SpecifyKind(mtimeUtc, DateTimeKind.Utc).Ticks;

    /// <summary>[Thay đổi] Cache file_state: đọc thông tin lần quét trước của file theo đường dẫn.</summary>
    /// <returns>null nếu file chưa từng được quét (hoặc DB đã đóng).</returns>
    /// <exception cref="SqliteException">Lỗi DB — nơi gọi coi như không có cache và quét bình thường.</exception>
    public FileStateInfo? TryGetFileState(string path)
    {
        // [Thay đổi] Giảm thời gian ghi DB: đọc qua kết nối CHỈ ĐỌC riêng (khoá riêng) — WAL cho đọc song song với ghi, nên tra
        // cache không còn phải chờ sau các lần ghi (trước đây dùng chung _writeLock + _connection).
        lock (_readLock)
        {
            if (_disposed)
            {
                return null;
            }

            using var cmd = _readConnection.CreateCommand();
            // LEFT JOIN: có file_state nhưng chưa có scan_result → labels_json NULL → HasConclusion = false
            cmd.CommandText = """
                SELECT f.size, f.mtime_utc, f.file_hash, s.labels_json IS NOT NULL
                FROM file_state f LEFT JOIN scan_result s ON s.file_id = f.file_id
                WHERE f.path = $path;
                """;
            cmd.Parameters.AddWithValue("$path", path);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new FileStateInfo(reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3) == 1);
        }
    }

    // [Thay đổi] Merger mục 7.4 "Lưu vết": join llm_job theo ĐÚNG file_hash hiện tại của scan_result — job của nội
    // dung CŨ (file đã đổi, job cũ bị đánh stale bởi SyncLlmJob) sẽ không khớp điều kiện này nên không bị lẫn vào.
    /// <summary>Đọc gộp đủ 8 trường "lưu vết" (mục 7.4) của 1 file theo đường dẫn — phục vụ debug/audit.</summary>
    /// <returns>null nếu file chưa từng được quét (chưa có scan_result).</returns>
    public ScanTrace? GetTrace(string path)
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return null;
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT s.s1, s.s_final, s.source, s.rules_ver, s.merge_policy_ver, r.s2, r.model_ver, r.prompt_ver
                FROM file_state f
                JOIN scan_result s ON s.file_id = f.file_id
                LEFT JOIN llm_job j ON j.file_id = s.file_id AND j.file_hash = s.file_hash
                LEFT JOIN llm_result r ON r.job_id = j.job_id
                WHERE f.path = $path
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$path", path);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ScanTrace(
                S1: reader.GetDouble(0),
                S2: reader.IsDBNull(5) ? null : reader.GetDouble(5),
                SFinal: reader.GetDouble(1),
                Source: reader.GetString(2),
                ModelVer: reader.IsDBNull(6) ? null : reader.GetString(6),
                PromptVer: reader.IsDBNull(7) ? null : reader.GetString(7),
                RulesVer: reader.GetString(3),
                MergePolicyVer: reader.GetString(4));
        }
    }

    // [Thay đổi] mục 4.12 - Worker điều phối job: Worker cần đọc lại finding đã lưu ở Pass 1 để đưa vào ClassifyReq
    // (ChunkSelector dùng location để trích ngữ cảnh quanh match; Finding trong proto dùng để LLM biết Regex đã nghi gì).
    /// <summary>Đọc các finding đã lưu của 1 file (theo file_id) — dùng khi Worker dựng gói gửi LLM.</summary>
    public List<FindingRecord> GetFindings(long fileId)
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return [];
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT rule_id, score, validator_passed, hard_rule, count, location
                FROM finding WHERE file_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", fileId);

            var results = new List<FindingRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new FindingRecord(
                    reader.GetString(0), reader.GetDouble(1), reader.IsDBNull(2) ? null : reader.GetInt64(2) == 1,
                    reader.GetInt64(3) == 1, reader.GetInt32(4), reader.IsDBNull(5) ? "" : reader.GetString(5)));
            }
            return results;
        }
    }

    /// <summary>[Thay đổi] Cache file_state: nội dung (file_hash) không đổi nhưng size/mtime đổi (VD mở file rồi Save lại)
    /// → chỉ cập nhật size/mtime/last_scan_utc để lần sau tầng 1 bỏ qua được luôn. scan_result và finding giữ nguyên.</summary>
    /// <exception cref="SqliteException">Lỗi DB — nơi gọi tự quyết định xử lý.</exception>
    public void MarkUnchanged(string path, long size, DateTime mtimeUtc)
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE file_state SET size = $size, mtime_utc = $mtime, last_scan_utc = $now WHERE path = $path;";
            cmd.Parameters.AddWithValue("$size", size);
            cmd.Parameters.AddWithValue("$mtime", ToTicks(mtimeUtc));
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$path", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // [Thay đổi] Giảm thời gian ghi DB: dừng checkpoint nền + đóng kết nối đọc/checkpoint. Thứ tự: dừng timer → chờ lần
        // checkpoint đang chạy (nếu có) → đóng. Đóng kết nối cuối cùng thì SQLite tự checkpoint + dọn file WAL.
        _checkpointTimer.Dispose();
        lock (_checkpointLock)
        {
            lock (_readLock)
            {
                lock (_writeLock)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                    _readConnection.Dispose();
                    _checkpointConnection.Dispose();
                    _connection.Dispose();
                }
            }
        }
    }

    // true sau khi Dispose — SaveScan bỏ qua thay vì lỗi "connection is not open"
    private bool _disposed;

    private SqliteCommand Command(SqliteTransaction transaction, string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // [Thay đổi] Chạy lệnh trả về 1 giá trị (dùng để đọc lại PRAGMA)
    private object? Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
