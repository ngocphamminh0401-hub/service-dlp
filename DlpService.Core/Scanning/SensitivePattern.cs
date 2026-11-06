// [Thay đổi] File mới (bước 4.1): mô tả 1 mẫu dữ liệu nhạy cảm cần tìm
using System.Text.RegularExpressions;

namespace DlpService.Scanning;

// [Thay đổi] Merger mục 7.2: chính sách dùng ý kiến LLM khi merge S_final, đọc từ rule JSON (field "llm_policy").
/// <summary>Chính sách dùng ý kiến LLM khi merge kết quả (mục 7.2 của kiến trúc).</summary>
public enum LlmPolicy
{
    /// <summary>Luôn dùng ý kiến LLM để điều chỉnh điểm.</summary>
    Always,

    /// <summary>Mặc định: dùng ý kiến LLM (rule vào vùng xám mới thực sự có job gửi LLM).</summary>
    VerifyIfAmbiguous,

    /// <summary>Bỏ qua hoàn toàn ý kiến LLM cho rule này — không xác nhận, không bác bỏ, không đổi điểm.</summary>
    Never
}

/// <summary>1 rule đã kiểm tra hợp lệ và sẵn sàng để RegexScanner dùng quét.</summary>
public class SensitivePattern
{
    /// <summary>Tên rule, ví dụ "CCCD".</summary>
    public required string Name { get; init; }

    /// <summary>Regex đã biên dịch sẵn (Compiled), có timeout để tránh treo.</summary>
    public required Regex Regex { get; init; }

    /// <summary>Mức độ nghiêm trọng, ví dụ "High".</summary>
    public required string Severity { get; init; }

    /// <summary>Hàm kiểm tra bổ sung sau khi regex khớp (tuỳ chọn); trả về false thì bỏ chỗ khớp đó.</summary>
    public Func<string, bool>? Validator { get; init; }

    // [Thay đổi] Cài validator: chỉ validator checksum (Luhn/MST/IBAN/CCCD) mới được tính là bằng chứng củng cố cho
    // level_gate; bộ lọc (email/SĐT/entropy) thì không — xem ValidatorEntry trong Validators.cs.
    /// <summary>true nếu Validator có chứa ít nhất 1 validator loại checksum.</summary>
    public bool ValidatorIsChecksum { get; init; }

    // [Thay đổi] Bước 5.6: keyword-required matching
    /// <summary>Có bắt buộc từ khoá ngữ cảnh gần đó mới tính là khớp hay không.</summary>
    public bool ContextRequired { get; init; } = false;

    /// <summary>Danh sách từ khoá xác nhận (không phân biệt hoa thường).</summary>
    public List<string> Keywords { get; init; } = new();

    /// <summary>Số ký tự tính từ chỗ khớp ra mỗi bên, để tìm từ khoá xung quanh.</summary>
    public int ContextWindow { get; init; } = 100;

    // [Thay đổi] Bước 5.7: mang theo negation filter và level gate của rule (đọc được, logic áp dụng ở bước sau)
    /// <summary>Negation filter của rule này, null nếu rule không khai báo.</summary>
    public NegationRule? NegationFilter { get; init; }

    /// <summary>Level gate của rule này, null nếu rule không khai báo.</summary>
    public LevelGateRule? LevelGate { get; init; }

    // [Thay đổi] Bậc 3 #1: escalation dùng chung cho mọi pattern con của rule (giống NegationFilter/LevelGate)
    /// <summary>Escalation của rule này, null nếu rule không khai báo.</summary>
    public EscalationRule? Escalation { get; init; }

    // [Thay đổi] Bậc 3 #2: proximity của RIÊNG pattern con này (require_keyword_within) hoặc chung của rule
    // (proximity_window). Chỉ có tác dụng khi ContextRequired = true.
    /// <summary>Yêu cầu từ khoá nằm gần chỗ khớp; null = dùng kiểm tra ContextWindow rộng như cũ.</summary>
    public ProximityRule? Proximity { get; init; }

    // [Thay đổi] Bậc 3 #4: volume escalation dùng chung cho mọi pattern con của rule (đếm gộp theo tên rule)
    /// <summary>Volume escalation của rule này, null nếu rule không khai báo.</summary>
    public VolumeEscalationRule? VolumeEscalation { get; init; }

    // [Thay đổi] Bậc 3 #5: placeholder exclusion dùng chung cho mọi pattern con của rule
    /// <summary>Danh sách giá trị mẫu cần loại, null nếu rule không khai báo.</summary>
    public PlaceholderRule? Placeholder { get; init; }

