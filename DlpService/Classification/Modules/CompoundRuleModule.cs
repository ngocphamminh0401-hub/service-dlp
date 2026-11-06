// [Thay đổi checklist Bậc 2 - "Compound rules"]: File mới — module thứ 5, kết hợp nhiều category/rule (qua tag)
// để nâng cấp độ phân loại vượt quá những gì 1 rule đơn lẻ tự làm được (VD: hợp đồng + CCCD/hộ chiếu/BHXH/y tế
// = CONFIDENTIAL dù từng thứ riêng lẻ chỉ INTERNAL/CONFIDENTIAL). Thiết kế THUẦN CỘNG THÊM (additive): chỉ đọc
// context.Matches có sẵn (không tự quét lại content), không đụng vào 4 module domain cũ, không cần sửa
// ClassificationEngine.cs (engine tự tìm thấy class này qua reflection như mọi IClassifierModule khác).
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DlpService.Classification.Modules;

/// <summary>Module compound rule: xét TẬP TAG dựng từ mọi ScanMatch trong context, nâng mức nếu đủ tổ hợp tag.</summary>
public class CompoundRuleModule : IClassifierModule
{
    /// <inheritdoc/>
    public string Domain => "Compound";

    // [Thay đổi checklist Bậc 2 - "Compound rules"]: allowlist tag tuỳ chỉnh dùng cho compound rule — CHỈ 2 tag
    // này trong Tags của rule gốc được tính là 1 "tag hợp lệ" cho compound. Không dùng MỌI Tags khai trong rule
    // JSON: nhiều rule tự khai tags cho mục đích thống kê chung (ví dụ "pii" cho toàn bộ 11 rule pii, kể cả
    // rule yếu như vn_name_001/dob_001 confidence thấp) — nếu tính hết sẽ khiến compound rule kích hoạt sai
    // (VD 1 hợp đồng mẫu chỉ có 1 tên minh hoạ cũng bị coi là "Contract + PII" dù không có PII thật nào).
    private static readonly HashSet<string> CustomTagAllowlist = new() { "pii_strong", "financial_personal" };

    private readonly List<CompoundRule> _rules;

    public CompoundRuleModule()
    {
        _rules = LoadCompoundRules(Path.Combine(AppContext.BaseDirectory, "rules", "compound.json"));
    }

    /// <inheritdoc/>
    public ClassificationResult? Classify(ScanContext context)
    {
        if (_rules.Count == 0 || context.Matches.Count == 0)
        {
            return null;
        }

        // [Thay đổi checklist Bậc 2 - "Compound rules"]: dựng tập tag từ TOÀN BỘ ScanMatch của file này. Mỗi tag
        // trỏ tới mức Severity CAO NHẤT trong các ScanMatch mang tag đó — cần cho min_component_level (không chỉ
        // "tag có mặt" mà phải "tag đã đạt mức X thật sự", VD tránh 1 rule bị level_gate hạ xuống CONFIDENTIAL
        // vẫn bị compound "hồi sinh" lên SECRET chỉ vì cùng chunk có tag khác — xem "VNPT email + Classified
        // SECRET doc" trong rules/compound.json).
        var tagLevels = new Dictionary<string, SensitivityLevel>();

        foreach (var match in context.Matches)
        {
            var level = match.Severity.ParseSensitivityLevel();

            // Nguồn tag #1: category (VD "pii")
            if (!string.IsNullOrWhiteSpace(match.Category))
            {
                AddTag(tagLevels, match.Category, level);
            }

            if (!string.IsNullOrWhiteSpace(match.RuleId))
            {
                // Nguồn tag #2: ruleId đầy đủ (VD "vn_id_001")
                AddTag(tagLevels, match.RuleId, level);

                // Nguồn tag #3: prefix — ruleId bỏ hậu tố "_NNN" (VD "vn_id_001" -> "vn_id")
                string prefix = StripTrailingIndex(match.RuleId);
                AddTag(tagLevels, prefix, level);

                // Nguồn tag #4: category_prefix (VD "pii" + "vn_id" -> "pii_vn_id")
                if (!string.IsNullOrWhiteSpace(match.Category))
                {
                    AddTag(tagLevels, $"{match.Category}_{prefix}", level);
                }
            }

            // Nguồn tag #5: tag tuỳ chỉnh trong allowlist (pii_strong, financial_personal), do CHÍNH rule khai
            foreach (var tag in match.Tags)
            {
                if (CustomTagAllowlist.Contains(tag))
                {
                    AddTag(tagLevels, tag, level);
                }
            }
        }

        var reasons = new List<string>();
        var resultLevel = SensitivityLevel.Public;

        foreach (var rule in _rules)
        {
            // [Thay đổi checklist Bậc 2 - "Compound rules"]: MỌI tag trong Conditions phải có mặt (AND)
            if (!rule.Conditions.All(tagLevels.ContainsKey))
            {
                continue;
            }

            // min_component_level: MỖI tag trong Conditions phải đạt tối thiểu mức này, không chỉ "có mặt"
            if (rule.MinComponentLevel != null)
            {
                var minLevel = rule.MinComponentLevel.ParseSensitivityLevel();
                if (rule.Conditions.Any(tag => tagLevels[tag] < minLevel))
                {
                    continue;
                }
            }

            // context_conditions: tag phải CÓ MẶT, bất kể mức — tách riêng khỏi min_component_level dùng chung
            if (rule.ContextConditions is { Count: > 0 } && !rule.ContextConditions.All(tagLevels.ContainsKey))
            {
                continue;
            }

            // exclude_context_patterns: tín hiệu phủ định cấp toàn văn bản (VD "hồ sơ bệnh án" = khám bệnh
            // thường quy, không phải dấu hiệu bất thường) — khớp 1 trong các regex này thì KHÔNG kích hoạt
            // [Thay đổi] Log timeout: trước đây IsMatch timeout thì exception lan lên ClassificationEngine → engine bỏ CẢ
            // module compound cho file này (mất luôn các mức nâng SECRET). Nay timeout = coi như KHÔNG loại trừ: không
            // chắc thì nghiêng về phía vẫn xét compound rule, không nghiêng về phía bỏ qua.
            if (rule.ExcludeContextPatterns.Any(pattern => IsMatchOrFalseOnTimeout(pattern, context.Content)))
            {
                continue;
            }

            var level = rule.ResultLevel.ParseSensitivityLevel();

            string reason = $"Compound: {rule.Name}";
            if (!string.IsNullOrWhiteSpace(rule.ViolationType))
            {
                reason += $" [violation_type={rule.ViolationType}]";
            }
            if (!string.IsNullOrWhiteSpace(rule.AlertPriority))
            {
                reason += $" [alert_priority={rule.AlertPriority}]";
            }
            reasons.Add(reason);

            if (level > resultLevel)
            {
                resultLevel = level;
            }
        }

        // Không compound rule nào kích hoạt: domain Compound không có ý kiến gì thêm cho file này
        if (reasons.Count == 0)
        {
            return null;
        }

        return new ClassificationResult
        {
            Domain = Domain,
            Level = resultLevel,
            Reasons = reasons
        };
    }

