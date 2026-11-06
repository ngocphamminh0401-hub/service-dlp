// [Thay đổi] File mới (mục 4.12 - Worker điều phối job): kiểm tra "máy có đang rảnh không" trước khi gọi LLM (tốn
// CPU, không muốn làm chậm máy người dùng đang thao tác). Chỉ kiểm CPU + pin — CHƯA kiểm "có phiên người dùng đang
// hoạt động" (Windows không có cách đáng tin cậy để 1 Windows Service biết màn hình có đang bị khoá hay không).
//
// Nguyên tắc: đo lỗi (không đọc được CPU/pin, VD thiếu quyền) -> coi như ĐANG RẢNH (fail-open) — đây chỉ là 1 lớp
// "lịch sự" giảm tải máy, không phải yêu cầu an toàn bắt buộc; không được để 1 lỗi đo đạc chặn LLM chạy vô thời hạn.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DlpService.Llm;

/// <summary>Cấu hình ngưỡng "rảnh".</summary>
public sealed class MachineIdleOptions
{
    /// <summary>CPU toàn máy (%) tối đa để coi là rảnh.</summary>
    public double MaxCpuPercent { get; set; } = 50;

    /// <summary>Pin (%) tối thiểu để coi là rảnh — CHỈ áp dụng khi đang chạy bằng pin (laptop rút sạc).</summary>
    public double MinBatteryPercent { get; set; } = 20;
}

/// <summary>Kiểm tra máy có đang rảnh để chạy LLM hay không (CPU thấp, pin không yếu).</summary>
public sealed class MachineIdleChecker(MachineIdleOptions? options = null)
{
    private readonly MachineIdleOptions _options = options ?? new MachineIdleOptions();

    // PerformanceCounter phải giữ 1 instance sống lâu dài và gọi NextValue() nhiều lần — lần gọi ĐẦU TIÊN luôn trả 0
    // (chưa có mốc so sánh), nên giữ instance ở đây thay vì tạo mới mỗi lần kiểm tra.
    private PerformanceCounter? _cpuCounter;
    private bool _cpuCounterFailed;

    /// <summary>Máy có đang rảnh không.</summary>
    /// <param name="reason">Lý do NẾU không rảnh (để log); rỗng nếu đang rảnh.</param>
    public bool IsIdle(out string reason)
    {
        double? cpu = TryReadCpuPercent();
        if (cpu is > 0 && cpu > _options.MaxCpuPercent)
        {
            reason = $"CPU {cpu:0}% > ngưỡng {_options.MaxCpuPercent:0}%";
            return false;
        }

        if (OperatingSystem.IsWindows() && TryReadBattery() is { OnBattery: true } battery
            && battery.Percent is >= 0 and < 100 && battery.Percent < _options.MinBatteryPercent)
        {
            reason = $"Pin {battery.Percent}% < ngưỡng {_options.MinBatteryPercent:0}% (đang chạy bằng pin)";
            return false;
        }

        reason = "";
        return true;
    }

    // Đọc "% Processor Time" của "_Total" (mọi nhân CPU) — cùng cách buổi 6 dùng khi đo hiệu năng (Get-Counter không
    // đọc được CPU của service chạy bằng tài khoản hệ thống nếu không có quyền admin qua Get-Process).
    private double? TryReadCpuPercent()
    {
        if (_cpuCounterFailed || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            _cpuCounter ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
            return _cpuCounter.NextValue();
        }
        catch (Exception)
        {
            // Thiếu quyền / performance counter hỏng — không đo được, coi như không chặn (fail-open)
            _cpuCounterFailed = true;
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static (bool OnBattery, int Percent)? TryReadBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
            {
                return null;
            }
            // ACLineStatus: 0 = đang chạy bằng pin, 1 = có sạc/AC, 255 = không xác định
            bool onBattery = status.ACLineStatus == 0;
            return (onBattery, status.BatteryLifePercent == 255 ? -1 : status.BatteryLifePercent);
        }
        catch (Exception)
        {
            return null; // Máy bàn không có pin, hoặc API lỗi — không chặn
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
