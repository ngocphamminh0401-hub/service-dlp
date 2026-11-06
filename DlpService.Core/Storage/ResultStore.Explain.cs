// [Thay đổi] File mới (lệnh chẩn đoán "explain <file_id>"): gộp MỌI thông tin liên quan tới 1 file — file_state,
// scan_result, finding, llm_job/llm_result (nếu có) — thành 1 bản ghi duy nhất để in ra console, phục vụ debug/audit
// khi cần hiểu "vì sao file này lại có S_final/needs_review như vậy". Chỉ đọc, không sửa gì; không có giá trị thật
// của file trong bất kỳ bảng nào nên in ra không vi phạm mục 5 (không log dữ liệu nhạy cảm nguyên văn).
namespace DlpService.Storage;

/// <summary>Toàn bộ thông tin để chẩn đoán 1 file (lệnh "explain &lt;file_id&gt;"). Các trường JobXxx/ModelVer/... là
/// null nếu file chưa từng có job LLM nào khớp file_hash hiện tại.</summary>
public sealed record ExplainRecord(
    long FileId, string Path, long Size, DateTime MtimeUtc, string FileHash,
    double S1, double SFinal, string Status, string Source, bool NeedsReview, bool NeedsOcr,
    string RulesVer, string MergePolicyVer,
    IReadOnlyList<FindingRecord> Findings,
    string? JobId, string? JobStatus, int? JobAttempts, int? JobPriority,
    string? ModelVer, string? PromptVer, double? S2, double? Confidence, int? LatencyMs, string? JsonOut);

public sealed partial class ResultStore
{
    /// <summary>Đọc gộp mọi thông tin của 1 file để chẩn đoán (lệnh "explain"). Tự dùng SQL nội bộ thay vì gọi lại
    /// GetFindings/GetTrace công khai — 2 hàm đó tự khoá _writeLock riêng, gọi lồng vào đây sẽ khoá lồng không cần thiết.</summary>
    /// <returns>null nếu không có file_state/scan_result nào khớp file_id.</returns>
    public ExplainRecord? GetExplain(long fileId)
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return null;
            }

            string path, fileHash, status, source, rulesVer, mergePolicyVer;
            long size, mtimeTicks;
            double s1, sFinal;
            bool needsReview, needsOcr;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT f.path, f.size, f.mtime_utc, f.file_hash,
                           s.s1, s.s_final, s.status, s.source, s.needs_review, s.needs_ocr, s.rules_ver, s.merge_policy_ver
                    FROM file_state f JOIN scan_result s ON s.file_id = f.file_id
                    WHERE f.file_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", fileId);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                path = reader.GetString(0);
                size = reader.GetInt64(1);
                mtimeTicks = reader.GetInt64(2);
                fileHash = reader.GetString(3);
                s1 = reader.GetDouble(4);
                sFinal = reader.GetDouble(5);
                status = reader.GetString(6);
                source = reader.GetString(7);
                needsReview = reader.GetInt64(8) == 1;
                needsOcr = reader.GetInt64(9) == 1;
                rulesVer = reader.GetString(10);
                mergePolicyVer = reader.GetString(11);
            }

            var findings = new List<FindingRecord>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT rule_id, score, validator_passed, hard_rule, count, location FROM finding WHERE file_id = $id;";
                cmd.Parameters.AddWithValue("$id", fileId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    findings.Add(new FindingRecord(
                        reader.GetString(0), reader.GetDouble(1), reader.IsDBNull(2) ? null : reader.GetInt64(2) == 1,
                        reader.GetInt64(3) == 1, reader.GetInt32(4), reader.IsDBNull(5) ? "" : reader.GetString(5)));
                }
            }

            // [Thay đổi] Chỉ lấy job khớp ĐÚNG file_hash hiện tại (cùng lý do đã áp dụng ở GetTrace) — job của nội
            // dung file CŨ (đã bị đánh stale khi file đổi) không liên quan tới scan_result đang xem.
            string? jobId = null, jobStatus = null;
            int? jobAttempts = null, jobPriority = null;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT job_id, status, attempts, priority FROM llm_job
                    WHERE file_id = $id AND file_hash = $hash LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$id", fileId);
                cmd.Parameters.AddWithValue("$hash", fileHash);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    jobId = reader.GetString(0);
                    jobStatus = reader.GetString(1);
                    jobAttempts = reader.GetInt32(2);
                    jobPriority = reader.GetInt32(3);
                }
            }

            string? modelVer = null, promptVer = null, jsonOut = null;
            double? s2 = null, confidence = null;
            int? latencyMs = null;
            if (jobId != null)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT s2, confidence, model_ver, prompt_ver, latency_ms, json_out FROM llm_result WHERE job_id = $job;";
                cmd.Parameters.AddWithValue("$job", jobId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    s2 = reader.IsDBNull(0) ? null : reader.GetDouble(0);
                    confidence = reader.IsDBNull(1) ? null : reader.GetDouble(1);
                    modelVer = reader.GetString(2);
                    promptVer = reader.GetString(3);
                    latencyMs = reader.IsDBNull(4) ? null : reader.GetInt32(4);
                    jsonOut = reader.GetString(5);
                }
            }

            return new ExplainRecord(fileId, path, size, new DateTime(mtimeTicks, DateTimeKind.Utc), fileHash,
                s1, sFinal, status, source, needsReview, needsOcr, rulesVer, mergePolicyVer, findings,
                jobId, jobStatus, jobAttempts, jobPriority, modelVer, promptVer, s2, confidence, latencyMs, jsonOut);
        }
    }
}
