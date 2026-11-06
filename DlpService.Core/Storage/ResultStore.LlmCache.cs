// [Thay đổi] File mới (llm_cache + tra cứu llm_result, kiến trúc mục 3.1, 4.8, 9 "cache hai tầng"): phần tách riêng của
// ResultStore (partial class, dùng chung kết nối + khoá). Chưa có worker LLM — các hàm này để worker dùng:
//   Tầng 1 (theo file_hash): cùng NỘI DUNG file đã có kết quả ở cùng model_ver + prompt_ver → dùng lại, khỏi đóng gói.
//   Tầng 2 (theo pack_hash): 2 file khác nhau nhưng GÓI đã chọn đoạn + mask giống hệt (VD cùng mẫu đơn) → dùng lại.
// Kết quả lấy từ cache vẫn đi qua CompleteJob (latency_ms = 0) → mọi job done đều có 1 dòng llm_result, truy vết như nhau.
// Upsert theo khoá chính: llm_result theo job_id, llm_cache theo (pack_hash, prompt_ver, model_ver) → trùng thì GHI ĐÈ bằng
// kết quả mới; gọi lại bao nhiêu lần cũng chỉ 1 dòng (at-least-once + idempotent).
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DlpService.Storage;

public sealed partial class ResultStore
{
    /// <summary>Tầng 2: tìm kết quả LLM đã lưu cho đúng gói (pack_hash) ở đúng phiên bản prompt + model.</summary>
    /// <returns>JSON kết quả; null nếu chưa có (hoặc DB đã đóng).</returns>
    public string? TryGetLlmCache(string packHash, string promptVer, string modelVer) => InJobTransaction((tx, _) =>
    {
        using var cmd = Command(tx, """
            SELECT json_out FROM llm_cache
            WHERE pack_hash = $pack AND prompt_ver = $prompt AND model_ver = $model;
            """);
        cmd.Parameters.AddWithValue("$pack", packHash);
        cmd.Parameters.AddWithValue("$prompt", promptVer);
        cmd.Parameters.AddWithValue("$model", modelVer);
        return cmd.ExecuteScalar() as string;
    }, null);

    /// <summary>Tầng 1: tìm kết quả LLM mới nhất của 1 job ĐÃ DONE có cùng nội dung file (file_hash) ở đúng phiên bản model +
    /// prompt (điều kiện cần số 4 của mục 4.8: "file_hash chưa được xử lý ở cùng model_ver và prompt_ver").</summary>
    /// <returns>Kết quả; null nếu chưa có.</returns>
    public LlmResultRecord? TryGetResultForContent(string fileHash, string modelVer, string promptVer) => InJobTransaction((tx, _) =>
    {
        using var cmd = Command(tx, """
            SELECT r.json_out, r.s2, r.confidence, r.model_ver, r.prompt_ver, r.latency_ms
            FROM llm_result r JOIN llm_job j ON j.job_id = r.job_id
            WHERE j.file_hash = $hash AND j.status = 'done' AND r.model_ver = $model AND r.prompt_ver = $prompt
            ORDER BY r.created_utc DESC LIMIT 1;
            """);
        cmd.Parameters.AddWithValue("$hash", fileHash);
        cmd.Parameters.AddWithValue("$model", modelVer);
        cmd.Parameters.AddWithValue("$prompt", promptVer);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new LlmResultRecord(reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5));
    }, null);

    /// <summary>Xoá mục llm_cache cũ hơn <paramref name="olderThan"/> (tính theo lần ghi gần nhất). Cache không có khoá ngoại
    /// tới bảng nào nên xoá không ảnh hưởng dữ liệu khác; mất cache chỉ làm lần sau phải gọi LLM lại.</summary>
    /// <returns>Số mục đã xoá.</returns>
    public int PurgeLlmCache(TimeSpan olderThan) => InJobTransaction((tx, now) =>
    {
        using var cmd = Command(tx, "DELETE FROM llm_cache WHERE created_utc < $cutoff;");
        cmd.Parameters.AddWithValue("$cutoff", now - (long)olderThan.TotalSeconds);
        int deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
        {
            Audit(tx, now, "llm_cache_purged", null, $"{deleted} mục cũ hơn {olderThan.TotalDays:0} ngày");
        }
        return deleted;
    }, 0);

    /// <summary>Số mục đang có trong llm_cache (để log/giám sát).</summary>
    public long CountLlmCache() => InJobTransaction((tx, _) => CountScalar(tx, "SELECT COUNT(*) FROM llm_cache;"), 0L);

    // Ghi 1 mục cache; trùng (pack_hash, prompt_ver, model_ver) → ghi đè bằng kết quả mới + cập nhật thời điểm (quyết định
    // người dùng: giữ kết quả mới nhất). Gọi từ CompleteJob, cùng transaction.
    private void UpsertLlmCache(SqliteTransaction tx, string packHash, LlmResultRecord result, long now)
    {
        using var cmd = Command(tx, """
            INSERT INTO llm_cache (pack_hash, prompt_ver, model_ver, json_out, created_utc)
            VALUES ($pack, $prompt, $model, $json, $now)
            ON CONFLICT(pack_hash, prompt_ver, model_ver) DO UPDATE SET
              json_out = excluded.json_out, created_utc = excluded.created_utc;
            """);
        cmd.Parameters.AddWithValue("$pack", packHash);
        cmd.Parameters.AddWithValue("$prompt", result.PromptVer);
        cmd.Parameters.AddWithValue("$model", result.ModelVer);
        cmd.Parameters.AddWithValue("$json", result.JsonOut);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    // Kiến trúc mục 3.1: json_out là "JSON đã validate" → chặn chuỗi không phải JSON TRƯỚC khi ghi bất cứ gì. Chỉ kiểm tra
    // cú pháp; kiểm tra đúng schema (các trường bắt buộc) là việc của worker khi nhận output từ LLM.
    private static void EnsureValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // Không đưa nội dung chuỗi vào thông báo lỗi (có thể bị log)
            throw new ArgumentException($"json_out không phải JSON hợp lệ (dài {json.Length} ký tự): {ex.Message}", nameof(json), ex);
        }
    }
}
