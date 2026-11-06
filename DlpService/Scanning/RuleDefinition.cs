// [Thay đổi] File mới (bước 1 hướng A): khuôn mẫu khớp với nội dung 1 file rule JSON
// [Thay đổi] Bước 5.7: mở rộng đầy đủ theo cấu trúc rule financial (id, category, level, weight, priority, tags,
// patterns[] nhiều pattern con, keywords {primary, secondary}, level_gate, negation_filter, false_positive_reduction).
// Các trường cũ (Pattern/Severity/ContextRequired/Keywords cũ/MinContextWindow) vẫn giữ nguyên để 30+ file rule
// dạng "1 rule = 1 regex" đã có từ trước không bị phá.
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

/// <summary>Khuôn mẫu khớp với nội dung 1 file rule JSON (dữ liệu thô, chưa kiểm tra hợp lệ).</summary>
public class RuleDefinition
{
    // ===== Trường mô tả chung (metadata) =====
    /// <summary>Mã định danh rule, ví dụ "fin_account_statement_001". Chỉ để tham khảo, chưa có logic nào dùng.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Tên rule, ví dụ "CCCD" hoặc "FIN-013 Sao kê tài khoản thanh toán cá nhân".</summary>
    public string? Name { get; set; }

    /// <summary>Nhóm rule, ví dụ "financial". Chỉ để tham khảo, chưa có logic nào dùng.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>Trọng số 0-1, ví dụ 0.75. Chỉ để tham khảo, engine hiện tại không tính điểm theo trọng số.</summary>
    [JsonPropertyName("weight")]
    public double? Weight { get; set; }

    /// <summary>Độ ưu tiên, số càng lớn càng ưu tiên. Chỉ để tham khảo, chưa có logic nào dùng.</summary>
    [JsonPropertyName("priority")]
    public int? Priority { get; set; }

    /// <summary>Nhãn phân loại, ví dụ ["financial", "bank_account"]. Chỉ để tham khảo, chưa có logic nào dùng.</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>Rule có đang bật hay không. Mặc định true; đặt "enabled": false trong JSON để tắt.</summary>
    public bool Enabled { get; set; } = true;

    // [Thay đổi] Merger mục 7.2: "Rule có llm_policy: never → Bỏ qua kết quả LLM cho rule đó". Đọc dạng chuỗi thô ở
    // đây (JSON); RuleLoader chuyển sang enum LlmPolicy đã chuẩn hoá, giá trị lạ/thiếu → verify_if_ambiguous (an toàn
    // nhất: vẫn cho LLM góp ý like trước khi có field này).
    /// <summary>Chính sách dùng ý kiến LLM cho rule này: "always" | "verify_if_ambiguous" | "never". Thiếu/giá trị lạ
    /// → mặc định "verify_if_ambiguous".</summary>
    [JsonPropertyName("llm_policy")]
    public string? LlmPolicy { get; set; }

    // ===== Mức độ nghiêm trọng: "level" (rule mới) hoặc "severity" (rule cũ), dùng cái nào có giá trị =====
    /// <summary>Mức độ nghiêm trọng kiểu mới, ví dụ "CONFIDENTIAL".</summary>
    [JsonPropertyName("level")]
    public string? Level { get; set; }

    /// <summary>Mức độ nghiêm trọng kiểu cũ, ví dụ "High". Dùng khi rule chưa có trường "level".</summary>
    public string? Severity { get; set; }

    // ===== Dạng cũ: 1 rule = 1 regex duy nhất (vẫn đọc được, không phá rule đã có) =====
    /// <summary>Chuỗi regex dạng text (dạng cũ), chưa biên dịch.</summary>
    public string? Pattern { get; set; }

    /// <summary>Tên validator tuỳ chọn (dạng cũ, ví dụ "luhn").</summary>
    public string? Validator { get; set; }

    [JsonPropertyName("context_required")]
    public bool ContextRequired { get; set; } = false;

    // ===== Dạng mới: 1 rule = nhiều pattern con =====
    /// <summary>Danh sách pattern con. Có giá trị thì rule ở "dạng mới", RuleLoader sẽ tạo nhiều SensitivePattern.</summary>
    [JsonPropertyName("patterns")]
    public List<PatternDefinition>? Patterns { get; set; }

    // ===== Từ khoá: đọc được cả mảng cũ ["a","b"] lẫn object mới {primary, secondary} =====
    [JsonPropertyName("keywords")]
    [JsonConverter(typeof(KeywordGroupJsonConverter))]
    public KeywordGroup? Keywords { get; set; }

    /// <summary>Số ký tự tính từ chỗ khớp ra mỗi bên, để tìm từ khoá xung quanh. Mặc định 100.</summary>
    [JsonPropertyName("min_context_window")]
    public int MinContextWindow { get; set; } = 100;

    // ===== Các cơ chế mới, chỉ đọc vào cấu trúc — logic áp dụng sẽ viết ở bước sau (5.7.3/5.7.4) =====
    /// <summary>Cấu hình hạ mức nếu rule thiếu bằng chứng củng cố.</summary>
    [JsonPropertyName("level_gate")]
    public LevelGateDefinition? LevelGate { get; set; }

    /// <summary>Cấu hình loại chỗ khớp nếu gần đó có từ phủ định.</summary>
    [JsonPropertyName("negation_filter")]
    public NegationFilterDefinition? NegationFilter { get; set; }

    /// <summary>Cấu hình giảm báo động giả chung (thay thế/bổ sung cho ContextRequired + MinContextWindow ở dạng mới).</summary>
    [JsonPropertyName("false_positive_reduction")]
    public FalsePositiveReductionDefinition? FalsePositiveReduction { get; set; }

    // [Thay đổi] Bậc 3 #1: đọc trường "escalation" (trước đây là dead field, engine bỏ qua)
    /// <summary>Cấu hình nâng mức khi gần chỗ khớp có từ khoá nguy hiểm.</summary>
    [JsonPropertyName("escalation")]
    public EscalationDefinition? Escalation { get; set; }

    // [Thay đổi] Bậc 3 #2: đọc trường "proximity_window" (trước đây là dead field)
    /// <summary>Cấu hình yêu cầu từ khoá nằm GẦN chỗ khớp (chặt hơn min_context_window).</summary>
    [JsonPropertyName("proximity_window")]
    public ProximityWindowDefinition? ProximityWindow { get; set; }

    // [Thay đổi] Bậc 3 #4: đọc trường "volume_escalation" (trước đây là dead field)
    /// <summary>Cấu hình nâng mức khi file chứa hàng loạt giá trị khác nhau của rule này.</summary>
    [JsonPropertyName("volume_escalation")]
    public VolumeEscalationDefinition? VolumeEscalation { get; set; }

    // [Thay đổi] Bậc 3 #5: đọc trường "placeholder_exclusion" (trước đây là dead field)
    /// <summary>Cấu hình loại giá trị mẫu/giả (changeme, 123456, số thẻ test...).</summary>
    [JsonPropertyName("placeholder_exclusion")]
    public PlaceholderExclusionDefinition? PlaceholderExclusion { get; set; }
}
