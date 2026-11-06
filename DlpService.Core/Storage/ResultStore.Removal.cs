// [Thay đổi] File mới (GĐ3 bước 3.1, rủi ro R4): xoá bản ghi của file đã bị xoá / đổi tên / chuyển ra khỏi vùng theo dõi. Trước đây DB giữ
// mãi bản ghi đường dẫn cũ → báo cáo "file nhạy cảm" chỉ tới file không còn tồn tại, đổi tên thì 1 file thành 2 bản ghi.
// Xoá HẲN (không đánh dấu) để DB luôn phản ánh hiện trạng trên đĩa; ghi 1 dòng audit chỉ có SỐ LƯỢNG (không có đường dẫn).
// Một đường dẫn có thể là thư mục (sự kiện xoá không còn biết là file hay thư mục — xem IFileChangeSource.FileDeleted) → xoá luôn mọi bản ghi
// nằm dưới nó. Dùng so sánh khoảng (path > "dir/" AND path < "dir0") để tận dụng index UNIQUE(path): '0' là ký tự ngay sau '/' trong bảng
// mã (Windows: ']' ngay sau '\').
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DlpService.Storage;

public sealed partial class ResultStore
{
    /// <summary>Xoá bản ghi của đường dẫn này VÀ mọi đường dẫn bên dưới nó (nếu là thư mục), ở mọi bảng (file_state, scan_result, finding,
    /// llm_job, llm_result). Không có bản ghi nào → không ghi gì.</summary>
    /// <returns>Số file đã xoá bản ghi.</returns>
    /// <exception cref="SqliteException">Lỗi DB — đã ROLLBACK.</exception>
    public int RemovePath(string path)
    {
        var (lo, hi) = SubtreeRange(path);
        lock (_writeLock)
        {
            if (_disposed)
            {
                return 0;
            }

            // Kiểm tra nhanh trước (đọc index) — đa số sự kiện xoá là file tạm của trình soạn thảo, không có bản ghi → khỏi mở transaction
            using (var probe = _connection.CreateCommand())
            {
                probe.CommandText = "SELECT EXISTS(SELECT 1 FROM file_state WHERE path = $p OR (path > $lo AND path < $hi));";
                probe.Parameters.AddWithValue("$p", path);
                probe.Parameters.AddWithValue("$lo", lo);
                probe.Parameters.AddWithValue("$hi", hi);
                if (Convert.ToInt64(probe.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                {
                    return 0;
                }
            }

            using var transaction = _connection.BeginTransaction();
            try
            {
                const string ids = "SELECT file_id FROM file_state WHERE path = $p OR (path > $lo AND path < $hi)";
                // Thứ tự xoá theo khoá ngoại: bảng con trước, file_state sau cùng
                foreach (var sql in new[]
                {
                    $"DELETE FROM llm_result WHERE job_id IN (SELECT job_id FROM llm_job WHERE file_id IN ({ids}));",
                    $"DELETE FROM llm_job WHERE file_id IN ({ids});",
                    $"DELETE FROM finding WHERE file_id IN ({ids});",
                    $"DELETE FROM scan_result WHERE file_id IN ({ids});",
                })
                {
                    using var cmd = Command(transaction, sql);
                    cmd.Parameters.AddWithValue("$p", path);
                    cmd.Parameters.AddWithValue("$lo", lo);
                    cmd.Parameters.AddWithValue("$hi", hi);
                    cmd.ExecuteNonQuery();
                }

                int removed;
                using (var cmd = Command(transaction, "DELETE FROM file_state WHERE path = $p OR (path > $lo AND path < $hi);"))
                {
                    cmd.Parameters.AddWithValue("$p", path);
                    cmd.Parameters.AddWithValue("$lo", lo);
                    cmd.Parameters.AddWithValue("$hi", hi);
                    removed = cmd.ExecuteNonQuery();
                }

                Audit(transaction, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "file_removed", null, $"{removed} file");
                transaction.Commit();
                return removed;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>Mọi đường dẫn file đang có bản ghi nằm dưới thư mục này (dùng cho quét đối soát: bản ghi nào không còn file trên đĩa thì xoá).</summary>
    public List<string> ListPathsUnder(string folder)
    {
        var (lo, hi) = SubtreeRange(Path.TrimEndingDirectorySeparator(folder));
        lock (_readLock)
        {
            if (_disposed)
            {
                return [];
            }
            using var cmd = _readConnection.CreateCommand();
            cmd.CommandText = "SELECT path FROM file_state WHERE path > $lo AND path < $hi;";
            cmd.Parameters.AddWithValue("$lo", lo);
            cmd.Parameters.AddWithValue("$hi", hi);
            var paths = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                paths.Add(reader.GetString(0));
            }
            return paths;
        }
    }

    // "dir" → ("dir/", "dir0"): mọi chuỗi bắt đầu bằng "dir/" nằm giữa hai mốc này (so sánh nhị phân của SQLite)
    private static (string Lo, string Hi) SubtreeRange(string path)
    {
        char sep = Path.DirectorySeparatorChar;
        string trimmed = Path.TrimEndingDirectorySeparator(path);
        return (trimmed + sep, trimmed + (char)(sep + 1));
    }
}
