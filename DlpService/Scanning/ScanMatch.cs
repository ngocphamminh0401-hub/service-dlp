// [Thay đổi] File mới (bước 4.1): kết quả của 1 lần regex khớp trong nội dung file
namespace DlpService.Scanning;
/// <summary>1 chỗ đã khớp trong nội dung file, do RegexScanner tạo ra.</summary>
public class ScanMatch
{
    /// <summary>Tên rule đã khớp, ví dụ "CCCD".</summary>
    public required string PatternName { get; init; }

    /// <summary>Đoạn text đã khớp. Không log nguyên văn, phải che trước khi in.</summary>
    public required string Value { get; init; }

    /// <summary>Vị trí bắt đầu của chỗ khớp trong nội dung file.</summary>
    public required int Index { get; init; }

    // [Thay đổi] Bước 5.7.4: mang theo mức độ nghiêm trọng thật sự của chỗ khớp này.
    // Bình thường bằng Severity của rule; nếu rule có level_gate và không đủ bằng chứng củng cố
    // thì bị hạ xuống FallbackLevel — module phân loại đọc trường này thay vì đoán qua PatternName.
    /// <summary>Mức độ nghiêm trọng của chỗ khớp này (đã áp level_gate nếu có).</summary>
    public required string Severity { get; init; }

    // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: chỗ khớp đã qua validator thuật toán (Luhn,
    // CCCD prefix...) là bằng chứng "cứng" — tự đủ điều kiện củng cố cho level_gate, không cần chờ khớp ≥2 lần.
    /// <summary>True nếu chỗ khớp này đã qua validator thuật toán (không có validator thì luôn false).</summary>
    public bool Validated { get; init; }

    // [Thay đổi checklist Bậc 2 - "Compound rules"]: mang theo category/ruleId/tags của rule gốc để
    // CompoundRuleModule dựng tập tag mà không cần tra ngược lại danh sách rule đã nạp.
    /// <summary>Nhóm rule (category) đã tạo ra chỗ khớp này; null nếu rule không khai.</summary>
    public string? Category { get; init; }

    /// <summary>Mã định danh rule (id) đã tạo ra chỗ khớp này; null nếu rule không khai id.</summary>
    public string? RuleId { get; init; }

    /// <summary>Nhãn tuỳ chỉnh (tags) của rule đã tạo ra chỗ khớp này.</summary>
    public List<string> Tags { get; init; } = new();

    // [Thay đổi] Bậc 3 #7: độ tin cậy ĐỘNG của chỗ khớp, dùng cho min_confidence_bypass.
    // [Thay đổi] Công thức Go: giờ bằng đúng Score (giống Go: bypass so với điểm cuối của match). Null nếu pattern không
    // khai confidence — khi đó không được bypass (Score vẫn có, tính bằng confidence mặc định 0,75).
    /// <summary>Điểm tin cậy 0-1 của chỗ khớp này (= Score); null nếu rule không khai confidence.</summary>
    public double? Confidence { get; init; }

    // [Thay đổi] S1 giai đoạn 1: điểm cấp candidate — LUÔN có giá trị (pattern không khai confidence dùng mặc định).
    // Dùng để tính S1 cấp file (Classification/FileScorer.cs).
    // [Thay đổi] Công thức Go: Score ≥ RegexScanner.MinConfidence với mọi chỗ khớp trả về (chỗ khớp dưới ngưỡng đã bị loại);
    // không còn phạt negation −0,30.
    /// <summary>Điểm candidate 0-1 (xem Scanning/CandidateScorer.cs).</summary>
    public double Score { get; init; }
}
