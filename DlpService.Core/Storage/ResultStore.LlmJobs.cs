// [Thay đổi] File mới (bảng llm_job + máy trạng thái, kiến trúc mục 3.2): hàng đợi job gửi LLM thẩm định. Là phần tách
// riêng của ResultStore (partial class) để dùng chung 1 kết nối SQLite + khoá _writeLock với phần ghi kết quả quét.
//
//   pending ──lấy job──► running ──OK──► done
//      ▲                    │ ├─ lỗi tạm ─► pending (attempts+1, backoff)
//      │                    │ ├─ quá deadline ─► timeout ─► pending (nếu attempts < max) | failed
//      │                    │ └─ file đã đổi ─► stale (bỏ, quét lại từ Pass 1)
//      └── khởi động lại service: running ──► pending
//
// Nguyên tắc: mỗi bước chuyển là 1 câu UPDATE có điều kiện "job đang ở trạng thái X" → bước chuyển sai (VD hoàn thành 1 job
// đã stale) không sửa dòng nào và hàm trả false; DB tự chặn, kể cả khi nhiều luồng cùng thao tác. Mỗi bước chuyển ghi 1 dòng
// audit (chỉ mã sự kiện, job_id, trạng thái, số lần thử — KHÔNG có nội dung file).
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DlpService.Storage;

/// <summary>6 trạng thái của job LLM (giá trị cột llm_job.status).</summary>
public static class LlmJobStatus
{
    /// <summary>Chờ worker lấy (có thể đang chờ tới next_try_utc do backoff).</summary>
    public const string Pending = "pending";
    /// <summary>Worker đang xử lý.</summary>
    public const string Running = "running";
    /// <summary>Đã có kết quả LLM (llm_result).</summary>
    public const string Done = "done";
    /// <summary>Hết số lần thử lại → giữ kết quả regex.</summary>
    public const string Failed = "failed";
    /// <summary>Quá deadline — trạng thái trung gian, chuyển ngay sang pending/failed trong cùng transaction.</summary>
    public const string Timeout = "timeout";
    /// <summary>File đã đổi nội dung → kết quả job (nếu có) không còn đúng, bỏ.</summary>
    public const string Stale = "stale";
}

/// <summary>Mức ưu tiên job (kiến trúc mục 4.8): số nhỏ được lấy trước.</summary>
public static class LlmJobPriority
{
    /// <summary>P1: có chỗ khớp nhưng rơi vào vùng xám của Router.</summary>
    public const int GreyZone = 1;
    /// <summary>P2: không khớp nhưng tên file/đường dẫn đáng nghi (chưa làm).</summary>
    public const int Suspicious = 2;
    /// <summary>P3: lấy mẫu file sạch để đo false negative (chưa làm).</summary>
    public const int Sample = 3;
}

/// <summary>1 job worker vừa lấy ra để xử lý.</summary>
/// <param name="JobId">GUID — khoá idempotency.</param>
/// <param name="FileId">file_state.file_id.</param>
/// <param name="Path">Đường dẫn file (worker đọc lại file).</param>
/// <param name="FileHash">file_hash lúc tạo job — worker so lại trước/sau khi gọi LLM, khác thì MarkJobStale.</param>
/// <param name="Priority">1/2/3.</param>
/// <param name="Attempts">Số lần đã thử lại trước lần này.</param>
/// <param name="S1">[Thay đổi] Merger mục 7: điểm S1 cấp file lúc quét (scan_result.s1) — lấy kèm luôn khi pick job để
/// Worker merge S_final mà không phải đọc thêm 1 lần DB riêng.</param>
public sealed record LlmJob(string JobId, long FileId, string Path, string FileHash, int Priority, int Attempts, double S1);

/// <summary>Kết quả LLM cần lưu vào llm_result (JSON đã validate, KHÔNG chứa dữ liệu thật).</summary>
public sealed record LlmResultRecord(string JsonOut, double? S2, double? Confidence, string ModelVer, string PromptVer, int? LatencyMs);

