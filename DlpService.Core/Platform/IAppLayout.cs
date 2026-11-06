// [Thay đổi] File mới (kế hoạch tách lõi, bước 1): gom "rule / DB / log / model nằm ở đâu" về MỘT nơi.
// Trước đây Worker.cs, Program.cs, ExplainCommand.cs, CompoundRuleModule.cs mỗi nơi tự ghép AppContext.BaseDirectory + tên thư mục.
// Lõi (Core) chỉ hỏi interface này, không tự biết thư mục — để mỗi hệ điều hành đặt thư mục theo cách của mình
// (Windows: cạnh file .exe; Linux sau này: /etc/dlp-service, /var/lib/dlp-service, /var/log/dlp-service).
namespace DlpService.Platform;

/// <summary>Cho biết các thư mục/đường dẫn mặc định mà service dùng. Mọi đường dẫn trả về đều là đường dẫn tuyệt đối.</summary>
public interface IAppLayout
{
    /// <summary>Thư mục chứa các file rule JSON (kể cả thư mục con theo domain và compound.json).</summary>
    string RulesFolder { get; }

    /// <summary>Thư mục ghi file log của service.</summary>
    string LogFolder { get; }

    /// <summary>Đường dẫn DB SQLite mặc định, dùng khi cấu hình "Storage:DbPath" để trống.</summary>
    string DefaultDbPath { get; }

    /// <summary>Đường dẫn file hệ số FileConfidence mặc định, dùng khi "FileConfidence:ModelPath" để trống.</summary>
    string DefaultConfidenceModelPath { get; }

    /// <summary>Đổi đường dẫn lấy từ cấu hình thành đường dẫn tuyệt đối: đã là tuyệt đối thì giữ nguyên,
    /// tương đối thì tính theo gốc của bố cục (Windows: thư mục chứa file chạy, KHÔNG phải thư mục làm việc —
    /// thư mục làm việc của Windows Service là System32).</summary>
    string ResolvePath(string path);
}