    private static void AddTag(Dictionary<string, SensitivityLevel> tagLevels, string tag, SensitivityLevel level)
    {
        if (!tagLevels.TryGetValue(tag, out var current) || level > current)
        {
            tagLevels[tag] = level;
        }
    }

    // "vn_id_001" -> "vn_id"; hậu tố cuối cùng không phải số (hoặc không có "_") thì giữ nguyên
    private static string StripTrailingIndex(string ruleId)
    {
        int lastUnderscore = ruleId.LastIndexOf('_');
        if (lastUnderscore < 0)
        {
            return ruleId;
        }

        string suffix = ruleId[(lastUnderscore + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsDigit) ? ruleId[..lastUnderscore] : ruleId;
    }

    // [Thay đổi] Log timeout: IsMatch nhưng regex quá thời gian thì trả false (= "không loại trừ") thay vì ném exception
    private static bool IsMatchOrFalseOnTimeout(Regex pattern, string content)
    {
        try
        {
            return pattern.IsMatch(content);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // Nạp rules/compound.json (1 file = MẢNG nhiều compound rule, khác khuôn RuleLoader dùng cho SensitivePattern)
    private static List<CompoundRule> LoadCompoundRules(string filePath)
    {
        var rules = new List<CompoundRule>();

        if (!File.Exists(filePath))
        {
            return rules;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            var defs = JsonSerializer.Deserialize<List<CompoundRuleDefinition>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (defs == null)
            {
                return rules;
            }

            foreach (var def in defs)
            {
                if (string.IsNullOrWhiteSpace(def.Name) || def.Conditions is not { Count: > 0 } || string.IsNullOrWhiteSpace(def.ResultLevel))
                {
                    continue;
                }

                var excludePatterns = new List<Regex>();
                if (def.ExcludeContextPatterns != null)
                {
                    foreach (var pattern in def.ExcludeContextPatterns)
                    {
                        try
                        {
                            excludePatterns.Add(new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(1)));
                        }
                        catch (ArgumentException)
                        {
                            // Regex loại trừ hỏng cú pháp: bỏ qua riêng pattern này, không bỏ cả compound rule
                        }
                    }
                }

                rules.Add(new CompoundRule
                {
                    Name = def.Name,
                    Conditions = def.Conditions,
                    MinComponentLevel = def.MinComponentLevel,
                    ContextConditions = def.ContextConditions,
                    ExcludeContextPatterns = excludePatterns,
                    ResultLevel = def.ResultLevel,
                    ViolationType = def.ViolationType,
                    AlertPriority = def.AlertPriority
                });
            }
        }
        catch (JsonException)
        {
            // compound.json hỏng cú pháp: coi như không có compound rule nào, không chặn service khởi động
        }

        return rules;
    }
}