/// <summary>[Thay đổi] Merger mục 7: kết quả merge cần ghi đè vào scan_result cùng lúc với llm_result (1 transaction).
/// Truyền null cho CompleteJob khi không merge được (không có Verdict nào hợp lệ) — khi đó scan_result giữ nguyên
/// như quét Pass 1, chỉ có llm_result là mới.</summary>
/// <param name="MergePolicyVer">[Thay đổi] Mục 7.4 "Lưu vết": phiên bản công thức merge THẬT SỰ dùng để ra SFinal này
/// (MergePolicyVersion.Current tại thời điểm merge — có thể khác lúc quét Pass 1 nếu công thức đổi trong lúc chờ LLM).</param>
public sealed record ScanMergeUpdate(double SFinal, bool NeedsReview, string Source, string MergePolicyVer);

/// <summary>[Thay đổi] Hạn ngạch job theo nhóm + giới hạn hàng đợi (kiến trúc mục 4.8, 9), đọc từ appsettings.json mục
/// "Llm:Quota". Giá trị ≤ 0 = không cho tạo job nhóm đó (P1 vẫn được hoãn, không bị bỏ).</summary>
public sealed class JobQuotaOptions
{
    /// <summary>Số job P1 được xếp lịch chạy trong mỗi giờ. Vượt → job hoãn sang giờ kế tiếp còn chỗ (không bỏ).</summary>
    public int P1PerHour { get; set; } = 200;
    /// <summary>Số job P2 được tạo trong 24 giờ gần nhất. Vượt → bỏ.</summary>
    public int P2PerDay { get; set; } = 300;
    /// <summary>Số job P3 được tạo trong 24 giờ gần nhất. Vượt → bỏ.</summary>
    public int P3PerDay { get; set; } = 50;
    /// <summary>Số job pending tối đa. Đạt mức này → không nhận P2/P3 mới (quá tải: bỏ P3, rồi P2); P1 vẫn nhận.</summary>
    public int MaxPendingJobs { get; set; } = 1000;
}

/// <summary>[Thay đổi] Kết quả xử lý hàng đợi khi ghi kết quả quét 1 file.</summary>
public enum JobEnqueueOutcome
{
    /// <summary>Không cần LLM.</summary>
    None,
    /// <summary>Đã có job cho đúng nội dung này → không tạo trùng.</summary>
    Duplicate,
    /// <summary>Đã tạo job, lấy được ngay.</summary>
    Created,
    /// <summary>P1 vượt hạn ngạch giờ → đã tạo job nhưng hoãn sang giờ kế tiếp còn chỗ.</summary>
    Deferred,
    /// <summary>P2/P3 vượt hạn ngạch ngày → không tạo.</summary>
    DroppedQuota,
    /// <summary>Hàng đợi đầy → không nhận P2/P3.</summary>
    DroppedQueueFull
}

public sealed partial class ResultStore
{
    /// <summary>Số lần thử lại tối đa (kiến trúc mục 3.2). Lỗi lần thứ MaxJobAttempts + 1 → failed.</summary>
    public const int MaxJobAttempts = 3;

