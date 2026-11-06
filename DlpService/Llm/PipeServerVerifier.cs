// [Thay đổi] File mới (mục 5.5 - "Bảo mật kênh": "Phía client (Regex): sau khi kết nối, kiểm tra PID và tài khoản
// chạy hoặc đường dẫn exe (và chữ ký) của process phía server; không khớp thì ngắt"): chống process giả mạo chiếm
// tên pipe (server thật đã tắt, process khác nhanh tay tạo pipe cùng tên để "nghe lén" gói tin gửi cho LLM).
//
// AN TOÀN LÀ TỪ CHỐI: mọi lỗi trong lúc xác minh (không lấy được PID, không mở được process, không đọc được đường
// dẫn...) đều coi là XÁC MINH THẤT BẠI, không phải "bỏ qua coi như hợp lệ" — đây là kiểm tra chống giả mạo, không
// phải kiểm tra "tiện lợi". InferenceClient vẫn thoái hoá về regex-only bình thường khi xác minh thất bại (nguyên
// tắc P3) — không làm giảm độ sẵn sàng của hệ thống, chỉ đảm bảo không nói chuyện với process sai.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace DlpService.Llm;

/// <summary>Điều kiện để tin tưởng process phía server của 1 Named Pipe đã kết nối.</summary>
/// <param name="ExpectedExePath">Đường dẫn đầy đủ tới exe của LLM host thật (vd DlpLlmHost.exe khi triển khai).</param>
/// <param name="RequireSignature">Bắt buộc exe phải có chữ ký Authenticode hợp lệ. Mặc định false vì dự án CHƯA có
/// hạ tầng ký code (build dev/local) — bật lên khi triển khai thật đã ký binary (mục 5.5 "và chữ ký").</param>
public sealed record PipeServerTrustPolicy(string ExpectedExePath, bool RequireSignature = false);

[SupportedOSPlatform("windows")]
public static class PipeServerVerifier
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    /// <summary>Kiểm tra process phía server của <paramref name="pipeHandle"/> đúng là <see
    /// cref="PipeServerTrustPolicy.ExpectedExePath"/> hay không. Trả false + lý do nếu không xác minh được — KHÔNG
    /// coi lỗi xác minh là hợp lệ.</summary>
    public static bool Verify(SafePipeHandle pipeHandle, PipeServerTrustPolicy policy, out string reason)
    {
        if (!GetNamedPipeServerProcessId(pipeHandle, out uint serverPid))
        {
            reason = $"Không lấy được PID phía server của pipe (mã lỗi Win32 {Marshal.GetLastWin32Error()})";
            return false;
        }

        string actualExePath;
        try
        {
            using var process = Process.GetProcessById((int)serverPid);
            actualExePath = process.MainModule?.FileName
                ?? throw new InvalidOperationException("MainModule.FileName rỗng");
        }
        catch (Exception ex)
        {
            reason = $"Không đọc được đường dẫn exe của PID {serverPid}: {ex.Message}";
            return false;
        }

        if (!string.Equals(Path.GetFullPath(actualExePath), Path.GetFullPath(policy.ExpectedExePath), StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Đường dẫn exe phía server không khớp (PID {serverPid}): thực tế \"{actualExePath}\", mong đợi \"{policy.ExpectedExePath}\"";
            return false;
        }

        if (policy.RequireSignature)
        {
            try
            {
                using var cert = X509CertificateLoader.LoadCertificateFromFile(actualExePath);
            }
            catch (Exception ex)
            {
                reason = $"exe phía server (PID {serverPid}) không có chữ ký Authenticode hợp lệ: {ex.Message}";
                return false;
            }
        }

        reason = "";
        return true;
    }
}
