// [Thay đổi] File mới (tích hợp Tika native): cấu hình đọc từ appsettings.json mục "TikaNative".
// Mặc định TẮT: service chạy y như cũ cho tới khi người vận hành bật rõ ràng và ghim mã băm của DLL.
namespace DlpService.Reading.Native;

// [Thay đổi] Tika native chế độ Replace: chọn Tika chỉ BỔ SUNG định dạng cũ hay THAY THẾ toàn bộ bộ đọc.
/// <summary>Cách dùng Tika native khi đã bật.</summary>
public enum TikaNativeMode
{
    /// <summary>Chỉ đọc các định dạng bộ đọc cũ chưa hỗ trợ (.doc, .ppt, .rtf). Mặc định.</summary>
    Supplement,

    /// <summary>Đọc MỌI định dạng (txt, csv, docx, pptx, xlsx, xls, pdf, eml + doc, ppt, rtf) thay cho bộ đọc cũ.</summary>
    Replace
}

/// <summary>Cấu hình bộ đọc file dùng thư viện Tika native (tika_native.dll, biên dịch bằng GraalVM).</summary>
public sealed class TikaNativeOptions
{
    /// <summary>false (mặc định) = không nạp DLL, không hỗ trợ thêm định dạng nào.</summary>
    public bool Enabled { get; set; } = false;

    // [Thay đổi] Tika native chế độ Replace
    /// <summary>Supplement (mặc định) hoặc Replace. Chỉ có tác dụng khi Enabled = true và nạp DLL thành công.</summary>
    public TikaNativeMode Mode { get; set; } = TikaNativeMode.Supplement;

    /// <summary>Đường dẫn tới tika_native.dll. Tương đối thì tính theo thư mục chạy của service (không phải thư mục làm việc,
    /// vì Windows Service chạy với thư mục làm việc là System32). Các DLL phụ của JDK (nếu có) phải nằm cùng thư mục.</summary>
    public string DllPath { get; set; } = Path.Combine("native", "tika", "tika_native.dll");

    /// <summary>Mã băm SHA-256 (hex) của DLL đã được duyệt. Khác mã băm thật thì từ chối nạp — chống DLL bị thay thế,
    /// vì service thường chạy quyền cao và DLL này chạy trong tiến trình của service.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>true (mặc định): khi Enabled mà Sha256 để trống thì KHÔNG nạp (an toàn). false chỉ dùng khi thử nghiệm.</summary>
    public bool RequireSha256 { get; set; } = true;

    /// <summary>Hạn thời gian mỗi file (ms), mặc định 30 giây như ExtractionTimeout của FileReaderService. &lt;= 0: không giới hạn.</summary>
    public int TimeoutMs { get; set; } = 30_000;

    /// <summary>Số ký tự tối đa lấy từ 1 file, mặc định 2.000.000 như MaxExtractedChars của FileReaderService.</summary>
    public int MaxChars { get; set; } = 2_000_000;

    // [Thay đổi] Tối ưu 1a (pool isolate): mỗi isolate GraalVM có heap + bộ dọn rác (Serial GC) RIÊNG. Serial GC dừng MỌI luồng đang
    // gắn vào isolate khi dọn → nhiều file đọc cùng lúc trên 1 isolate phải chờ nhau (đo: ~46 % thời gian ở 16 luồng). Chia ra K isolate
    // thì GC ở isolate này không chặn isolate khác. Đo 16 luồng: K=2 +30–35 % file/s (+145 MB RAM), K=4 +65–75 % (+350 MB RAM).
    /// <summary>Số isolate (1–8, mặc định 1). Mỗi isolate thêm tốn tới ~150 MB RAM khi tải cao.</summary>
    public int Isolates { get; set; } = 1;
}
