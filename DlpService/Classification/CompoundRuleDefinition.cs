// [Thay đổi checklist Bậc 2 - "Compound rules"]: File mới — khuôn mẫu khớp 1 phần tử trong rules/compound.json
// (dữ liệu thô, chưa kiểm tra hợp lệ). Đặt ở Classification (không phải Scanning) vì compound rule không quét
// regex trực tiếp trên content — nó chỉ xét TẬP TAG được dựng từ các ScanMatch đã có sẵn (Category/RuleId/Tags/
// Severity), đây là việc của tầng phân loại, không phải tầng quét.
using System.Text.Json.Serialization;

namespace DlpService.Classification;

/// <summary>Khuôn mẫu khớp 1 compound rule trong rules/compound.json (dữ liệu thô, chưa kiểm tra hợp lệ).</summary>
public class CompoundRuleDefinition
{
    /// <summary>Tên compound rule, để log/hiển thị lý do phân loại.</summary>
    public string? Name { get; set; }

    /// <summary>Danh sách tag bắt buộc phải CÓ MẶT (AND) — mỗi tag khớp category, ruleId đầy đủ, prefix (ruleId bỏ
    /// hậu tố "_NNN"), category_prefix ("category_prefix"), hoặc tag tuỳ chỉnh trong allowlist (pii_strong,
    /// financial_personal) mà rule tạo ra ScanMatch đó đã tự khai.</summary>
    [JsonPropertyName("conditions")]
    public List<string>? Conditions { get; set; }

    /// <summary>Mức tối thiểu MỖI điều kiện trong Conditions phải đạt được (dựa trên Severity thật của ScanMatch
    /// mang tag đó, không chỉ "có mặt là đủ"). Null = chỉ cần có mặt, không xét mức.</summary>
    [JsonPropertyName("min_component_level")]
    public string? MinComponentLevel { get; set; }

    /// <summary>Tag bắt buộc phải CÓ MẶT (bất kể mức), tách riêng khỏi Conditions để không bị ràng buộc bởi
    /// MinComponentLevel dùng chung cho cả nhóm Conditions.</summary>
    [JsonPropertyName("context_conditions")]
    public List<string>? ContextConditions { get; set; }

    /// <summary>Regex tín hiệu phủ định ở cấp toàn văn bản — hễ khớp 1 trong các regex này thì compound rule
    /// KHÔNG kích hoạt, dù Conditions/MinComponentLevel đã đủ (dùng loại trừ ngữ cảnh "thường quy", ví dụ hồ sơ
    /// khám bệnh routine không phải dấu hiệu bất thường).</summary>
    [JsonPropertyName("exclude_context_patterns")]
    public List<string>? ExcludeContextPatterns { get; set; }

    /// <summary>Mức kết quả khi compound rule này kích hoạt.</summary>
    [JsonPropertyName("result_level")]
    public string? ResultLevel { get; set; }

    /// <summary>Mã vi phạm để trigger workflow riêng (audit log/alert/ticket). Chỉ để tham khảo, hiển thị trong Reasons.</summary>
    [JsonPropertyName("violation_type")]
    public string? ViolationType { get; set; }

    /// <summary>Mức ưu tiên cảnh báo (CRITICAL/HIGH/MEDIUM). Chỉ để tham khảo, hiển thị trong Reasons.</summary>
    [JsonPropertyName("alert_priority")]
    public string? AlertPriority { get; set; }
}