    // [Thay đổi] Bậc 1 (cải thiện engine): vị trí (0-indexed) của pattern con này trong mảng "patterns" của rule
    // gốc — dùng để NegationRule.AppliesTo/LevelGateRule.AppliesToPattern biết filter/gate có áp dụng cho đúng
    // pattern con này hay không. Rule "dạng cũ" (1 rule = 1 regex) luôn có PatternIdx = 0.
    /// <summary>Vị trí pattern con này trong rule gốc (0-indexed).</summary>
    public int PatternIdx { get; init; }

    // [Thay đổi] Bậc 1 (cải thiện engine): mức độ nghiêm trọng riêng của pattern con này (đọc từ
    // PatternDefinition.OverrideLevel), dùng thay Severity chung của rule khi có giá trị.
    /// <summary>Mức độ nghiêm trọng riêng của pattern con này; null = dùng Severity chung của rule.</summary>
    public string? OverrideLevel { get; init; }

    // [Thay đổi checklist Bậc 2 - "Compound rules"]: trước đây RuleDefinition.Category/Id/Tags chỉ "đọc để tham
    // khảo" rồi dừng ở đó — CompoundRuleModule cần biết category/id/tags của rule đã tạo ra 1 ScanMatch để dựng
    // tập tag (category, ruleId, prefix, category_prefix, custom tag) mới xét được compound rule kiểu "PII + Financial".
    /// <summary>Nhóm rule (category), ví dụ "pii"; null nếu rule không khai.</summary>
    public string? Category { get; init; }

    /// <summary>Mã định danh rule (id), ví dụ "vn_id_001"; null nếu rule không khai (khi đó dùng Name thay thế).</summary>
    public string? RuleId { get; init; }

    // [Thay đổi] Merger mục 7.2: chính sách dùng ý kiến LLM khi merge, mặc định VerifyIfAmbiguous nếu rule không khai "llm_policy".
    /// <summary>Chính sách dùng ý kiến LLM khi merge S_final cho rule này.</summary>
    public LlmPolicy LlmPolicy { get; init; } = LlmPolicy.VerifyIfAmbiguous;

    /// <summary>Nhãn tuỳ chỉnh của rule (tags), dùng cho compound rule và level_gate.required_corroboration_tags.</summary>
    public List<string> Tags { get; init; } = new();

    // [Thay đổi] Bậc 3 #3: dữ liệu để tính độ tin cậy ĐỘNG của 1 chỗ khớp (confidence × weight + boost từ khoá),
    // phục vụ level_gate.min_confidence_bypass. Keywords ở trên là bản gộp primary+secondary (dùng cho
    // context_required), còn boost cần phân biệt 2 nhóm nên giữ riêng thêm ở đây.
    /// <summary>Độ tin cậy gốc của pattern con (0-1); null nếu JSON không khai (khi đó không tính bypass).</summary>
    public double? Confidence { get; init; }

    /// <summary>Trọng số của rule (0-1); mặc định 1.0 nếu JSON không khai.</summary>
    public double Weight { get; init; } = 1.0;

    // [Thay đổi] Công thức Go: boost theo khoảng cách (xem CandidateScorer.ZoneBoost), không còn cố định +0.15/+0.05
    /// <summary>Từ khoá chính, mỗi lần xuất hiện gần chỗ khớp +0.15/+0.10/+0.05 tuỳ khoảng cách.</summary>
    public List<string> PrimaryKeywords { get; init; } = new();

    /// <summary>Từ khoá phụ, mỗi lần xuất hiện gần chỗ khớp +0.07/+0.05/+0.02 tuỳ khoảng cách.</summary>
    public List<string> SecondaryKeywords { get; init; } = new();

    // [Thay đổi] Công thức Go: false_positive_reduction.cvv_expiry_boost (rule thẻ tín dụng) — trước đây JSON có khai
    // nhưng code không đọc nên bị bỏ qua
    /// <summary>Điểm cộng khi quanh chỗ khớp có cả CVV lẫn ngày hết hạn; 0 = rule không khai.</summary>
    public double CvvExpiryBoost { get; init; }

    // [Thay đổi] Chẩn đoán nhánh B: 2 trường hợp CHỈ để đếm counter proximity_reject đúng định nghĩa Go (Go chỉ đếm cho rule
    // khai exclude_if_no_keywords). KHÔNG dùng để lọc chỗ khớp — việc lọc vẫn theo ContextRequired/Proximity như cũ.
    /// <summary>Rule khai false_positive_reduction.exclude_if_no_keywords = true.</summary>
    public bool ExcludeIfNoKeywords { get; init; }

    /// <summary>proximity_window cấp RULE (khác Proximity có thể là require_keyword_within của riêng pattern con); null nếu
    /// rule không khai.</summary>
    public ProximityRule? RuleProximity { get; init; }
}
