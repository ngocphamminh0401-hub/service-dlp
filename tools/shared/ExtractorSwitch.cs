// [Thay đổi] File mới (đo lại 5 chỉ số với Tika native): dùng chung cho DlpBaseline, DlpSizeBench, DlpConcurrencyBench
// (mỗi csproj thêm <Compile Include="..\..\shared\ExtractorSwitch.cs" />).
// Đọc 2 tham số rồi BỎ chúng khỏi args, để phần đọc tham số riêng của từng công cụ không phải sửa:
//   --extractor legacy|tika   legacy (mặc định) = bộ đọc cũ; tika = Tika native thay cho MỌI định dạng (TikaNative:Mode = Replace)
//   --extractor supplement    [Thay đổi] P/A 1 đúng cấu hình chạy thật: NẠP DLL (tốn RAM như service thật) nhưng bộ đọc cũ giữ
//                             8 định dạng chính, Tika chỉ đọc .doc/.ppt/.rtf (TikaNative:Mode = Supplement)
//   --tika-dll <đường dẫn>    mặc định tools\tika-native\build\tika_native.dll
// PHẢI gọi Apply TRƯỚC khi tạo FileReaderService đầu tiên (FileReaderService chọn extractor ngay trong constructor).
using DlpService.Reading.Native;
using Microsoft.Extensions.Logging;

namespace DlpTools.Shared;

internal static class ExtractorSwitch
{
    /// <summary>"legacy", "tika" hoặc "supplement" — ghi vào tên file kết quả và tiêu đề báo cáo.</summary>
    public static string Name { get; private set; } = "legacy";

    /// <summary>SHA-256 của DLL đã nạp (rỗng khi dùng bộ đọc cũ) — ghi vào báo cáo để biết đã đo bằng bản DLL nào.</summary>
    public static string DllSha256 { get; private set; } = "";

    /// <summary>Đọc --extractor/--tika-dll, cấu hình TikaNativeRuntime; trả args còn lại. Ném exception nếu chọn tika mà không nạp được.</summary>
    public static string[] Apply(string[] args)
    {
        var rest = new List<string>();
        string dll = Path.GetFullPath(Path.Combine("tools", "tika-native", "build", "tika_native.dll"));
        int isolates = 1; // [Thay đổi] Tối ưu 1a: số isolate (--tika-isolates N), mặc định 1 như TikaNativeOptions
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--extractor" && i + 1 < args.Length) Name = args[++i].ToLowerInvariant();
            else if (args[i] == "--tika-dll" && i + 1 < args.Length) dll = Path.GetFullPath(args[++i]);
            else if (args[i] == "--tika-isolates" && i + 1 < args.Length && int.TryParse(args[i + 1], out int k)) { isolates = k; i++; }
            else rest.Add(args[i]);
        }

        if (Name == "legacy")
        {
            return [.. rest];
        }
        if (Name is not ("tika" or "supplement"))
        {
            throw new ArgumentException($"--extractor phải là legacy, tika hoặc supplement (nhận '{Name}')");
        }

        // Công cụ đo: không ghim mã băm (RequireSha256 = false) nhưng vẫn in mã băm thật vào báo cáo
        // [Thay đổi] supplement → Mode = Supplement (P/A 1); tika → Mode = Replace (P/A 2)
        var mode = Name == "tika" ? TikaNativeMode.Replace : TikaNativeMode.Supplement;
        TikaNativeRuntime.Configure(new TikaNativeOptions
        {
            Enabled = true,
            Mode = mode,
            DllPath = dll,
            RequireSha256 = false,
            Isolates = isolates
        }, new ConsoleOnlyWarnings());
        // [Thay đổi] Kiểm tra đúng chế độ đã chọn: Replace phải thay toàn bộ; Supplement phải nạp được DLL nhưng KHÔNG thay
        bool ok = mode == TikaNativeMode.Replace ? TikaNativeRuntime.ReplacesAll : TikaNativeRuntime.IsAvailable && !TikaNativeRuntime.ReplacesAll;
        if (!ok)
        {
            throw new InvalidOperationException($"Chọn --extractor {Name} nhưng không nạp được {dll} đúng chế độ {mode} (xem cảnh báo ở trên)");
        }
        using (var fs = File.OpenRead(dll))
        {
            DllSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs)).ToLowerInvariant();
        }
        Console.WriteLine($"[Extractor] Tika native ({mode}, {isolates} isolate), DLL {dll}, SHA-256 {DllSha256[..16]}…");
        return [.. rest];
    }

    /// <summary>Gọi cuối chương trình: huỷ isolate trên luồng chính (không để lúc thoát tiến trình tự dọn).</summary>
    public static void Shutdown()
    {
        if (Name != "legacy") TikaNativeRuntime.Shutdown(); // [Thay đổi] cả tika lẫn supplement đều đã nạp DLL
    }

    // Logger tối thiểu: chỉ in cảnh báo/lỗi ra console (công cụ đo dùng NullLogger cho phần còn lại để không lệch thời gian)
    private sealed class ConsoleOnlyWarnings : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Console.Error.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }
    }
}
