// [Thay đổi] File mới (FileConfidence 2 nhánh): các counter "suýt" (near-miss) RegexScanner đếm trong lúc quét, port từ
// FileScanTelemetry của engine Go (D:\dlp-classifier\internal\engine\telemetry.go). CHỈ quan sát — không đổi chỗ khớp hay
// mức của file. Dùng làm đầu vào cho Classification/FileConfidence.cs. Không chứa giá trị nhạy cảm nào (chỉ là số đếm).
namespace DlpService.Scanning;

// [Thay đổi] Đoán domain (nhánh B): 1 rule có từ khoá PRIMARY xuất hiện trong file (dù có chỗ khớp hay không)
/// <summary>Rule có ≥1 từ khoá primary trong file.</summary>
/// <param name="RuleKey">RuleId (không có thì tên rule).</param>
/// <param name="Category">Category của rule (VD "hr", "financial"); null nếu không khai.</param>
public sealed record KeywordRuleHit(string RuleKey, string? Category);

/// <summary>Số liệu near-miss của 1 lần quét (cấp file).</summary>
public sealed class ScanTelemetry
{
    /// <summary>Không có số liệu (VD tạo ScanResult thủ công trong test).</summary>
    public static readonly ScanTelemetry Empty = new();

    /// <summary>Số chỗ khớp bị loại vì thiếu từ khoá gần đó (context_required / proximity), CHỈ đếm khi rule đó có từ
    /// khoá ở đâu đó trong file (giống Go: rule không có từ khoá nào thì là "không liên quan", không phải "suýt").</summary>
    public int ProximityRejectCount { get; init; }

    /// <summary>Số chỗ khớp bị level_gate hạ về fallback vì thiếu bằng chứng củng cố.</summary>
    public int LevelGateDowngradeCount { get; init; }

    /// <summary>Số RULE có ≥1 từ khoá primary trong file nhưng không có chỗ khớp nào.</summary>
    public int KeywordHitNoMatchPrimaryRules { get; init; }

    /// <summary>Số dãy 8–19 chữ số đứng riêng (không phải mảnh của dãy dài hơn) KHÔNG nằm trong chỗ khớp nào.</summary>
    public int UnstructuredNumericHits { get; init; }

    /// <summary>File có từ khoá kiểu "ví dụ / template / SOP / biểu mẫu" (danh sách cố định của Go).</summary>
    public bool DocContextDiscount { get; init; }

    /// <summary>Tổng mức "suýt đạt ngưỡng volume": với mỗi rule có volume_escalation mà số chỗ khớp chưa tới bậc thấp
    /// nhất, cộng số_chỗ_khớp / min_count_thấp_nhất.</summary>
    public double IdentifierVolumeNearGate { get; init; }

    /// <summary>Độ dài nội dung đã quét tính theo BYTE UTF-8 (Go dùng byte — các hệ số k/ngưỡng 200 fit theo byte).</summary>
    public long EffectiveLengthBytes { get; init; }

    // [Thay đổi] Đoán domain (nhánh B): file không có chỗ khớp nào vẫn thường nhắc từ khoá của rule theo domain
    // (VD "bệnh án", "hợp đồng lao động") → dùng để đoán domain thay vì rơi thẳng về domain dự phòng
    /// <summary>Các rule có ≥1 từ khoá primary trong file.</summary>
    public IReadOnlyList<KeywordRuleHit> PrimaryKeywordRules { get; init; } = [];

    /// <summary>Mật độ dãy số lạ = UnstructuredNumericHits / (EffectiveLengthBytes / 1000); 0 nếu nội dung rỗng.</summary>
    public double UnstructuredNumericDensity =>
        EffectiveLengthBytes <= 0 ? 0 : UnstructuredNumericHits / (EffectiveLengthBytes / 1000.0);
}
