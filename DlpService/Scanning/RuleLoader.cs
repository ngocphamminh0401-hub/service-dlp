// [Thay đổi] File mới (bước 2 hướng A): tự động nạp các rule regex từ thư mục rules/ (mỗi rule 1 file JSON)
// [Thay đổi] Bước 5.7: hỗ trợ thêm rule "dạng mới" (nhiều pattern con trong 1 rule), bên cạnh rule "dạng cũ"
// (1 rule = 1 regex) đã có từ trước.
using System.Text.Json;
using System.Text.RegularExpressions;
using DlpService.Storage;

namespace DlpService.Scanning;

// (ILogger logger) = primary constructor: nhận logger từ ngoài, dùng được ở mọi hàm trong class
/// <summary>Tự động nạp rule quét (SensitivePattern) từ các file *.json trong 1 thư mục.</summary>
public class RuleLoader(ILogger logger)
{
    // Timeout cho mỗi regex, tránh regex bị treo (ReDoS) làm đứng service
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // Cho phép tên trường trong JSON viết thường ("name") vẫn khớp với thuộc tính C# viết hoa ("Name")
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // [Thay đổi] Merger mục 7.4 "Lưu vết": rules_ver để biết 1 kết quả được tính bằng bộ rule nào, so sánh được khi
    // rule thay đổi theo thời gian. Băm NỘI DUNG (không phải tên/thời gian sửa file) — đổi thứ tự nạp không đổi version,
    // nhưng chỉ 1 ký tự trong rule cũng đổi version (đúng nguyên tắc ContentHash đã dùng cho file_hash).
    /// <summary>Tính phiên bản bộ rule = SHA-256 của nội dung mọi file *.json trong thư mục (đệ quy), nối theo thứ tự
    /// đường dẫn ổn định (OrdinalIgnoreCase) để không phụ thuộc thứ tự liệt kê của hệ điều hành.</summary>
    /// <param name="folderPath">Cùng thư mục truyền cho LoadFromFolder.</param>
    /// <returns>Chuỗi hex rỗng nếu thư mục không tồn tại; file đọc lỗi thì bỏ qua file đó (không làm hỏng cả version).</returns>
    public static string ComputeRulesVersion(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return "";
        }

        var files = Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        var combined = new System.Text.StringBuilder();
        foreach (var file in files)
        {
            try
            {
                combined.Append(File.ReadAllText(file));
            }
            catch (IOException)
            {
                // File đang bị khoá/xoá giữa chừng — bỏ qua, không làm hỏng cả version
            }
            catch (UnauthorizedAccessException)
            {
                // Không có quyền đọc — bỏ qua, cùng lý do
            }
        }

