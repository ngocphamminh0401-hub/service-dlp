// [Thay đổi] File mới (mục 6.4 - Suy luận: "validate JSON sau khi sinh và retry tối đa 1 lần; sau đó trả
// MODEL_ERROR"): parse + validate NGHIÊM NGẶT dòng JSON model trả về cho prompt phân loại 4 mức nhạy cảm
// (PromptBuilder.cs). Sai định dạng, thiếu field, hoặc giá trị ngoài tập đóng đều coi là lỗi — KHÔNG cố "sửa hộ"
// giá trị lạ, để InferenceHostService quyết định thử lại hay báo MODEL_ERROR.
using System.Text.Json;
using Dlp.Inference;

namespace DlpLlmHost;

// [Thay đổi] mục 7 (nợ buổi 9/10): thêm Verdicts — mỗi phần tử là 1 kết luận true/false positive của model cho
// ĐÚNG 1 rule_id mà Pass 1 (regex) đã khớp, để Merger.MergeFile (đã sẵn sàng từ buổi 10) có dữ liệu thật để nâng/hạ
// S_final thay vì luôn giữ nguyên S1. Dùng lại thẳng kiểu Verdict sinh từ inference.proto (giống cách Level/Rule ở
// trên dùng thẳng SensitivityLevel/MatchedRule) — khỏi định nghĩa DTO trung gian.
public sealed record ClassifyOutput(SensitivityLevel Level, MatchedRule Rule, bool NeedReview, string Reason, IReadOnlyList<Verdict> Verdicts);

public static class ClassifyOutputSchema
{
    // [Thay đổi] mục 6.4 "dùng constrained decoding/grammar nếu runtime hỗ trợ" — ONNX Runtime GenAI 0.9.x có
    // GeneratorParams.SetGuidance("json_schema", ...), truyền JSON Schema chuẩn này vào để ép model CHỈ sinh ra
    // đúng khuôn dạng (đóng vai trò chính); ClassifyOutputSchema.TryParse bên dưới vẫn giữ làm lưới an toàn thứ 2
    // (runtime không hỗ trợ, hoặc guidance thất bại âm thầm).
    // [Thay đổi] mục 7: thêm "verdicts" — bắt buộc có mảng (được phép rỗng khi findings rỗng).
    // [Thay đổi] Schema CHẶT (dùng trước, xem ModelHost.LoadAndPreparePrefixCache): thêm biên để model KHÔNG thể "chạy lạc".
    // Log chẩn đoán cho thấy 1 file sinh tới trần 642 token mà JSON vẫn chưa đóng — schema cũ không giới hạn độ dài
    // "reason" (prompt chỉ xin ≤ 15 từ), số phần tử "verdicts", độ dài "rule_id", hay số chữ số của "confidence".
    //   - reason: tối đa 120 ký tự; rule_id: tối đa 40; verdicts: tối đa 16 phần tử.
    //   - confidence: tập giá trị rời rạc (bước 0,05) thay vì "number" tự do — vừa chặn chuỗi chữ số dài vô tận, vừa
    //     chỉ dùng cú pháp "enum" cơ bản nhất, mọi engine ràng buộc đều hỗ trợ. TryParse bên dưới vẫn kiểm [0,1].
    // Nếu runtime từ chối schema này, ModelHost tự lùi về JsonSchema (bản cũ) và ghi log rõ.
    public const string JsonSchemaStrict = """
        {
          "type": "object",
          "properties": {
            "label": { "type": "string", "enum": ["PUBLIC", "INTERNAL", "CONFIDENTIAL", "RESTRICTED"] },
            "matched_rule": { "type": "string", "enum": ["public_published", "internal_ops", "pii_basic", "financial_confidential", "auth_secret", "special_category", "legal_investigation", "ma_strategy"] },
            "need_review": { "type": "boolean" },
            "reason": { "type": "string", "maxLength": 120 },
            "verdicts": {
              "type": "array",
              "maxItems": 16,
              "items": {
                "type": "object",
                "properties": {
                  "rule_id": { "type": "string", "maxLength": 40 },
                  "true_positive": { "type": "boolean" },
                  "confidence": { "enum": [0, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.35, 0.4, 0.45, 0.5, 0.55, 0.6, 0.65, 0.7, 0.75, 0.8, 0.85, 0.9, 0.95, 1] }
                },
                "required": ["rule_id", "true_positive", "confidence"],
                "additionalProperties": false
              }
            }
          },
          "required": ["label", "matched_rule", "need_review", "reason", "verdicts"],
          "additionalProperties": false
        }
        """;

