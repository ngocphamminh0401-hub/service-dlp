// [Thay đổi] File mới (buổi 15): tách từ DlpConcurrencyBench để mọi công cụ đo dùng chung (mỗi csproj thêm
// <Compile Include="..\..\shared\PowerThrottling.cs" />).
// [Sửa lỗi đo] Windows 11 tự hạ xung (EcoQoS — "chế độ tiết kiệm" cho tiến trình nền, ưu tiên xếp vào nhân E) với tiến trình chạy
// không có cửa sổ ở tiền cảnh. Lượt đo 29/10 chạy nền: 1 luồng chỉ 76 file/giây, CPU = đúng 1 lõi chạy kín (so với 262–286 file/giây
// sau khi tắt) → số đo phụ thuộc cách khởi động, không phải code. Gọi Disable() ĐẦU TIÊN trong Main.
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DlpTools.Shared;

internal static class PowerThrottling
{
    /// <summary>Tự xin thoát chế độ hạ xung (không cần quyền admin) và in kết quả ra console.</summary>
    public static void Disable()
    {
        // ControlMask = loại hạ xung muốn điều khiển (EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION), StateMask = 0 nghĩa là TẮT
        var state = new State { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        bool ok = SetProcessInformation(Process.GetCurrentProcess().Handle, 4 /* ProcessPowerThrottling */, ref state, (uint)Marshal.SizeOf<State>());
        Console.WriteLine(ok ? "Đã tắt hạ xung (EcoQoS) cho tiến trình đo" : $"Không tắt được hạ xung (lỗi {Marshal.GetLastWin32Error()}) — số đo có thể thấp hơn thực tế");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct State { public uint Version; public uint ControlMask; public uint StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref State info, uint size);
}