        return ContentHash.Compute(combined.ToString());
    }

    /// <summary>Đọc mọi file *.json trong thư mục, kiểm tra và biên dịch thành rule dùng được ngay.</summary>
    /// <param name="folderPath">Đường dẫn thư mục chứa các file rule JSON.</param>
    /// <returns>Danh sách rule hợp lệ. File hỏng hoặc thiếu trường sẽ bị bỏ qua và ghi log cảnh báo.</returns>
    public List<SensitivePattern> LoadFromFolder(string folderPath)
    {
        var patterns = new List<SensitivePattern>();

        if (!Directory.Exists(folderPath))
        {
            logger.LogWarning("Không tìm thấy thư mục rule: {folder}", folderPath);
            return patterns;
        }

        // [Thay đổi] Quét đệ quy (AllDirectories): cho phép gom rule theo thư mục con đặt tên theo domain
        // (vd rules/hr/, rules/financial/...) thay vì bắt buộc để phẳng hết trong rules/.
        foreach (var filePath in Directory.GetFiles(folderPath, "*.json", SearchOption.AllDirectories))
        {
            // [Thay đổi checklist Bậc 2 - "Compound rules"]: rules/compound.json không phải rule quét regex
            // (1 file = MẢNG nhiều compound rule, đọc riêng bởi CompoundRuleModule) — bỏ qua ở đây để tránh bị
            // hiểu nhầm thành "JSON sai cú pháp" khi RuleLoader cố ép nó vào khuôn RuleDefinition (1 rule/file).
            if (string.Equals(Path.GetFileName(filePath), "compound.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Mỗi file có try/catch riêng: 1 file hỏng chỉ bị bỏ qua, không làm chết cả engine
            try
            {
                string json = File.ReadAllText(filePath);
                var rule = JsonSerializer.Deserialize<RuleDefinition>(json, JsonOptions);

                // Mức độ nghiêm trọng: ưu tiên "level" (dạng mới), thiếu thì lấy "severity" (dạng cũ)
                string? effectiveSeverity = !string.IsNullOrWhiteSpace(rule?.Level) ? rule!.Level : rule?.Severity;

                if (rule == null || string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(effectiveSeverity))
                {
                    logger.LogWarning("Rule thiếu Name/Level(Severity), bỏ qua: {file}", filePath);
                    continue;
                }

                if (!rule.Enabled)
                {
                    logger.LogInformation("Rule {name} đang tắt, bỏ qua", rule.Name);
                    continue;
                }

                if (patterns.Any(p => p.Name == rule.Name))
                {
                    logger.LogError("Trùng tên rule {name}, bỏ qua file {filePath}", rule.Name, filePath);
                    continue;
                }

                // Chuyển negation_filter/level_gate sang dạng "sẵn sàng" (dùng chung cho mọi pattern của rule này)
                // [Thay đổi] Bậc 1: mang thêm Action + AppliesToPatterns thay vì chỉ WindowChars/TriggerWords như trước.
                var negationFilter = rule.NegationFilter == null
                    ? null
                    : new NegationRule
                    {
                        WindowChars = rule.NegationFilter.WindowChars,
                        TriggerWords = rule.NegationFilter.TriggerWords,
                        // Chuẩn hoá: rỗng hoặc giá trị lạ đều coi là "suppress" (hành vi cũ, an toàn nhất)
                        Action = string.Equals(rule.NegationFilter.Action, "downgrade_to_gate", StringComparison.OrdinalIgnoreCase)
                            ? "downgrade_to_gate"
                            : "suppress",
                        AppliesToPatterns = rule.NegationFilter.AppliesToPatterns
                    };

                // [Thay đổi] Bậc 1: downgrade_to_gate cần level_gate.require_corroboration=true để có FallbackLevel
                // hạ về — giống ràng buộc trong tài liệu tham khảo (loadRule). Thiếu thì bỏ qua cả rule thay vì
                // chạy với hành vi không xác định.
                if (negationFilter is { Action: "downgrade_to_gate" } && rule.LevelGate?.RequireCorroboration != true)
                {
                    logger.LogWarning(
                        "Rule {name}: negation_filter.action=downgrade_to_gate yêu cầu level_gate.require_corroboration=true, bỏ qua file: {file}",
                        rule.Name, filePath);
                    continue;
                }

                // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: strict_tags_only=true mà không có
                // required_corroboration_tags thì không biết "chỉ dựa vào tag" là tag nào — cấu hình vô nghĩa,
                // bỏ qua cả rule thay vì âm thầm chạy sai (cùng cách xử lý với ràng buộc downgrade_to_gate ở trên).
                if (rule.LevelGate is { StrictTagsOnly: true } && (rule.LevelGate.RequiredCorroborationTags == null || rule.LevelGate.RequiredCorroborationTags.Count == 0))
                {
                    logger.LogWarning(
                        "Rule {name}: level_gate.strict_tags_only=true yêu cầu required_corroboration_tags không rỗng, bỏ qua file: {file}",
                        rule.Name, filePath);
                    continue;
                }

                // [Thay đổi] Bậc 1: mang thêm AppliesToPatterns/ExemptPatterns thay vì chỉ RequireCorroboration/FallbackLevel như trước.
                // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: mang thêm RequiredCorroborationTags/StrictTagsOnly.
                var levelGate = (rule.LevelGate == null || string.IsNullOrWhiteSpace(rule.LevelGate.FallbackLevel))
                    ? null
                    : new LevelGateRule
                    {
                        RequireCorroboration = rule.LevelGate.RequireCorroboration,
                        FallbackLevel = rule.LevelGate.FallbackLevel,
                        AppliesToPatterns = rule.LevelGate.AppliesToPatterns,
                        ExemptPatterns = rule.LevelGate.ExemptPatterns,
                        RequiredCorroborationTags = rule.LevelGate.RequiredCorroborationTags,
                        StrictTagsOnly = rule.LevelGate.StrictTagsOnly,
                        MinConfidenceBypass = rule.LevelGate.MinConfidenceBypass // [Thay đổi] Bậc 3 #3
                    };

                // [Thay đổi] Bậc 3 #1: chuyển escalation sang dạng "sẵn sàng". Thiếu keywords hoặc escalate_to thì
                // KHÔNG bỏ cả rule (khác negation/gate ở trên) — rule vẫn quét được bình thường, chỉ tắt riêng
                // escalation + log cảnh báo, vì thiếu escalation chỉ làm rule "lỏng hơn", không chạy sai.
                EscalationRule? escalation = null;
                if (rule.Escalation != null)
                {
                    if (rule.Escalation.Keywords is { Count: > 0 } && !string.IsNullOrWhiteSpace(rule.Escalation.EscalateTo))
                    {
                        escalation = new EscalationRule
                        {
                            Keywords = rule.Escalation.Keywords,
                            EscalateTo = rule.Escalation.EscalateTo
                        };
                    }
                    else
                    {
                        logger.LogWarning(
                            "Rule {name}: escalation thiếu keywords hoặc escalate_to, bỏ qua escalation (rule vẫn được nạp): {file}",
                            rule.Name, filePath);
                    }
                }

                // [Thay đổi] Bậc 3 #2: proximity_window cấp rule. max_chars thiếu/≤0 thì bỏ qua proximity (giữ kiểm
                // tra ContextWindow rộng như cũ) + log cảnh báo — cùng cách xử lý "cấu hình hỏng chỉ tắt tính năng đó"
                // như escalation. keyword_scope thiếu/lạ coi như "primary_or_secondary" (lỏng hơn, an toàn cho DLP).
                ProximityRule? ruleProximity = null;
                if (rule.ProximityWindow != null)
                {
                    if (rule.ProximityWindow.MaxChars is > 0)
                    {
                        ruleProximity = new ProximityRule
                        {
                            MaxChars = rule.ProximityWindow.MaxChars.Value,
                            SameSentence = rule.ProximityWindow.SameSentenceRequired,
                            IncludeSecondary = !string.Equals(rule.ProximityWindow.KeywordScope, "primary", StringComparison.OrdinalIgnoreCase),
                            AllowTableFallback = true
                        };
                    }
                    else
                    {
                        logger.LogWarning(
                            "Rule {name}: proximity_window thiếu max_chars hợp lệ, bỏ qua proximity (rule vẫn được nạp): {file}",
                            rule.Name, filePath);
                    }
                }

                // [Thay đổi] Bậc 3 #4: volume_escalation. Chỉ giữ các bậc hợp lệ (min_count > 0 và có escalate_to),
                // sắp xếp min_count GIẢM dần để LevelFor() gặp bậc cao nhất trước. Không còn bậc nào hợp lệ thì tắt
                // riêng volume_escalation + log cảnh báo, rule vẫn được nạp (cùng cách xử lý với escalation).
                VolumeEscalationRule? volumeEscalation = null;
                if (rule.VolumeEscalation != null)
                {
                    var thresholds = (rule.VolumeEscalation.Thresholds ?? new List<VolumeThresholdDefinition>())
                        .Where(t => t.MinCount is > 0 && !string.IsNullOrWhiteSpace(t.EscalateTo))
                        .Select(t => (MinCount: t.MinCount!.Value, EscalateTo: t.EscalateTo!))
                        .OrderByDescending(t => t.MinCount)
                        .ToList();

                    if (thresholds.Count > 0)
                    {
                        volumeEscalation = new VolumeEscalationRule { Thresholds = thresholds };
                    }
                    else
                    {
                        logger.LogWarning(
                            "Rule {name}: volume_escalation không có bậc ngưỡng hợp lệ, bỏ qua volume_escalation (rule vẫn được nạp): {file}",
                            rule.Name, filePath);
                    }
                }

                // [Thay đổi] Bậc 3 #5: placeholder_exclusion. Biên dịch từng regex blocklist riêng — 1 regex sai cú pháp
                // chỉ bỏ ĐÚNG regex đó + log cảnh báo (không bỏ cả rule như lỗi regex quét chính, vì thiếu 1 blocklist
                // chỉ làm rule lỏng hơn). Giá trị test chuẩn hoá chỉ giữ chữ số để so khớp bất kể dấu cách/gạch.
                PlaceholderRule? placeholder = null;
                if (rule.PlaceholderExclusion != null)
                {
                    var blocklist = new List<Regex>();
                    foreach (var blockPattern in rule.PlaceholderExclusion.ValueBlocklistPatterns ?? new List<string>())
                    {
                        try
                        {
                            string finalPattern = rule.PlaceholderExclusion.ValueBlocklistFullMatch ? $"^(?:{blockPattern})$" : blockPattern;
                            blocklist.Add(new Regex(finalPattern, RegexOptions.Compiled, RegexTimeout));
                        }
                        catch (ArgumentException ex)
                        {
                            logger.LogWarning("Rule {name}: regex placeholder sai cú pháp, bỏ qua regex này: {error}", rule.Name, ex.Message);
                        }
                    }

                    var testValues = (rule.PlaceholderExclusion.KnownTestValues ?? new List<string>())
                        .Select(v => new string(v.Where(char.IsDigit).ToArray()))
                        .Where(v => v.Length > 0)
                        .ToHashSet();

                    // [Thay đổi] S1 giai đoạn 1: đọc doc_context_discount_keywords/factor. Hệ số ngoài khoảng (0, 1] thì bỏ
                    // penalty + log cảnh báo (rule vẫn được nạp, chỉ không giảm điểm).
                    var docKeywords = rule.PlaceholderExclusion.DocContextDiscountKeywords ?? new List<string>();
                    double docFactor = rule.PlaceholderExclusion.DocContextDiscountFactor ?? 1.0;
                    if (docKeywords.Count > 0 && (docFactor <= 0 || docFactor > 1))
                    {
                        logger.LogWarning("Rule {name}: doc_context_discount_factor={factor} ngoài khoảng (0, 1], bỏ penalty: {file}",
                            rule.Name, docFactor, filePath);
                        docKeywords = new List<string>();
                        docFactor = 1.0;
                    }

                    if (blocklist.Count > 0 || testValues.Count > 0 || docKeywords.Count > 0)
                    {
                        placeholder = new PlaceholderRule
                        {
                            Blocklist = blocklist,
                            KnownTestValues = testValues,
                            DocDiscountKeywords = docKeywords, // [Thay đổi] S1 giai đoạn 1
                            DocDiscountFactor = docFactor
                        };
                    }
                }

                // Từ khoá và cửa sổ ngữ cảnh: dạng mới ưu tiên false_positive_reduction.min_context_window
                var keywords = rule.Keywords?.All ?? new List<string>();
                int contextWindow = rule.FalsePositiveReduction?.MinContextWindow ?? rule.MinContextWindow;

                // [Thay đổi] Công thức Go: cvv_expiry_boost dùng chung cho mọi pattern con của rule; chặn về 0-1 để JSON
                // gõ nhầm (VD 7 thay vì 0.07) không đẩy mọi số thẻ lên điểm tối đa
                double cvvExpiryBoost = Math.Clamp(rule.FalsePositiveReduction?.CvvExpiryBoost ?? 0, 0, 1);

                // [Thay đổi] Merger mục 7.2: chuyển "llm_policy" thô sang enum đã chuẩn hoá, dùng chung cho mọi pattern con của rule
                var llmPolicy = ParseLlmPolicy(rule.LlmPolicy);

                if (rule.Patterns != null && rule.Patterns.Count > 0)
                {
                    // ===== Dạng mới: 1 rule = nhiều pattern con =====
                    // [Thay đổi] Bậc 3 #1: truyền thêm escalation
                    // [Thay đổi] Bậc 3 #2: truyền thêm ruleProximity
                    // [Thay đổi] Bậc 3 #4: truyền thêm volumeEscalation
                    // [Thay đổi] Bậc 3 #5: truyền thêm placeholder
                    // [Thay đổi] Merger mục 7.2: truyền thêm llmPolicy
                    // [Thay đổi] Công thức Go: truyền thêm cvvExpiryBoost
                    int addedCount = LoadMultiPatternRule(rule, effectiveSeverity, keywords, contextWindow, negationFilter, levelGate, escalation, ruleProximity, volumeEscalation, placeholder, llmPolicy, cvvExpiryBoost, filePath, patterns);
                    if (addedCount > 0)
                    {
                        logger.LogInformation("Đã nạp rule {name} ({count} pattern con) từ {file}", rule.Name, addedCount, filePath);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(rule.Pattern))
                {
                    // ===== Dạng cũ: 1 rule = 1 regex =====
                    var regex = new Regex(rule.Pattern, RegexOptions.Compiled, RegexTimeout);
                    var validator = ResolveValidator(rule.Validator, filePath);
                    if (rule.Validator != null && validator == null)
                    {
                        continue; // ResolveValidator đã log cảnh báo, bỏ qua cả rule
                    }

                    patterns.Add(new SensitivePattern
                    {
                        Name = rule.Name,
                        Regex = regex,
                        Severity = effectiveSeverity,
                        // [Thay đổi] Cài validator: ResolveValidator trả ValidatorEntry (hàm + cờ checksum) thay vì chỉ hàm
                        Validator = validator?.Check,
                        ValidatorIsChecksum = validator?.IsChecksum ?? false,
                        ContextRequired = rule.ContextRequired,
                        Keywords = keywords,
                        ContextWindow = contextWindow,
                        NegationFilter = negationFilter,
                        LevelGate = levelGate,
                        Escalation = escalation, // [Thay đổi] Bậc 3 #1
                        Proximity = ruleProximity, // [Thay đổi] Bậc 3 #2
                        VolumeEscalation = volumeEscalation, // [Thay đổi] Bậc 3 #4
                        Placeholder = placeholder, // [Thay đổi] Bậc 3 #5
                        // [Thay đổi] Bậc 3 #3: rule dạng cũ không có confidence theo pattern → Confidence = null
                        // (không tính bypass), chỉ mang theo weight + từ khoá cho thống nhất.
                        Weight = rule.Weight ?? 1.0,
                        PrimaryKeywords = rule.Keywords?.Primary ?? new List<string>(),
                        SecondaryKeywords = rule.Keywords?.Secondary ?? new List<string>(),
                        // [Thay đổi] Bậc 1: rule dạng cũ chỉ có 1 pattern duy nhất → luôn ở vị trí 0 ("pattern_1")
                        PatternIdx = 0,
                        // [Thay đổi checklist Bậc 2 - "Compound rules"]: mang theo category/id/tags của rule
                        Category = rule.Category,
                        RuleId = rule.Id,
                        Tags = rule.Tags ?? new List<string>(),
                        LlmPolicy = llmPolicy, // [Thay đổi] Merger mục 7.2
                        CvvExpiryBoost = cvvExpiryBoost, // [Thay đổi] Công thức Go
                        // [Thay đổi] Chẩn đoán nhánh B: chỉ phục vụ counter proximity_reject
                        ExcludeIfNoKeywords = rule.FalsePositiveReduction?.ExcludeIfNoKeywords ?? false,
                        RuleProximity = ruleProximity
                    });
                    logger.LogInformation("Đã nạp rule {name} từ {file}", rule.Name, filePath);
                }
                else
                {
                    logger.LogWarning("Rule {name} không có \"pattern\" lẫn \"patterns\", bỏ qua: {file}", rule.Name, filePath);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning("JSON sai cú pháp trong {file}: {error}", filePath, ex.Message);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning("Regex sai cú pháp trong {file}: {error}", filePath, ex.Message);
            }
            catch (IOException ex)
            {
                logger.LogWarning("Không đọc được file {file}: {error}", filePath, ex.Message);
            }
            catch (UnauthorizedAccessException)
            {
                logger.LogWarning("Không có quyền đọc file {file}", filePath);
            }
        }

        return patterns;
    }

    // Tạo 1 SensitivePattern cho mỗi phần tử trong rule.Patterns, cùng chung Name/Severity/negation/gate của rule
    private int LoadMultiPatternRule(
        RuleDefinition rule,
        string severity,
        List<string> ruleKeywords,
        int contextWindow,
        NegationRule? negationFilter,
        LevelGateRule? levelGate,
        EscalationRule? escalation, // [Thay đổi] Bậc 3 #1
        ProximityRule? ruleProximity, // [Thay đổi] Bậc 3 #2
        VolumeEscalationRule? volumeEscalation, // [Thay đổi] Bậc 3 #4
        PlaceholderRule? placeholder, // [Thay đổi] Bậc 3 #5
        LlmPolicy llmPolicy, // [Thay đổi] Merger mục 7.2
        double cvvExpiryBoost, // [Thay đổi] Công thức Go
        string filePath,
        List<SensitivePattern> patterns)
    {
        int added = 0;

        // [Thay đổi] Bậc 1: patIdx phải theo ĐÚNG vị trí trong JSON gốc (0-indexed, khớp "pattern_N" 1-indexed mà
        // level_gate/negation_filter tham chiếu) — kể cả khi 1 pattern con phía trước bị bỏ qua do lỗi regex,
        // patIdx của các pattern con sau đó KHÔNG được dồn lại, nếu không applies_to_patterns sẽ trỏ sai chỗ.
        for (int patIdx = 0; patIdx < rule.Patterns!.Count; patIdx++)
        {
            var patternDef = rule.Patterns[patIdx];

            if (string.IsNullOrWhiteSpace(patternDef.Regex))
            {
                logger.LogWarning("Pattern con thiếu regex trong rule {name}, bỏ qua pattern: {file}", rule.Name, filePath);
                continue;
            }

            Regex regex;
            try
            {
                regex = new Regex(patternDef.Regex, RegexOptions.Compiled, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning("Regex sai cú pháp ở 1 pattern con của rule {name}: {error}", rule.Name, ex.Message);
                continue;
            }

            // Nhiều validator (ví dụ ["luhn"]) kết hợp bằng AND: phải qua hết mới coi là khớp
            // [Thay đổi] Cài validator: là "checksum" nếu có ít nhất 1 validator checksum trong danh sách
            Func<string, bool>? validator = null;
            bool validatorIsChecksum = false;
            if (patternDef.Validators is { Count: > 0 })
            {
                var resolved = new List<ValidatorEntry>();
                bool ok = true;
                foreach (var name in patternDef.Validators)
                {
                    var v = ResolveValidator(name, filePath);
                    if (v == null) { ok = false; break; }
                    resolved.Add(v);
                }
                if (!ok) { continue; }
                validator = value => resolved.All(v => v.Check(value));
                validatorIsChecksum = resolved.Any(v => v.IsChecksum);
            }

            // [Thay đổi] Bậc 3 #2: require_keyword_within của pattern con ưu tiên hơn proximity_window của rule.
            // Dạng này chỉ giới hạn khoảng cách (không xét câu), CHỈ xét từ khoá primary (notes[] của date-of-birth/
            // location-tracking ghi rõ "buộc keyword PRIMARY"), và KHÔNG có chế độ bảng (dùng cho ngày sinh/toạ độ —
            // loại dễ báo nhầm nhất, nên luôn giữ chặt).
            var proximity = patternDef.RequireKeywordWithin is > 0
                ? new ProximityRule
                {
                    MaxChars = patternDef.RequireKeywordWithin.Value,
                    SameSentence = false,
                    IncludeSecondary = false,
                    AllowTableFallback = false
                }
                : ruleProximity;

            patterns.Add(new SensitivePattern
            {
                Name = rule.Name!,
                Regex = regex,
                Severity = severity,
                Validator = validator,
                ValidatorIsChecksum = validatorIsChecksum, // [Thay đổi] Cài validator
                ContextRequired = patternDef.ContextRequired,
                Keywords = ruleKeywords,
                ContextWindow = contextWindow,
                NegationFilter = negationFilter,
                LevelGate = levelGate,
                Escalation = escalation, // [Thay đổi] Bậc 3 #1
                Proximity = proximity, // [Thay đổi] Bậc 3 #2
                VolumeEscalation = volumeEscalation, // [Thay đổi] Bậc 3 #4
                Placeholder = placeholder, // [Thay đổi] Bậc 3 #5
                // [Thay đổi] Bậc 3 #3: confidence riêng của pattern con + weight/từ khoá chung của rule, để tính
                // độ tin cậy động phục vụ min_confidence_bypass.
                Confidence = patternDef.Confidence,
                Weight = rule.Weight ?? 1.0,
                PrimaryKeywords = rule.Keywords?.Primary ?? new List<string>(),
                SecondaryKeywords = rule.Keywords?.Secondary ?? new List<string>(),
                // [Thay đổi] Bậc 1: vị trí thật trong JSON + mức nghiêm trọng riêng (override_level) của pattern con này
                PatternIdx = patIdx,
                OverrideLevel = patternDef.OverrideLevel,
                // [Thay đổi checklist Bậc 2 - "Compound rules"]: mang theo category/id/tags của rule
                Category = rule.Category,
                RuleId = rule.Id,
                Tags = rule.Tags ?? new List<string>(),
                LlmPolicy = llmPolicy, // [Thay đổi] Merger mục 7.2
                CvvExpiryBoost = cvvExpiryBoost, // [Thay đổi] Công thức Go
                // [Thay đổi] Chẩn đoán nhánh B: chỉ phục vụ counter proximity_reject
                ExcludeIfNoKeywords = rule.FalsePositiveReduction?.ExcludeIfNoKeywords ?? false,
                RuleProximity = ruleProximity
            });
            added++;
        }

        return added;
    }

    // [Thay đổi] Merger mục 7.2: "never" tắt hẳn ý kiến LLM cho rule, "always" luôn dùng, còn lại (kể cả thiếu/giá trị lạ)
    // coi là verify_if_ambiguous — mặc định AN TOÀN NHẤT vì giữ đúng hành vi hiện có (LLM vẫn góp ý bình thường).
    private static LlmPolicy ParseLlmPolicy(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "always" => LlmPolicy.Always,
        "never" => LlmPolicy.Never,
        _ => LlmPolicy.VerifyIfAmbiguous
    };

    // Tra tên validator trong bảng; trả null và ghi log cảnh báo nếu không tìm thấy
    // [Thay đổi] Cài validator: trả ValidatorEntry (hàm + cờ checksum) thay vì chỉ hàm
    private ValidatorEntry? ResolveValidator(string? validatorName, string filePath)
    {
        if (string.IsNullOrWhiteSpace(validatorName))
        {
            return null;
        }

        if (!Validators.ByName.TryGetValue(validatorName, out var validator))
        {
            logger.LogWarning("Validator {validator} không tồn tại, bỏ qua rule trong file {file}", validatorName, filePath);
            return null;
        }

        return validator;
    }
}