    // [Thay đổi] Bản CHẶT + "x-guidance": { "whitespace_flexible": false } (tùy chọn của llguidance): cấm khoảng trắng/xuống
    // dòng tùy ý giữa các thành phần JSON. Log chẩn đoán: file hr_top_00155 sinh 739 ký tự mà chưa tới trường "reason" (một JSON
    // hợp lệ tới đó chỉ cần ~100 ký tự) — nghi model sinh khoảng trắng liên tục vì schema cho phép. Tài liệu chỉ nêu x-guidance ở
    // ví dụ Lark grammar, không nói rõ cho kiểu json_schema của bản 0.9.2 → ModelHost thử bản này TRƯỚC, bị từ chối thì lùi
    // về JsonSchemaStrict (ghi log rõ lý do). Đầu ra gọn không khoảng trắng vẫn parse bình thường (TryParse không phụ thuộc).
    public static readonly string JsonSchemaStrictCompact =
        "{ \"x-guidance\": { \"whitespace_flexible\": false }," + JsonSchemaStrict.TrimStart().Substring(1);

    public const string JsonSchema = """
        {
          "type": "object",
          "properties": {
            "label": { "type": "string", "enum": ["PUBLIC", "INTERNAL", "CONFIDENTIAL", "RESTRICTED"] },
            "matched_rule": { "type": "string", "enum": ["public_published", "internal_ops", "pii_basic", "financial_confidential", "auth_secret", "special_category", "legal_investigation", "ma_strategy"] },
            "need_review": { "type": "boolean" },
            "reason": { "type": "string" },
            "verdicts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "rule_id": { "type": "string" },
                  "true_positive": { "type": "boolean" },
                  "confidence": { "type": "number" }
                },
                "required": ["rule_id", "true_positive", "confidence"],
                "additionalProperties": false
              }
            }
          },
          "required": ["label", "matched_rule", "need_review", "reason", "verdicts"],
          "additionalProperties": false
        }
        """;

    private static readonly Dictionary<string, SensitivityLevel> LabelMap = new(StringComparer.Ordinal)
    {
        ["PUBLIC"] = SensitivityLevel.Public,
        ["INTERNAL"] = SensitivityLevel.Internal,
        ["CONFIDENTIAL"] = SensitivityLevel.Confidential,
        ["RESTRICTED"] = SensitivityLevel.Restricted,
    };

    private static readonly Dictionary<string, MatchedRule> MatchedRuleMap = new(StringComparer.Ordinal)
    {
        ["public_published"] = MatchedRule.PublicPublished,
        ["internal_ops"] = MatchedRule.InternalOps,
        ["pii_basic"] = MatchedRule.PiiBasic,
        ["financial_confidential"] = MatchedRule.FinancialConfidential,
        ["auth_secret"] = MatchedRule.AuthSecret,
        ["special_category"] = MatchedRule.SpecialCategory,
        ["legal_investigation"] = MatchedRule.LegalInvestigation,
        ["ma_strategy"] = MatchedRule.MaStrategy,
    };

