// [Thay đổi] File mới (mục 6.1/6.3/8 - "Job Object giới hạn RAM và CPU; nếu vượt RAM thì bị hệ điều hành dừng",
// "Khai thác runtime native (ONNX) -> Process riêng, tài khoản quyền thấp, Job Object, chặn mạng ra ngoài"): áp
// giới hạn RAM + CPU cho CHÍNH tiến trình DlpLlmHost bằng Windows Job Object, và hạ độ ưu tiên CPU (priority thấp)
// — để 1 lần suy luận LLM lỗi/treo/leak không thể chiếm hết tài nguyên máy, ảnh hưởng tới Regex service hay các
// tiến trình khác đang chạy. Best-effort: lỗi ở đây KHÔNG được chặn service khởi động (an toàn hơn là bảo vệ
// tuyệt đối — mục 6.1 coi đây là lớp phòng thủ thêm, không phải điều kiện tiên quyết để chạy).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DlpLlmHost;

[SupportedOSPlatform("windows")]
public static class ResourceLimiter
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    private const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate; // đơn vị: 1/100 %, HARD_CAP -> CpuRate=5000 nghĩa là tối đa 50% tổng CPU toàn máy
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    /// <summary>Gán TIẾN TRÌNH HIỆN TẠI vào 1 Job Object mới, giới hạn tổng RAM và CPU, đồng thời hạ độ ưu tiên
    /// tiến trình. Best-effort — mọi lỗi (Windows cũ không hỗ trợ CPU rate control, tiến trình đã ở trong job khác
    /// không cho breakaway...) chỉ log cảnh báo, KHÔNG ném exception, KHÔNG chặn service chạy tiếp.</summary>
    public static void ApplyLimitsBestEffort(long memoryLimitBytes, int cpuRatePercent)
    {
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[llm-host] Không đặt được priority thấp (bỏ qua, không chặn service): {ex.Message}");
        }

        IntPtr job = IntPtr.Zero;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                Console.Error.WriteLine("[llm-host] CreateJobObject thất bại (bỏ qua giới hạn RAM/CPU).");
                return;
            }

            SetMemoryLimit(job, memoryLimitBytes);
            SetCpuRateLimit(job, cpuRatePercent);

            if (!AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
            {
                Console.Error.WriteLine($"[llm-host] AssignProcessToJobObject thất bại (mã lỗi {Marshal.GetLastWin32Error()}) — có thể tiến trình đã thuộc 1 Job khác không cho breakaway. Bỏ qua giới hạn.");
                return;
            }

            Console.WriteLine($"[llm-host] Đã áp giới hạn Job Object: RAM tối đa {memoryLimitBytes / (1024 * 1024)}MB, CPU tối đa {cpuRatePercent}%, priority=BelowNormal.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[llm-host] Lỗi khi áp Job Object (bỏ qua, không chặn service): {ex.Message}");
        }
    }

    private static void SetMemoryLimit(IntPtr job, long memoryLimitBytes)
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_JOB_MEMORY },
            JobMemoryLimit = (UIntPtr)memoryLimitBytes
        };
        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length))
            {
                Console.Error.WriteLine($"[llm-host] SetInformationJobObject (giới hạn RAM) thất bại, mã lỗi {Marshal.GetLastWin32Error()}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static void SetCpuRateLimit(IntPtr job, int cpuRatePercent)
    {
        var info = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
            CpuRate = (uint)(cpuRatePercent * 100)
        };
        int length = Marshal.SizeOf<JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>();
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectCpuRateControlInformation, ptr, (uint)length))
            {
                // Windows cũ / máy ảo có thể không hỗ trợ CPU rate control -> chỉ cảnh báo, RAM limit vẫn còn hiệu lực
                Console.Error.WriteLine($"[llm-host] SetInformationJobObject (giới hạn CPU) thất bại, mã lỗi {Marshal.GetLastWin32Error()} — bỏ qua giới hạn CPU, vẫn giữ giới hạn RAM.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
