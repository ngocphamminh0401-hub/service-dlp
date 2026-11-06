// [Thay đổi] File mới: log CHẨN ĐOÁN cho MODEL_ERROR / TIMEOUT của LLM host (tài liệu buổi 11: lỗi này chỉ ghi ra stderr,
// mà stderr không đi đâu khi chạy dưới SCM; riêng nhánh "JSON sai sau khi retry" không ghi gì cả).
// Ghi 1 dòng "giờ thật | nội dung" vào <ProgramData>\DlpLlmHost\llm-diag.log (đổi thư mục bằng biến môi trường
// DLP_LLM_DIAG_DIR, dùng khi test). Quy tắc dự án: KHÔNG ghi nội dung file / chunk / văn bản model sinh ra — chỉ ghi
// con số (độ dài, số token, mili giây), mã trạng thái và loại exception. Lỗi ghi log bị nuốt: log không được làm sập job.
// Lưu ý quyền: file do tài khoản nào tạo thì tài khoản đó sở hữu — test bằng tay (tài khoản khác) nên đặt
// DLP_LLM_DIAG_DIR sang thư mục khác, kẻo service không ghi tiếp được vào file do người khác tạo.
using System.Diagnostics;

namespace DlpLlmHost;

public static class DiagLog
{
    private const long MaxBytes = 5 * 1024 * 1024; // vượt 5 MB thì đổi tên thành .old (chỉ giữ 1 bản cũ)
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            string dir = Environment.GetEnvironmentVariable("DLP_LLM_DIAG_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DlpLlmHost");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "llm-diag.log");
            lock (Gate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // cố ý bỏ qua
        }
    }

    /// <summary>RAM (working set) của chính process này, MB — ghi kèm để thấy áp lực bộ nhớ lúc lỗi.</summary>
    public static long WorkingSetMb()
    {
        try
        {
            return Process.GetCurrentProcess().WorkingSet64 / 1048576;
        }
        catch
        {
            return -1;
        }
    }
}
