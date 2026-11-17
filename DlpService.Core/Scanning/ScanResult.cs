// [Thay đổi] File mới (log timeout, không kết luận khi quét chưa trọn): kết quả của 1 lần quét = danh sách chỗ khớp
// + tên các rule bị quá thời gian. Trước đây Scan() chỉ trả danh sách chỗ khớp và NUỐT timeout im lặng — nơi gọi không
// biết có rule bị bỏ dở, nên file có thể bị log "sạch" dù chưa quét hết (lỗ để né phát hiện bằng cách chèn chuỗi làm
// regex chạy quá lâu).
namespace DlpService.Scanning;

/// <summary>Kết quả 1 lần quét của RegexScanner.</summary>
public class ScanResult
{
    /// <summary>Các chỗ đã khớp (đã qua validator, keyword-required, negation_filter, level_gate). Khi có rule bị
    /// timeout, vẫn gồm các chỗ khớp tìm được TRƯỚC lúc rule đó dừng.</summary>
    public required List<ScanMatch> Matches { get; init; }

    /// <summary>Tên các rule bị quá thời gian (RegexMatchTimeoutException) trong lần quét này, không trùng lặp.</summary>
    public required IReadOnlyList<string> TimedOutPatterns { get; init; }

    // [Thay đổi] M4: hết ngân sách thời gian của CẢ file (RegexScanner.MaxScanTime) → dừng giữa chừng, các rule còn lại chưa chạy
    /// <summary>true nếu lần quét dừng vì hết ngân sách thời gian của cả file (chưa chạy hết mọi rule).</summary>
    public bool BudgetExceeded { get; init; }

    /// <summary>true nếu MỌI rule đều quét xong; false thì kết quả chưa đủ căn cứ để kết luận mức nhạy cảm.</summary>
    public bool IsComplete => TimedOutPatterns.Count == 0 && !BudgetExceeded; // [Thay đổi] M4: + BudgetExceeded

    // [Thay đổi] M4: mã lý do để pipeline ghi scan_result.incomplete_reason (cùng bộ mã Reading.IncompleteReasons). Hết ngân sách được ưu tiên
    // báo vì nó là nguyên nhân dừng cả lần quét.
    /// <summary>"scan_budget" / "regex_timeout"; null nếu quét trọn.</summary>
    public string? IncompleteReason => BudgetExceeded ? "scan_budget" : TimedOutPatterns.Count > 0 ? "regex_timeout" : null;

    // [Thay đổi] FileConfidence 2 nhánh: counter near-miss đếm cùng lúc quét (xem ScanTelemetry)
    /// <summary>Số liệu near-miss của lần quét này.</summary>
    public ScanTelemetry Telemetry { get; init; } = ScanTelemetry.Empty;
}