    // Backoff lũy thừa cho lần thử lại thứ 1, 2, 3 (kiến trúc mục 3.2)
    private static readonly TimeSpan[] RetryBackoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(20)];

    /// <summary>Worker lấy 1 job: pending → running. Chọn job ưu tiên cao nhất (priority nhỏ nhất), cũ nhất, đã hết thời
    /// gian chờ backoff. 1 câu UPDATE ... RETURNING nên 2 worker không bao giờ lấy trùng 1 job.</summary>
    /// <returns>null nếu không còn job nào sẵn sàng.</returns>
    public LlmJob? TryPickNextJob() => InJobTransaction<LlmJob?>((tx, now) =>
    {
        string jobId;
        long fileId;
        string fileHash;
        int priority, attempts;
        using (var cmd = Command(tx, """
            UPDATE llm_job SET status = 'running', updated_utc = $now
            WHERE job_id = (SELECT job_id FROM llm_job
                            WHERE status = 'pending' AND next_try_utc <= $now
                            ORDER BY priority, created_utc LIMIT 1)
            RETURNING job_id, file_id, file_hash, priority, attempts;
            """))
        {
            cmd.Parameters.AddWithValue("$now", now);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            (jobId, fileId, fileHash, priority, attempts) =
                (reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4));
        }

        string path;
        using (var cmd = Command(tx, "SELECT path FROM file_state WHERE file_id = $id;"))
        {
            cmd.Parameters.AddWithValue("$id", fileId);
            path = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        // [Thay đổi] Merger mục 7: lấy kèm s1 hiện tại của file (đã ghi ở SaveScan Pass 1) để Worker merge S_final.
        // Không có scan_result (không nên xảy ra vì job chỉ tạo sau SaveScan) → coi S1 = 0, an toàn hơn là ném lỗi.
        double s1;
        using (var cmd = Command(tx, "SELECT s1 FROM scan_result WHERE file_id = $id;"))
        {
            cmd.Parameters.AddWithValue("$id", fileId);
            object? value = cmd.ExecuteScalar();
            s1 = value == null ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        Audit(tx, now, "job_picked", jobId, $"{LlmJobStatus.Pending}→{LlmJobStatus.Running}, attempts={attempts}");
        return new LlmJob(jobId, fileId, path, fileHash, priority, attempts, s1);
    }, null);

    /// <summary>Xử lý OK: running → done, và lưu kết quả vào llm_result (upsert theo job_id) trong cùng transaction.
    /// Idempotent: gọi lại lần 2 cho job đã done chỉ ghi đè kết quả, không lỗi (at-least-once).</summary>
    /// <param name="packHash">[Thay đổi] llm_cache: hash của gói đã mask vừa gửi LLM → ghi thêm vào llm_cache (upsert, trùng
    /// khoá thì ghi đè) trong CÙNG transaction. null = không ghi cache (VD kết quả vốn lấy từ cache).</param>
    /// <param name="merge">[Thay đổi] Merger mục 7: kết quả merge (S_final/needs_review/source) cần ghi đè vào scan_result
    /// trong CÙNG transaction; null = không đổi scan_result (VD LLM không trả verdict nào dùng được).</param>
    /// <param name="fileId">[Thay đổi] Merger mục 7: cần để cập nhật đúng dòng scan_result (chỉ dùng khi merge != null).</param>
    /// <param name="fileHash">[Thay đổi] Merger mục 7: chỉ ghi đè nếu scan_result vẫn còn đúng file_hash này — phòng
    /// trường hợp hiếm: có bản quét MỚI (Pass 1) ghi đè scan_result trong lúc job LLM còn đang chạy.</param>
    /// <returns>false nếu job không ở running/done (VD đã stale, đã về pending sau khi service khởi động lại) → bỏ kết quả.</returns>
    /// <exception cref="ArgumentException">[Thay đổi] json_out không phải JSON hợp lệ — không ghi gì, job giữ nguyên trạng thái.</exception>
    public bool CompleteJob(string jobId, LlmResultRecord result, string? packHash = null,
        ScanMergeUpdate? merge = null, long fileId = 0, string? fileHash = null)
    {
        EnsureValidJson(result.JsonOut); // [Thay đổi] kiểm tra TRƯỚC khi mở transaction → JSON hỏng thì không đụng DB
        return CompleteJobCore(jobId, result, packHash, merge, fileId, fileHash);
    }

    private bool CompleteJobCore(string jobId, LlmResultRecord result, string? packHash,
        ScanMergeUpdate? merge, long fileId, string? fileHash) => InJobTransaction((tx, now) =>
    {
        bool moved = TryTransition(tx, jobId, LlmJobStatus.Running, LlmJobStatus.Done, now, "job_done", "");
        if (!moved && GetJobStatus(tx, jobId) != LlmJobStatus.Done)
        {
            return false;
        }

        using (var cmd = Command(tx, """
            INSERT INTO llm_result (job_id, json_out, s2, confidence, model_ver, prompt_ver, latency_ms, created_utc)
            VALUES ($job, $json, $s2, $conf, $model, $prompt, $latency, $now)
            ON CONFLICT(job_id) DO UPDATE SET
              json_out = excluded.json_out, s2 = excluded.s2, confidence = excluded.confidence,
              model_ver = excluded.model_ver, prompt_ver = excluded.prompt_ver, latency_ms = excluded.latency_ms,
              created_utc = excluded.created_utc;
            """))
        {
            cmd.Parameters.AddWithValue("$job", jobId);
            cmd.Parameters.AddWithValue("$json", result.JsonOut);
            cmd.Parameters.AddWithValue("$s2", (object?)result.S2 ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$conf", (object?)result.Confidence ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$model", result.ModelVer);
            cmd.Parameters.AddWithValue("$prompt", result.PromptVer);
            cmd.Parameters.AddWithValue("$latency", (object?)result.LatencyMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        // [Thay đổi] Merger mục 7: ghi đè scan_result.s_final/source/needs_review — CHỈ khi file_hash vẫn khớp (an
        // toàn: nếu khác thì đã có bản quét Pass 1 mới hơn, s1/s_final đó mới là đúng, không được ghi đè bằng kết quả
        // merge của nội dung CŨ).
        if (merge != null)
        {
            using var cmd = Command(tx, """
                UPDATE scan_result SET s_final = $sfinal, source = $source, needs_review = $review,
                  merge_policy_ver = $mergever, updated_utc = $now
                WHERE file_id = $id AND file_hash = $hash;
                """);
            cmd.Parameters.AddWithValue("$sfinal", merge.SFinal);
            cmd.Parameters.AddWithValue("$source", merge.Source);
            cmd.Parameters.AddWithValue("$review", merge.NeedsReview ? 1 : 0);
            cmd.Parameters.AddWithValue("$mergever", merge.MergePolicyVer);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.Parameters.AddWithValue("$hash", fileHash ?? "");
            cmd.ExecuteNonQuery();
        }

        // [Thay đổi] llm_cache (tầng 2): cùng transaction → không bao giờ có job done mà thiếu cache hoặc ngược lại
        if (packHash != null)
        {
            UpsertLlmCache(tx, packHash, result, now);
        }
        return true;
    }, false);

    /// <summary>Lỗi tạm (pipe đứt, LLM chưa sẵn sàng, output lỗi...): running → pending (attempts+1, chờ backoff), hoặc
    /// → failed nếu đã thử lại đủ MaxJobAttempts lần.</summary>
    /// <param name="reason">Mã lỗi ngắn (VD "PIPE_BROKEN", "MODEL_ERROR") — ghi vào audit, KHÔNG đưa nội dung file vào.</param>
    /// <returns>false nếu job không đang running.</returns>
    public bool FailJob(string jobId, string reason) =>
        InJobTransaction((tx, now) => RetryOrFail(tx, jobId, LlmJobStatus.Running, now, reason), false);

    /// <summary>[Thay đổi] Lỗi VĨNH VIỄN (VD LLM báo OUTPUT_TOO_LONG: cùng nội dung sẽ luôn ra cùng kết quả hỏng): running →
    /// failed NGAY, không qua backoff/thử lại; đồng thời chốt scan_result.status='final' (giữ điểm regex, needs_review
    /// giữ như Pass 1 đã đặt) — cùng cách xử lý như job hết lượt thử ở RetryOrFail.</summary>
    /// <param name="reason">Mã lỗi ngắn ghi vào audit, KHÔNG đưa nội dung file vào.</param>
    /// <returns>false nếu job không đang running.</returns>
    public bool FailJobPermanently(string jobId, string reason) => InJobTransaction((tx, now) =>
    {
        bool moved = TryTransition(tx, jobId, LlmJobStatus.Running, LlmJobStatus.Failed, now, "job_failed",
            $", {reason}, lỗi vĩnh viễn nên không thử lại");
        if (moved)
        {
            MarkScanFinalAfterJobFailed(tx, jobId, now);
        }
        return moved;
    }, false);

    /// <summary>Quá deadline: running → timeout → pending (attempts+1, chờ backoff) | failed. Hai bước trong cùng 1
    /// transaction (timeout là trạng thái trung gian, không bao giờ bị kẹt lại); audit ghi đủ cả 2 bước.</summary>
    /// <returns>false nếu job không đang running.</returns>
    public bool TimeoutJob(string jobId) => InJobTransaction((tx, now) =>
        TryTransition(tx, jobId, LlmJobStatus.Running, LlmJobStatus.Timeout, now, "job_timeout", "")
        && RetryOrFail(tx, jobId, LlmJobStatus.Timeout, now, "TIMEOUT"), false);

    /// <summary>Worker thấy file đã đổi (file_hash khác lúc tạo job): running → stale. File sẽ được quét lại từ Pass 1
    /// qua watcher; không tự tạo job mới ở đây.</summary>
    /// <returns>false nếu job không đang running.</returns>
    public bool MarkJobStale(string jobId) => InJobTransaction((tx, now) =>
        TryTransition(tx, jobId, LlmJobStatus.Running, LlmJobStatus.Stale, now, "job_stale", ", file đã đổi"), false);

    /// <summary>Gọi khi service khởi động: job running của lần chạy trước (service tắt/crash giữa chừng) → pending, được lấy
    /// lại ngay. KHÔNG tăng attempts — service tắt không phải lỗi của job.</summary>
    /// <returns>Số job được đưa về pending.</returns>
    public int RecoverRunningJobs() => InJobTransaction((tx, now) =>
    {
        var ids = new List<string>();
        using (var cmd = Command(tx, """
            UPDATE llm_job SET status = 'pending', next_try_utc = $now, updated_utc = $now
            WHERE status = 'running' RETURNING job_id;
            """))
        {
            cmd.Parameters.AddWithValue("$now", now);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        foreach (var id in ids)
        {
            Audit(tx, now, "job_recovered", id, $"{LlmJobStatus.Running}→{LlmJobStatus.Pending}, khởi động lại service");
        }
        return ids.Count;
    }, 0);

    /// <summary>Tạo bù job P1 cho file vùng xám (provisional + needs_review + đã có nhãn) chưa có job nào cho đúng nội dung
    /// hiện tại. Cần vì cache file_state bỏ qua file không đổi → file vùng xám quét TRƯỚC khi có hàng đợi sẽ không bao giờ
    /// được tạo job. Idempotent: chạy nhiều lần không tạo trùng.</summary>
    /// <returns>Số job được tạo.</returns>
    public int EnqueueMissingGreyJobs() => InJobTransaction((tx, now) =>
    {
        var files = new List<(long FileId, string Hash)>();
        using (var cmd = Command(tx, """
            SELECT s.file_id, s.file_hash FROM scan_result s
            WHERE s.status = 'provisional' AND s.needs_review = 1 AND s.labels_json IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM llm_job j
                              WHERE j.file_id = s.file_id AND j.file_hash = s.file_hash AND j.status <> 'stale');
            """))
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                files.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        // [Thay đổi] Hạn ngạch: tạo bù cũng phải qua hạn ngạch P1 (vượt → hoãn sang giờ sau, không bỏ)
        foreach (var (fileId, hash) in files)
        {
            EnqueueWithQuota(tx, fileId, hash, LlmJobPriority.GreyZone, now);
        }
        return files.Count;
    }, 0);

    /// <summary>Dọn job stale/failed cũ hơn <paramref name="olderThan"/> (kiến trúc: giữ cho audit, dọn theo lịch ~30 ngày).
    /// Bảng audit giữ nguyên.</summary>
    /// <returns>Số job đã xoá.</returns>
    public int PurgeOldJobs(TimeSpan olderThan) => InJobTransaction((tx, now) =>
    {
        long cutoff = now - (long)olderThan.TotalSeconds;
        const string OldJobs = "SELECT job_id FROM llm_job WHERE status IN ('stale', 'failed') AND updated_utc < $cutoff";

        // Xoá llm_result (nếu có) trước, vì khoá ngoại llm_result.job_id → llm_job đang bật
        using (var cmd = Command(tx, $"DELETE FROM llm_result WHERE job_id IN ({OldJobs});"))
        {
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }

        int deleted;
        using (var cmd = Command(tx, "DELETE FROM llm_job WHERE status IN ('stale', 'failed') AND updated_utc < $cutoff;"))
        {
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            deleted = cmd.ExecuteNonQuery();
        }

        if (deleted > 0)
        {
            Audit(tx, now, "jobs_purged", null, $"{deleted} job stale/failed cũ hơn {olderThan.TotalDays:0} ngày");
        }
        return deleted;
    }, 0);

    /// <summary>[Thay đổi] Dọn dẹp theo lịch: xoá dòng audit cũ hơn <paramref name="olderThan"/> (bảng audit tăng mãi vì mỗi
    /// bước chuyển job ghi 1 dòng). Nên giữ lâu hơn job để còn truy vết được job đã bị xoá.</summary>
    /// <returns>Số dòng đã xoá.</returns>
    public int PurgeOldAudit(TimeSpan olderThan) => InJobTransaction((tx, now) =>
    {
        using var cmd = Command(tx, "DELETE FROM audit WHERE ts_utc < $cutoff;");
        cmd.Parameters.AddWithValue("$cutoff", now - (long)olderThan.TotalSeconds);
        int deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
        {
            // Dòng này mới (ts = now) nên không bị lần dọn sau xoá ngay → luôn còn dấu vết của lần dọn gần nhất
            Audit(tx, now, "audit_purged", null, $"{deleted} dòng cũ hơn {olderThan.TotalDays:0} ngày");
        }
        return deleted;
    }, 0);

    /// <summary>Đếm số job theo trạng thái (để log/giám sát). Trạng thái không có job nào sẽ không có trong kết quả.</summary>
    public IReadOnlyDictionary<string, int> CountJobsByStatus() => InJobTransaction<IReadOnlyDictionary<string, int>>((tx, _) =>
    {
        var counts = new Dictionary<string, int>();
        using var cmd = Command(tx, "SELECT status, COUNT(*) FROM llm_job GROUP BY status;");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }
        return counts;
    }, new Dictionary<string, int>());

    // Gọi từ SaveScan (cùng transaction với scan_result — không bao giờ có file provisional mà thiếu job):
    //  1) Job pending/running của file này nhưng file_hash KHÁC (nội dung đã đổi) → stale.
    //  2) Có yêu cầu LLM (priority != null) và chưa có job nào (khác stale) cho đúng (file_id, file_hash) → tạo job pending.
    //     Đã có job pending/running/done/failed cho cùng nội dung → không tạo trùng.
    // [Thay đổi] Hạn ngạch: bước 2 đi qua EnqueueWithQuota; trả kết quả để nơi gọi log
    private JobEnqueueOutcome SyncLlmJob(SqliteTransaction tx, long fileId, string fileHash, int? priority, long now)
    {
        var staleIds = new List<string>();
        using (var cmd = Command(tx, """
            UPDATE llm_job SET status = 'stale', updated_utc = $now
            WHERE file_id = $id AND status IN ('pending', 'running') AND file_hash <> $hash
            RETURNING job_id;
            """))
        {
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.Parameters.AddWithValue("$hash", fileHash);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                staleIds.Add(reader.GetString(0));
            }
        }
        foreach (var id in staleIds)
        {
            Audit(tx, now, "job_stale", id, "→stale, file đã đổi (quét lại)");
        }

        if (priority is not int p)
        {
            return JobEnqueueOutcome.None;
        }

        using (var cmd = Command(tx, """
            SELECT 1 FROM llm_job WHERE file_id = $id AND file_hash = $hash AND status <> 'stale' LIMIT 1;
            """))
        {
            cmd.Parameters.AddWithValue("$id", fileId);
            cmd.Parameters.AddWithValue("$hash", fileHash);
            if (cmd.ExecuteScalar() != null)
            {
                return JobEnqueueOutcome.Duplicate;
            }
        }

        return EnqueueWithQuota(tx, fileId, fileHash, p, now);
    }

    // [Thay đổi] Hạn ngạch (kiến trúc mục 4.8): kiểm tra hàng đợi đầy + hạn ngạch rồi mới tạo job. Chạy trong transaction của
    // nơi gọi nên đếm + tạo là 1 khối, không bị luồng khác chen vào làm vượt hạn ngạch.
    //  - Hàng đợi đầy (pending ≥ MaxPendingJobs): bỏ P2/P3 mới; P1 vẫn nhận.
    //  - P2/P3: đếm job cùng nhóm TẠO trong 24 giờ gần nhất; đủ → bỏ.
    //  - P1: hạn ngạch = số job được XẾP LỊCH CHẠY trong mỗi giờ (theo next_try_utc). Giờ hiện tại đủ → xếp vào giờ kế tiếp
    //    còn chỗ (hoãn, không bỏ). Đếm theo next_try_utc (không theo created_utc) để job hoãn dàn đều ra các giờ sau thay vì dồn
    //    hết vào đầu giờ kế tiếp.
    private JobEnqueueOutcome EnqueueWithQuota(SqliteTransaction tx, long fileId, string fileHash, int priority, long now)
    {
        if (priority != LlmJobPriority.GreyZone)
        {
            long pending = CountScalar(tx, "SELECT COUNT(*) FROM llm_job WHERE status = 'pending';");
            if (pending >= _quota.MaxPendingJobs)
            {
                Audit(tx, now, "job_dropped_queue_full", null, $"file_id={fileId}, priority={priority}, pending={pending}");
                return JobEnqueueOutcome.DroppedQueueFull;
            }

            int limit = priority == LlmJobPriority.Suspicious ? _quota.P2PerDay : _quota.P3PerDay;
            long used = CountScalar(tx, "SELECT COUNT(*) FROM llm_job WHERE priority = $p AND created_utc > $since;",
                ("$p", priority), ("$since", now - 86_400));
            if (used >= limit)
            {
                Audit(tx, now, "job_dropped_quota", null, $"file_id={fileId}, priority={priority}, đã tạo {used}/{limit} trong 24 giờ");
                return JobEnqueueOutcome.DroppedQuota;
            }

            EnqueueJob(tx, fileId, fileHash, priority, now, now);
            return JobEnqueueOutcome.Created;
        }

        // P1: tìm giờ đầu tiên (từ giờ hiện tại) còn chỗ
        long currentHour = now / 3600;
        var perHour = new Dictionary<long, long>();
        using (var cmd = Command(tx, """
            SELECT next_try_utc / 3600, COUNT(*) FROM llm_job
            WHERE priority = $p AND next_try_utc >= $from GROUP BY next_try_utc / 3600;
            """))
        {
            cmd.Parameters.AddWithValue("$p", LlmJobPriority.GreyZone);
            cmd.Parameters.AddWithValue("$from", currentHour * 3600);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                perHour[reader.GetInt64(0)] = reader.GetInt64(1);
            }
        }

        // P1PerHour ≤ 0 (cấu hình sai) → coi như 1/giờ để vẫn không bỏ file vùng xám
        int p1Limit = Math.Max(1, _quota.P1PerHour);
        long hour = currentHour;
        while (perHour.GetValueOrDefault(hour) >= p1Limit)
        {
            hour++;
        }

        if (hour == currentHour)
        {
            EnqueueJob(tx, fileId, fileHash, priority, now, now);
            return JobEnqueueOutcome.Created;
        }

        long nextTry = hour * 3600;
        EnqueueJob(tx, fileId, fileHash, priority, now, nextTry,
            $", vượt {p1Limit} job/giờ → hoãn {(hour - currentHour)} giờ");
        return JobEnqueueOutcome.Deferred;
    }

    // [Thay đổi] Đếm nhanh 1 giá trị với tham số tuỳ chọn
    private long CountScalar(SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = Command(tx, sql);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // Tạo 1 job pending mới
    // [Thay đổi] Hạn ngạch: nhận nextTryUtc (P1 bị hoãn → đầu giờ kế tiếp còn chỗ) + ghi chú audit
    private void EnqueueJob(SqliteTransaction tx, long fileId, string fileHash, int priority, long now, long nextTryUtc,
        string auditNote = "")
    {
        string jobId = Guid.NewGuid().ToString();
        using var cmd = Command(tx, """
            INSERT INTO llm_job (job_id, file_id, file_hash, priority, status, attempts, next_try_utc, created_utc, updated_utc)
            VALUES ($job, $id, $hash, $priority, 'pending', 0, $next, $now, $now);
            """);
        cmd.Parameters.AddWithValue("$job", jobId);
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.Parameters.AddWithValue("$hash", fileHash);
        cmd.Parameters.AddWithValue("$priority", priority);
        cmd.Parameters.AddWithValue("$next", nextTryUtc);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
        Audit(tx, now, "job_created", jobId, $"→{LlmJobStatus.Pending}, priority={priority}{auditNote}");
    }

    // Lỗi tạm hoặc sau timeout: còn lượt thử lại → pending + chờ backoff theo lần thử; hết lượt → failed.
    // attempts = số lần đã thử lại: 0 → thử lại lần 1 sau 1 phút, 1 → lần 2 sau 5 phút, 2 → lần 3 sau 20 phút, 3 → failed.
    private bool RetryOrFail(SqliteTransaction tx, string jobId, string from, long now, string reason)
    {
        int? attempts;
        using (var cmd = Command(tx, "SELECT attempts FROM llm_job WHERE job_id = $job AND status = $from;"))
        {
            cmd.Parameters.AddWithValue("$job", jobId);
            cmd.Parameters.AddWithValue("$from", from);
            object? value = cmd.ExecuteScalar();
            attempts = value == null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        if (attempts is not int tried)
        {
            return false;
        }

        if (tried < MaxJobAttempts)
        {
            var wait = RetryBackoff[tried];
            return TryTransition(tx, jobId, from, LlmJobStatus.Pending, now, "job_retry",
                $", {reason}, attempts={tried + 1}, chờ {wait.TotalMinutes:0} phút",
                attempts: tried + 1, nextTryUtc: now + (long)wait.TotalSeconds);
        }

        // [Thay đổi] Mục 7.2 "Timeout, lỗi, LLM tắt → status=final; giữ điểm regex": trước đây chỉ đổi llm_job.status,
        // không đụng scan_result → file kẹt vĩnh viễn ở "provisional" dù LLM đã bỏ cuộc hẳn (hết MaxJobAttempts lần
        // thử). needs_review GIỮ NGUYÊN như Pass 1 đã đặt (Router đã bật true cho P1/vùng xám từ trước, đúng ý
        // "needs_review=1 nếu thuộc P1" — không cần tính lại ở đây).
        bool moved = TryTransition(tx, jobId, from, LlmJobStatus.Failed, now, "job_failed",
            $", {reason}, đã thử lại {tried} lần");
        if (moved)
        {
            MarkScanFinalAfterJobFailed(tx, jobId, now);
        }
        return moved;
    }

    // [Thay đổi] Mục 7.2: chốt scan_result.status='final' cho ĐÚNG (file_id, file_hash) của job vừa failed hẳn — dùng
    // sub-query thay vì SELECT riêng vì llm_job vẫn còn nguyên dòng (chỉ đổi status, không xoá) nên tra lại được ngay.
    private void MarkScanFinalAfterJobFailed(SqliteTransaction tx, string jobId, long now)
    {
        using var cmd = Command(tx, """
            UPDATE scan_result SET status = $final, updated_utc = $now
            WHERE file_id = (SELECT file_id FROM llm_job WHERE job_id = $job)
              AND file_hash = (SELECT file_hash FROM llm_job WHERE job_id = $job);
            """);
        cmd.Parameters.AddWithValue("$final", StatusFinal);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$job", jobId);
        cmd.ExecuteNonQuery();
    }

    // Đổi trạng thái 1 job NẾU nó đang ở `from` (+ ghi audit). false = job không ở trạng thái đó → bước chuyển không hợp lệ.
    private bool TryTransition(SqliteTransaction tx, string jobId, string from, string to, long now, string auditEvent,
        string detail, int? attempts = null, long? nextTryUtc = null)
    {
        using (var cmd = Command(tx, """
            UPDATE llm_job SET status = $to, updated_utc = $now,
              attempts = COALESCE($attempts, attempts), next_try_utc = COALESCE($next, next_try_utc)
            WHERE job_id = $job AND status = $from;
            """))
        {
            cmd.Parameters.AddWithValue("$to", to);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$attempts", (object?)attempts ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$next", (object?)nextTryUtc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$job", jobId);
            cmd.Parameters.AddWithValue("$from", from);
            if (cmd.ExecuteNonQuery() == 0)
            {
                return false;
            }
        }

        Audit(tx, now, auditEvent, jobId, $"{from}→{to}{detail}");
        return true;
    }

    private string? GetJobStatus(SqliteTransaction tx, string jobId)
    {
        using var cmd = Command(tx, "SELECT status FROM llm_job WHERE job_id = $job;");
        cmd.Parameters.AddWithValue("$job", jobId);
        return cmd.ExecuteScalar() as string;
    }

    // Ghi 1 dòng audit. detail chỉ chứa mã/trạng thái/số — KHÔNG đưa nội dung hay giá trị trong file vào.
    private void Audit(SqliteTransaction tx, long now, string auditEvent, string? jobId, string detail)
    {
        using var cmd = Command(tx, "INSERT INTO audit (ts_utc, event, job_id, detail) VALUES ($ts, $event, $job, $detail);");
        cmd.Parameters.AddWithValue("$ts", now);
        cmd.Parameters.AddWithValue("$event", auditEvent);
        cmd.Parameters.AddWithValue("$job", (object?)jobId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detail", detail);
        cmd.ExecuteNonQuery();
    }

    // Chạy 1 thao tác job trong khoá + transaction (lỗi → ROLLBACK rồi ném lại). DB đã đóng (service đang dừng) → trả
    // whenDisposed thay vì lỗi. now = thời điểm hiện tại (giây Unix), dùng chung cho mọi câu lệnh trong thao tác.
    private T InJobTransaction<T>(Func<SqliteTransaction, long, T> work, T whenDisposed)
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return whenDisposed;
            }

            using var transaction = _connection.BeginTransaction();
            try
            {
                T result = work(transaction, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                transaction.Commit();
                return result;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}