    public static bool TryParse(string rawOutput, out ClassifyOutput? result, out string? error)
    {
        result = null;
        error = null;
        string text = StripMarkdownFence(rawOutput.Trim());

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            error = $"Không parse được JSON: {ex.Message}";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "JSON không phải object";
                return false;
            }
            if (!root.TryGetProperty("label", out var labelEl) || labelEl.ValueKind != JsonValueKind.String
                || !LabelMap.TryGetValue(labelEl.GetString() ?? "", out var level))
            {
                error = "Thiếu/sai field 'label' (phải là PUBLIC|INTERNAL|CONFIDENTIAL|RESTRICTED)";
                return false;
            }
            if (!root.TryGetProperty("matched_rule", out var ruleEl) || ruleEl.ValueKind != JsonValueKind.String
                || !MatchedRuleMap.TryGetValue(ruleEl.GetString() ?? "", out var rule))
            {
                error = "Thiếu/sai field 'matched_rule' (ngoài 8 giá trị đóng)";
                return false;
            }
            if (!root.TryGetProperty("need_review", out var reviewEl)
                || (reviewEl.ValueKind != JsonValueKind.True && reviewEl.ValueKind != JsonValueKind.False))
            {
                error = "Thiếu/sai field 'need_review' (phải là bool)";
                return false;
            }
            if (!root.TryGetProperty("reason", out var reasonEl) || reasonEl.ValueKind != JsonValueKind.String)
            {
                error = "Thiếu/sai field 'reason' (phải là string)";
                return false;
            }
            if (!TryParseVerdicts(root, out var verdicts, out error))
            {
                return false;
            }

            result = new ClassifyOutput(level, rule, reviewEl.GetBoolean(), reasonEl.GetString()!, verdicts!);
            return true;
        }
    }

    // [Thay đổi] mục 7: "verdicts" phải là mảng (rỗng cũng được); mỗi phần tử validate NGHIÊM NGẶT giống các field
    // khác trong file này — sai kiểu hoặc confidence ngoài [0,1] đều coi là lỗi để InferenceHostService retry, KHÔNG
    // tự "sửa hộ" (VD không tự clamp confidence).
    private static bool TryParseVerdicts(JsonElement root, out List<Verdict>? verdicts, out string? error)
    {
        verdicts = null;
        error = null;

        if (!root.TryGetProperty("verdicts", out var verdictsEl) || verdictsEl.ValueKind != JsonValueKind.Array)
        {
            error = "Thiếu/sai field 'verdicts' (phải là mảng)";
            return false;
        }

        var result = new List<Verdict>();
        foreach (var item in verdictsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "Phần tử 'verdicts' phải là object";
                return false;
            }
            if (!item.TryGetProperty("rule_id", out var ruleIdEl) || ruleIdEl.ValueKind != JsonValueKind.String)
            {
                error = "Thiếu/sai 'verdicts[].rule_id' (phải là string, được phép rỗng)";
                return false;
            }
            if (!item.TryGetProperty("true_positive", out var tpEl)
                || (tpEl.ValueKind != JsonValueKind.True && tpEl.ValueKind != JsonValueKind.False))
            {
                error = "Thiếu/sai 'verdicts[].true_positive' (phải là bool)";
                return false;
            }
            if (!item.TryGetProperty("confidence", out var confEl) || confEl.ValueKind != JsonValueKind.Number
                || !confEl.TryGetSingle(out float confidence) || confidence < 0f || confidence > 1f)
            {
                error = "Thiếu/sai 'verdicts[].confidence' (phải là số trong [0,1])";
                return false;
            }

            result.Add(new Verdict { RuleId = ruleIdEl.GetString()!, TruePositive = tpEl.GetBoolean(), Confidence = confidence });
        }

        verdicts = result;
        return true;
    }

    // Prompt đã cấm markdown code fence, nhưng model vẫn có thể bọc "```json ... ```" — bỏ đi trước khi parse,
    // KHÔNG coi đây là lỗi định dạng (khác với JSON sai cấu trúc hoặc giá trị ngoài tập đóng).
    private static string StripMarkdownFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }
        int firstNewline = text.IndexOf('\n');
        int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline ? text[(firstNewline + 1)..lastFence].Trim() : text;
    }
}
