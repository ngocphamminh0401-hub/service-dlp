// [Thay đổi] File mới: tách hàm băm nội dung dùng chung (trước đây là hàm riêng trong FileWatcherService) — mục
// 4.12 Worker điều phối job cũng cần băm LẠI nội dung theo ĐÚNG cách này để so với file_hash đã lưu trong llm_job.
using System.Security.Cryptography;
using System.Text;

namespace DlpService.Storage;

/// <summary>Băm nội dung text đã trích — cách duy nhất trong toàn bộ service để tính "file_hash" (Pass 1 lẫn Pass 2
/// của Worker LLM phải dùng CHUNG hàm này, không thì so sánh sẽ luôn lệch).</summary>
public static class ContentHash
{
    /// <summary>SHA-256 của nội dung (UTF-8), dạng hex.</summary>
    public static string Compute(string content)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hashBytes);
    }
}
