// [Thay đổi] File mới (bước 4.2): cỗ máy quét text bằng regex để tìm dữ liệu nhạy cảm
using System.Text.RegularExpressions;

namespace DlpService.Scanning;

/// <summary>Quét nội dung text theo danh sách rule (SensitivePattern) đã nạp sẵn.</summary>
public class RegexScanner
{
    private readonly List<SensitivePattern> _patterns;

    // [Thay đổi] Merger mục 7.4 "Lưu vết": rules_ver ghi kèm scan_result để biết kết quả được tính bằng bộ rule nào.
    /// <summary>Phiên bản bộ rule đang dùng (RuleLoader.ComputeRulesVersion), rỗng nếu không truyền lúc tạo scanner.</summary>
    public string RulesVer { get; }

    // [Thay đổi] Công thức Go: ngưỡng loại chỗ khớp điểm thấp (Go: EngineConfig.MinConfidence)
    /// <summary>Chỗ khớp có điểm candidate dưới ngưỡng này bị loại, không góp vào mức của file. 0 = không loại gì.</summary>
    public double MinConfidence { get; }

    /// <summary>Tạo scanner với danh sách rule đã sẵn sàng (đã kiểm tra hợp lệ, regex đã biên dịch).</summary>
    /// <param name="patterns">Danh sách rule, thường do RuleLoader nạp từ thư mục rules/.</param>
    /// <param name="rulesVer">[Thay đổi] Mục 7.4: phiên bản bộ rule (RuleLoader.ComputeRulesVersion), mặc định rỗng
    /// cho code cũ/test chưa cần theo dõi.</param>
    /// <param name="minConfidence">[Thay đổi] Công thức Go: ngưỡng điểm tối thiểu (mặc định 0,60 như Go); ngoài 0-1
    /// thì bị chặn về 0-1.</param>
    public RegexScanner(List<SensitivePattern> patterns, string rulesVer = "", double minConfidence = CandidateScorer.DefaultMinConfidence)
    {
        _patterns = patterns;
        RulesVer = rulesVer;
        MinConfidence = Math.Clamp(minConfidence, 0, 1);
    }

    // [Thay đổi] Công thức Go: CVV và ngày hết hạn (MM/YY, MM/YYYY) — bản C# của cvvPattern/expiryPattern trong regex.go.
    // Dùng [0-9] thay \d vì \d của .NET khớp cả chữ số Unicode khác (Go RE2 chỉ khớp 0-9).
    private static readonly Regex CvvRegex = new(@"(?i)(?:cvv|cvc|security\s*code)[^0-9]{0,5}([0-9]{3,4})\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex ExpiryRegex = new(@"\b(?:0[1-9]|1[0-2])[/\-](?:[0-9]{2}|[0-9]{4})\b",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Bán kính (ký tự) quanh số thẻ để tìm CVV + ngày hết hạn (Go: ±300).</summary>
    private const int CvvExpiryWindow = 300;

    // [Thay đổi] Merger mục 7.2: Merger cần biết llm_policy của rule đã tạo ra 1 finding để quyết định có dùng ý kiến
    // LLM hay không. Rule không có RuleId (dạng cũ, không khai "id") thì tra theo Name — khớp cách FindingRecord.RuleId
    // đang được ghi (RuleId ?? PatternName, xem FileScorer/FileWatcherService).
    /// <summary>Chính sách dùng ý kiến LLM của rule theo RuleId (hoặc Name nếu rule không khai "id"); không tìm thấy
    /// → VerifyIfAmbiguous (an toàn nhất, coi như rule vẫn muốn LLM góp ý bình thường).</summary>
    public LlmPolicy PolicyOf(string ruleKey)
    {
        var pattern = _patterns.FirstOrDefault(p => (p.RuleId ?? p.Name) == ruleKey);
        return pattern?.LlmPolicy ?? LlmPolicy.VerifyIfAmbiguous;
    }

    /// <summary>Quét nội dung theo mọi rule đã nạp.</summary>
    /// <param name="content">Nội dung text cần quét.</param>
    /// <returns>Mọi chỗ khớp của mọi rule (đã qua validator, keyword-required, negation_filter, level_gate) + tên các rule
    /// bị quá thời gian.</returns>
    // [Thay đổi] Log timeout: trả ScanResult thay cho List<ScanMatch> để nơi gọi biết lần quét có trọn vẹn hay không
    public ScanResult Scan(string content)
    {
        // [Thay đổi] Log timeout: tên các rule bị RegexMatchTimeoutException (HashSet: 1 rule chỉ ghi 1 lần)
        var timedOut = new HashSet<string>();

        // [Thay đổi] S1 giai đoạn 1: "tài liệu có phải hướng dẫn/mẫu không" chỉ phụ thuộc rule + nội dung → tính 1 lần cho
        // mỗi PlaceholderRule trong lần quét này, không lặp lại cho từng chỗ khớp
        var docDiscountCache = new Dictionary<PlaceholderRule, double>();

        // [Thay đổi] Bước 5.7.4: chia làm 2 vòng. Vòng 1 lọc từng chỗ khớp (như trước).
        // Vòng 2 mới thêm: gộp theo TÊN RULE để đếm số lần khớp, phục vụ level_gate (cần đếm được
        // TOÀN BỘ chỗ khớp của rule đó mới quyết định hạ mức hay không — không xét được ngay trong vòng 1).
        // [Thay đổi] Bậc 1: thêm cờ NegationDowngrade — negation_filter action="downgrade_to_gate" KHÔNG loại
        // chỗ khớp khỏi kết quả (khác "suppress"), chỉ đánh dấu để vòng 2 hạ Severity trực tiếp về FallbackLevel.
        // [Thay đổi] Bậc 3 #1: thêm cờ Escalated — gần chỗ khớp có từ khoá escalation (VD CCCD cạnh "mật khẩu")
        // [Thay đổi] Công thức Go: thêm Score — điểm candidate tính ngay lúc nhận chỗ khớp (để lọc theo MinConfidence)
        var survivors = new List<(SensitivePattern Pattern, Match Match, bool NegationDowngrade, bool Escalated, double Score)>();

        // [Thay đổi] Bậc 3 #2 - "chế độ bảng" cho proximity_window: chỗ khớp trượt proximity KHÔNG bị loại ngay mà
        // để chờ ở đây — sau khi quét hết mới biết rule đó khớp tổng cộng bao nhiêu lần. Khớp ≥ TableModeMinMatches
        // lần (dấu hiệu bảng/danh sách, VD file CSV 200 dòng CCCD chỉ có nhãn "Số CCCD" ở dòng tiêu đề) + có từ khoá
        // ở bất kỳ đâu trong file → vẫn giữ. candidateCounts đếm mọi chỗ khớp đã qua validator, theo tên rule.
        var deferred = new List<(SensitivePattern Pattern, Match Match)>();
        var candidateCounts = new Dictionary<string, int>();

        // [Thay đổi] FileConfidence 2 nhánh: counter near-miss (chỉ đếm, không đổi kết quả). ruleHasKeyword cache theo tên
        // rule: "rule có từ khoá ở đâu đó trong file không" (xem FailsGoKeywordCheck)
        int proximityRejects = 0;
        int gateDowngrades = 0;
        var ruleHasKeyword = new Dictionary<string, bool>();
        // [Thay đổi] Sửa "dãy số lạ" (nhánh A): vị trí các chỗ regex đã khớp nhưng bị validator loại (VD MST sai checksum,
        // SĐT sai đầu số) — rule đã NHẬN RA định dạng nên không tính là "số lạ" (Go không có các validator này, số đó nằm
        // trong chỗ khớp của Go). Chỉ dùng cho counter, không đổi kết quả quét.
        var validatorRejectedSpans = new List<(int Start, int End)>();

        foreach (var pattern in _patterns)
        {
            try
            {
                // Matches() trả về tất cả chỗ khớp của pattern này trong content
                foreach (Match match in pattern.Regex.Matches(content))
                {
                    // [Thay đổi] Nếu rule có validator mà chỗ khớp không qua được (ví dụ sai Luhn) thì bỏ qua
                    if (pattern.Validator != null && !pattern.Validator(match.Value))
                    {
                        validatorRejectedSpans.Add((match.Index, match.Index + match.Length)); // [Thay đổi] Sửa "dãy số lạ"
                        continue;
                    }

                    // [Thay đổi] Bậc 3 #5: placeholder_exclusion — giá trị mẫu/giả (changeme, 123456, số thẻ test...)
                    // bị LOẠI HẲN khỏi kết quả, đặt TRƯỚC bước đếm để không góp vào chế độ bảng (#2) lẫn volume (#4).
                    if (pattern.Placeholder != null && IsPlaceholder(pattern.Placeholder, match.Value))
                    {
                        continue;
                    }

                    // [Thay đổi] Chẩn đoán nhánh B: đếm proximity_reject ĐÚNG định nghĩa Go (regex.go bước 4) — chỉ rule khai
                    // exclude_if_no_keywords, xét mọi pattern con, ngay sau validator/placeholder. Chỉ ĐẾM, không loại chỗ
                    // khớp (việc lọc vẫn theo ContextRequired/Proximity bên dưới). Trước đây đếm mọi chỗ khớp context_required
                    // bị loại → C# kêu ở 53 % file nhánh B so với 4,6 % của Go, kéo conf nhánh B xuống sai.
                    if (pattern.ExcludeIfNoKeywords && FailsGoKeywordCheck(pattern, match, content, ruleHasKeyword))
                    {
                        proximityRejects++;
                    }

                    // [Thay đổi] Bậc 3 #2: đếm chỗ khớp theo rule (phục vụ chế độ bảng bên dưới)
                    candidateCounts[pattern.Name] = candidateCounts.GetValueOrDefault(pattern.Name) + 1;

                    // [Thay đổi] Bậc 3 #2: có proximity → kiểm tra CHẶT (gần + cùng câu + đúng nhóm từ khoá) thay cho
                    // kiểm tra ContextWindow rộng. Trượt thì chờ xét chế độ bảng (nếu được phép), không thì loại luôn.
                    if (pattern.ContextRequired && pattern.Proximity != null)
                    {
                        if (!HasKeywordInProximity(pattern, match, content))
                        {
                            if (pattern.Proximity.AllowTableFallback)
                            {
                                deferred.Add((pattern, match));
                            }
                            continue;
                        }
                    }
                    // [Thay đổi] Bước 5.6: keyword-required matching — chỉ giữ chỗ khớp nếu gần đó có từ khoá xác nhận
                    // [Thay đổi] Bậc 3 #2: đổi "if" thành "else if" — chỉ còn áp cho pattern KHÔNG khai proximity
                    else if (pattern.ContextRequired)
                    {
                        // Math.Max/Math.Min để cắt đoạn xung quanh mà không vượt ra ngoài chuỗi content
                        int start = Math.Max(0, match.Index - pattern.ContextWindow);
                        int end = Math.Min(content.Length, match.Index + match.Length + pattern.ContextWindow);
                        string surrounding = content.Substring(start, end - start);

                        // Contains(k, OrdinalIgnoreCase) = so khớp từ khoá không phân biệt hoa thường
                        bool hasKeyword = pattern.Keywords.Any(k => surrounding.Contains(k, StringComparison.OrdinalIgnoreCase));
                        if (!hasKeyword)
                        {
                            continue;
                        }
                    }

                    // [Thay đổi] Bậc 3 #2: phần negation_filter + escalation tách ra hàm AddSurvivor, để các chỗ khớp
                    // được "cứu" nhờ chế độ bảng (bên dưới) cũng đi qua ĐÚNG các bước lọc đó, không viết lặp 2 lần.
                    AddSurvivor(pattern, match, content, docDiscountCache, survivors); // [Thay đổi] Công thức Go: + cache hệ số tài liệu
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Pattern này quét quá lâu: bỏ qua, chuyển sang pattern kế tiếp
                // [Thay đổi] Log timeout: không nuốt im lặng nữa — ghi lại tên rule để nơi gọi cảnh báo và KHÔNG kết luận
                // mức. Các chỗ khớp của rule này tìm được trước lúc timeout vẫn nằm trong survivors.
                timedOut.Add(pattern.Name);
            }
        }

        // [Thay đổi] Bậc 3 #2: xét chế độ bảng cho các chỗ khớp đã trượt proximity (xem giải thích ở khai báo deferred)
        foreach (var (pattern, match) in deferred)
        {
            bool looksLikeTable = candidateCounts.GetValueOrDefault(pattern.Name) >= TableModeMinMatches;
            if (looksLikeTable && ProximityKeywords(pattern).Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                AddSurvivor(pattern, match, content, docDiscountCache, survivors); // [Thay đổi] Công thức Go: + cache hệ số tài liệu
            }
        }

        // [Thay đổi] Bậc 3 #4: volume_escalation — với mỗi rule có khai, đếm số giá trị KHÁC NHAU (đã chuẩn hoá) trong
        // file rồi tra bậc ngưỡng. Chạy TRƯỚC vòng chuẩn bị strongRuleNames để mức đã nâng được tính luôn là "rule
        // mạnh". Chỗ khớp bị negation hạ mức (VD số TK trong "ví dụ") KHÔNG được đếm — negation vẫn thắng như #1/#3.
        var volumeLevels = new Dictionary<string, string>();
        foreach (var group in survivors.Where(s => !s.NegationDowngrade).GroupBy(s => s.Pattern.Name))
        {
            var volumeEscalation = group.First().Pattern.VolumeEscalation;
            if (volumeEscalation == null)
            {
                continue;
            }

            int distinctCount = group.Select(s => NormalizeValue(s.Match.Value)).Distinct().Count();
            string? volumeLevel = volumeEscalation.LevelFor(distinctCount);
            if (volumeLevel != null)
            {
                volumeLevels[group.Key] = volumeLevel;
            }
        }

        // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: vòng chuẩn bị MỚI, chạy TRƯỚC vòng gộp
        // theo rule — cần thông tin TOÀN CỤC (xuyên suốt mọi rule trong content này), không thể tính trong vòng
        // gộp per-rule như "≥2 lần khớp" (groupList.Count) hay "đã qua validator" (groupList.Any(...)):
        //   - strongRuleNames: tên các rule có ÍT NHẤT 1 chỗ khớp đạt mức CONFIDENTIAL+ (SAU khi đã áp override_level
        //     và negation_filter, TRƯỚC khi áp level_gate — đúng thứ tự "level_gate coi các rule KHÁC đã ổn định
        //     mức của chúng trước khi xét corroboration cho rule này"). Dùng cho điều kiện "có rule KHÁC cùng nội
        //     dung đạt ≥CONFIDENTIAL" (VD: 1 email vô danh + 1 số CCCD cùng chunk → CCCD đã CONFIDENTIAL nên email
        //     không cần tự thân đạt ≥2 lần).
        //   - tagCounts: đếm mọi Tags (không lọc allowlist) trên TOÀN BỘ chỗ khớp — level_gate.required_corroboration_tags
        //     tham chiếu tag do CHÍNH RULE KHÁC khai (VD health_001 cần tag "personal_identifier" do vn_name_001/
        //     vn_id_001/dob_001 khai), không giới hạn trong rule đang xét.
        var strongRuleNames = new HashSet<string>();
        var tagCounts = new Dictionary<string, int>();
        foreach (var (pattern, match, negationDowngrade, escalated, _) in survivors) // [Thay đổi] Công thức Go: + Score (không dùng ở đây)
        {
            // [Thay đổi] Bậc 3 #1: dùng mức ĐÃ NÂNG — CCCD cạnh "mật khẩu" lên SECRET sẽ được tính là rule mạnh,
            // giúp các rule khác cùng nội dung qua được level_gate.
            // [Thay đổi] Bậc 3 #4: truyền thêm mức volume của rule (null nếu chưa đạt ngưỡng)
            string baseSeverity = ResolveBaseSeverity(pattern, escalated, volumeLevels.GetValueOrDefault(pattern.Name));
            string resolvedSeverity = negationDowngrade ? (pattern.LevelGate?.FallbackLevel ?? baseSeverity) : baseSeverity;
            if (SeverityRank(resolvedSeverity) >= SeverityRank("CONFIDENTIAL"))
            {
                strongRuleNames.Add(pattern.Name);
            }

            foreach (var tag in pattern.Tags)
            {
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
            }
        }

        // [Thay đổi] Bước 5.7.4: level_gate — gộp theo tên rule (nhiều pattern con của cùng 1 rule dùng chung tên)
        // GroupBy(s => s.Pattern.Name): "nhóm các chỗ khớp có cùng Pattern.Name lại với nhau"
        // [Thay đổi] Bậc 1: mức độ nghiêm trọng giờ tính RIÊNG cho từng chỗ khớp (override_level của pattern con,
        // có bị negation hạ hay không, gate có áp cho đúng pattern con đó hay không) — trước đây cả nhóm cùng tên
        // rule luôn nhận chung 1 Severity, bất kể chỗ khớp đó đến từ pattern con nào.
        var results = new List<ScanMatch>();
        foreach (var group in survivors.GroupBy(s => s.Pattern.Name))
        {
            var groupList = group.ToList();

            // Tất cả pattern con của cùng 1 rule mang chung LevelGate (RuleLoader đã gán vậy), lấy đại diện từ phần tử đầu
            var levelGate = groupList[0].Pattern.LevelGate;

            // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: 2 trong 3 điều kiện củng cố mặc định
            // (≥2 lần khớp, đã qua validator) chỉ cần thông tin CỦA CHÍNH rule này nên tính ngay tại đây — không
            // cần đưa vào vòng chuẩn bị toàn cục ở trên.
            bool hasEnoughMatches = groupList.Count >= 2;
            // [Thay đổi] Cài validator: chỉ validator CHECKSUM mới là bằng chứng củng cố; bộ lọc email/SĐT/entropy thì không
            bool hasValidatedMatch = groupList.Any(g => g.Pattern.ValidatorIsChecksum);
            bool hasOtherStrongRule = strongRuleNames.Any(name => name != groupList[0].Pattern.Name);

            foreach (var (pattern, match, negationDowngrade, escalated, score) in groupList) // [Thay đổi] Công thức Go: + score
            {
                // Mức gốc: ưu tiên override_level riêng của pattern con, không có thì dùng Severity chung của rule
                // [Thay đổi] Bậc 3 #1: gọi hàm dùng chung với vòng chuẩn bị ở trên (đã tính cả escalation)
                // [Thay đổi] Bậc 3 #4: + mức volume. Chỗ khớp bị negation hạ vẫn rơi vào nhánh fallback ngay dưới,
                // nên không bao giờ được volume nâng.
                string baseSeverity = ResolveBaseSeverity(pattern, escalated, volumeLevels.GetValueOrDefault(pattern.Name));

                // [Thay đổi] Bậc 3 #7: tính điểm tin cậy MỘT lần cho mỗi chỗ khớp — dùng cho bypass bên dưới và gán
                // vào ScanMatch.Confidence (trước đây chỉ tính trong nhánh gate rồi bỏ đi).
                // [Thay đổi] S1 giai đoạn 1: tách phần boost từ khoá ra để dùng chung cho Confidence (giữ nguyên công thức,
                // bypass không đổi) và điểm candidate Score (thêm thưởng checksum, phạt negation/ngữ cảnh tài liệu).
                // [Thay đổi] Công thức Go: score đã tính sẵn trong AddSurvivor. Confidence (dùng cho min_confidence_bypass)
                // giờ CHÍNH LÀ score — giống Go (bypass so m.Confidence = điểm cuối), trước đây là công thức rút gọn
                // confidence × weight + boost. Pattern không khai confidence thì vẫn null (không được bypass) như cũ.
                double? confidence = pattern.Confidence != null ? score : null;

                string severity;
                if (negationDowngrade)
                {
                    // negation_filter đã phát hiện từ phủ định gần đó → hạ NGAY về fallback, không chờ xét corroboration
                    // (loadRule đã đảm bảo levelGate khác null khi Action=downgrade_to_gate được phép tồn tại)
                    severity = levelGate?.FallbackLevel ?? baseSeverity;
                }
                else if (levelGate != null && levelGate.RequireCorroboration && levelGate.AppliesToPattern(pattern.PatternIdx))
                {
                    // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: đủ củng cố nếu MỘT trong các
                    // điều kiện sau đúng — trước đây (Bậc 1) chỉ có duy nhất "khớp ≥2 lần".
                    bool hasRequiredTag = levelGate.RequiredCorroborationTags is { Count: > 0 }
                        && levelGate.RequiredCorroborationTags.Any(t => tagCounts.GetValueOrDefault(t) >= 1);

                    bool corroborated;
                    if (levelGate.StrictTagsOnly)
                    {
                        // strict_tags_only: LOẠI BỎ 2 điều kiện mặc định — corroborated CHỈ còn dựa vào tag cụ thể
                        // (loadRule đã đảm bảo RequiredCorroborationTags không rỗng khi StrictTagsOnly=true).
                        corroborated = hasRequiredTag;
                    }
                    else
                    {
                        // [Thay đổi] Bậc 3 #1: từ khoá escalation gần chỗ khớp cũng là 1 bằng chứng củng cố — chỗ
                        // khớp đã nâng mức vẫn đi qua gate (không miễn gate), nhưng sẽ qua được nhờ điều kiện này.
                        // Không thêm vào nhánh StrictTagsOnly phía trên vì strict nghĩa là CHỈ dựa vào tag.
                        // [Thay đổi] Bậc 3 #3: min_confidence_bypass — độ tin cậy động của CHÍNH chỗ khớp này đạt ngưỡng
                        // thì cũng tính là đủ củng cố. Chỉ tính khi rule khai ngưỡng VÀ pattern con có confidence.
                        // [Thay đổi] Bậc 3 #7: dùng lại biến confidence đã tính ở trên
                        bool bypassed = levelGate.MinConfidenceBypass != null
                            && confidence is double conf
                            && conf >= levelGate.MinConfidenceBypass.Value;

                        corroborated = hasValidatedMatch || hasEnoughMatches || hasOtherStrongRule || hasRequiredTag || escalated || bypassed;
                    }

                    severity = corroborated ? baseSeverity : levelGate.FallbackLevel;

                    // [Thay đổi] FileConfidence: đếm như Go — chỉ khi gate THỰC SỰ hạ mức (mức gốc cao hơn fallback)
                    if (!corroborated && SeverityRank(baseSeverity) > SeverityRank(levelGate.FallbackLevel))
                    {
                        gateDowngrades++;
                    }
                }
                else
                {
                    severity = baseSeverity;
                }

                results.Add(new ScanMatch
                {
                    PatternName = pattern.Name,
                    Value = match.Value,
                    Index = match.Index,
                    Severity = severity,
                    // [Thay đổi checklist Bậc 2 - "Compound rules"]: mang theo category/id/tags + cờ đã qua validator
                    // để CompoundRuleModule (Classification layer) dựng tập tag mà không cần tra ngược rule gốc.
                    Validated = pattern.ValidatorIsChecksum, // [Thay đổi] Cài validator: chỉ tính checksum
                    Category = pattern.Category,
                    RuleId = pattern.RuleId,
                    Tags = pattern.Tags,
                    Confidence = confidence, // [Thay đổi] Bậc 3 #7
                    Score = score // [Thay đổi] S1 giai đoạn 1
                });
            }
        }

        // [Thay đổi] Log timeout: trả kèm danh sách rule bị timeout
        // [Thay đổi] FileConfidence 2 nhánh: trả kèm counter near-miss
        return new ScanResult
        {
            Matches = results,
            TimedOutPatterns = timedOut.ToList(),
            Telemetry = BuildTelemetry(content, results, proximityRejects, gateDowngrades, validatorRejectedSpans)
        };
    }

    // ===================== [Thay đổi] FileConfidence 2 nhánh: counter near-miss (port từ telemetry.go của Go) =====================

    // Từ khoá "tài liệu ví dụ / mẫu" — danh sách CỐ ĐỊNH của Go (docContextProbeKeywords), khác doc_context_discount_keywords
    // riêng của từng rule. Hệ số nhánh B được fit theo đúng danh sách này nên giữ nguyên, không lấy từ rule.
    private static readonly string[] DocContextProbeKeywords =
    [
        "ví dụ", "vi du", "template", "sop", "biểu mẫu", "bieu mau",
        "minh họa", "minh hoa", "mẫu điền", "mau dien"
    ];

    // Dãy 8–19 chữ số ASCII (Go: \d{8,19} — RE2 chỉ khớp 0-9)
    private static readonly Regex UnstructuredNumericRegex = new(@"[0-9]{8,19}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Rule có từ khoá (primary hoặc secondary) ở bất kỳ đâu trong file không — tính 1 lần cho mỗi tên rule trong lần quét
    private static bool RuleHasKeyword(SensitivePattern pattern, string content, Dictionary<string, bool> cache)
    {
        if (!cache.TryGetValue(pattern.Name, out bool has))
        {
            has = pattern.Keywords.Any(k => k.Length > 0 && content.Contains(k, StringComparison.OrdinalIgnoreCase));
            cache[pattern.Name] = has;
        }
        return has;
    }

    // [Thay đổi] Chẩn đoán nhánh B: kiểm tra từ khoá kiểu Go cho rule exclude_if_no_keywords — true = Go sẽ loại chỗ khớp
    // này VÀ đếm proximity_reject. Hai trường hợp Go loại nhưng KHÔNG đếm thì trả false:
    //  - pattern context_required mà cả file không có từ khoá nào của rule (Go bỏ qua cả pattern từ trước);
    //  - pattern con có require_keyword_within và trượt (Go loại ở bước trước, im lặng).
    private static bool FailsGoKeywordCheck(SensitivePattern pattern, Match match, string content, Dictionary<string, bool> ruleHasKeyword)
    {
        bool hasAnyKeyword = RuleHasKeyword(pattern, content, ruleHasKeyword);
        if (pattern.ContextRequired && !hasAnyKeyword)
        {
            return false;
        }

        // Proximity của riêng pattern con (require_keyword_within) — khác đối tượng proximity cấp rule
        if (pattern.ContextRequired && pattern.Proximity != null && !ReferenceEquals(pattern.Proximity, pattern.RuleProximity)
            && !HasKeywordInProximity(pattern, pattern.Proximity, match, content))
        {
            return false;
        }

        // Rule không khai proximity_window: Go chỉ đòi "có từ khoá ở đâu đó trong file"
        if (pattern.RuleProximity == null)
        {
            return !hasAnyKeyword;
        }

        // Rule có proximity_window: vế (a) từ khoá trong min_context_window VÀ vế (b) proximity (gần + cùng câu)
        int start = Math.Max(0, match.Index - pattern.ContextWindow);
        int end = Math.Min(content.Length, match.Index + match.Length + pattern.ContextWindow);
        string surrounding = content.Substring(start, end - start);
        bool inMinWindow = pattern.Keywords.Any(k => k.Length > 0 && surrounding.Contains(k, StringComparison.OrdinalIgnoreCase));
        return !inMinWindow || !HasKeywordInProximity(pattern, pattern.RuleProximity, match, content);
    }

    // Gom các counter cần TOÀN BỘ kết quả quét (chỗ khớp cuối cùng) — tính sau vòng gộp theo rule
    // [Thay đổi] Sửa "dãy số lạ": + validatorRejectedSpans. [Thay đổi] Đoán domain: + danh sách rule có từ khoá primary.
    private ScanTelemetry BuildTelemetry(string content, List<ScanMatch> results, int proximityRejects, int gateDowngrades,
        List<(int Start, int End)> validatorRejectedSpans)
    {
        // Số chỗ khớp cuối cùng theo khoá rule (RuleId, không có thì tên) — dùng cho keyword_hit_no_match + volume near-miss
        var matchCountByRule = results
            .GroupBy(m => m.RuleId ?? m.PatternName)
            .ToDictionary(g => g.Key, g => g.Count());

        int keywordHitNoMatch = 0;
        double volumeNearGate = 0;
        var primaryKeywordRules = new List<KeywordRuleHit>(); // [Thay đổi] Đoán domain
        // Nhiều pattern con của cùng 1 rule dùng chung từ khoá/volume → chỉ xét 1 lần cho mỗi rule
        foreach (var rule in _patterns.GroupBy(p => p.RuleId ?? p.Name))
        {
            var first = rule.First();
            int count = matchCountByRule.GetValueOrDefault(rule.Key);

            // [Thay đổi] Đoán domain: xét từ khoá primary cho MỌI rule (không chỉ rule không có chỗ khớp)
            bool hasPrimary = first.PrimaryKeywords.Any(k => k.Length > 0 && content.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (hasPrimary)
            {
                primaryKeywordRules.Add(new KeywordRuleHit(rule.Key, first.Category));
            }

            // Go: rule có ≥1 từ khoá PRIMARY trong file nhưng 0 chỗ khớp
            if (count == 0 && hasPrimary)
            {
                keywordHitNoMatch++;
            }

            // Go (ApplyVolumeEscalation): có chỗ khớp nhưng chưa tới bậc thấp nhất → cộng count / min_count_thấp_nhất.
            // Go đếm SỐ CHỖ KHỚP (không phải số giá trị khác nhau như volume escalation của C#) — giữ đúng định nghĩa đã fit.
            if (count > 0 && first.VolumeEscalation is { Thresholds.Count: > 0 } volume)
            {
                int lowest = volume.Thresholds.Min(t => t.MinCount);
                if (lowest > 0 && count < lowest)
                {
                    volumeNearGate += Math.Min(1.0, (double)count / lowest);
                }
            }
        }

        return new ScanTelemetry
        {
            ProximityRejectCount = proximityRejects,
            LevelGateDowngradeCount = gateDowngrades,
            KeywordHitNoMatchPrimaryRules = keywordHitNoMatch,
            UnstructuredNumericHits = CountUnstructuredNumericHits(content, results, validatorRejectedSpans),
            DocContextDiscount = DocContextProbeKeywords.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase)),
            IdentifierVolumeNearGate = volumeNearGate,
            EffectiveLengthBytes = System.Text.Encoding.UTF8.GetByteCount(content),
            PrimaryKeywordRules = primaryKeywordRules // [Thay đổi] Đoán domain
        };
    }

    // Go countUnstructuredNumericHits: đếm dãy 8–19 chữ số KHÔNG nằm trong chỗ khớp nào; bỏ dãy là mảnh của số dài hơn
    // (ký tự sát 2 đầu vẫn là chữ số). Regex quá thời gian → trả số đếm được tới lúc đó.
    // [Thay đổi] Sửa "dãy số lạ": cũng bỏ dãy chồng lên chỗ regex đã khớp nhưng bị validator loại (xem validatorRejectedSpans)
    private static int CountUnstructuredNumericHits(string content, List<ScanMatch> results, List<(int Start, int End)> validatorRejectedSpans)
    {
        int count = 0;
        try
        {
            foreach (Match m in UnstructuredNumericRegex.Matches(content))
            {
                int s = m.Index, e = m.Index + m.Length;
                if ((s > 0 && char.IsAsciiDigit(content[s - 1])) || (e < content.Length && char.IsAsciiDigit(content[e])))
                {
                    continue;
                }
                bool inMatch = results.Any(r => s < r.Index + r.Value.Length && e > r.Index)
                    || validatorRejectedSpans.Any(v => s < v.End && e > v.Start);
                if (!inMatch)
                {
                    count++;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Giữ số đếm được — đây chỉ là tín hiệu quan sát, không đáng làm hỏng cả lần quét
        }
        return count;
    }

    // [Thay đổi] Bậc 3 #2: tách từ vòng 1 của Scan() — áp negation_filter + escalation cho 1 chỗ khớp đã qua các
    // bước lọc từ khoá, rồi thêm vào survivors. Nội dung GIỮ NGUYÊN như trước khi tách (chỉ đổi "continue" → "return").
    // [Thay đổi] Công thức Go: bỏ "static" (cần đọc MinConfidence), nhận thêm cache hệ số tài liệu. Tính điểm candidate
    // NGAY ĐẦU hàm và loại chỗ khớp dưới ngưỡng TRƯỚC negation/escalation — đúng thứ tự Go (bước 6 "ngưỡng confidence"
    // đứng trước bước 8 escalation và 8.5 negation). Chỗ khớp bị loại không góp vào volume, level_gate, compound, S1.
    private void AddSurvivor(
        SensitivePattern pattern,
        Match match,
        string content,
        Dictionary<PlaceholderRule, double> docDiscountCache,
        List<(SensitivePattern Pattern, Match Match, bool NegationDowngrade, bool Escalated, double Score)> survivors)
    {
        double score = CandidateScorer.Score(pattern.Confidence, pattern.Weight, KeywordBoost(pattern, match, content),
            pattern.ValidatorIsChecksum, CvvExpiryBoostFor(pattern, match, content),
            DocDiscountFactor(pattern, content, docDiscountCache));
        if (score < MinConfidence)
        {
            return;
        }

        // [Thay đổi] Bước 5.7.3: negation_filter — xử lý nếu gần đó có từ phủ định
        // (ví dụ "Sao kê tài khoản định kỳ 5.500 VNĐ/lần" bị loại vì có "/lần" gần đó — đó là biểu phí, không phải sao kê thật)
        // [Thay đổi] Bậc 1: chỉ áp dụng nếu filter khai applies_to_patterns bao gồm đúng pattern con này
        // (rỗng = áp cho mọi pattern, như cũ); action="downgrade_to_gate" giữ lại chỗ khớp (đánh dấu cờ)
        // thay vì loại hẳn như "suppress" — trước đây engine luôn xử lý như suppress bất kể action khai gì.
        bool negationDowngrade = false;
        if (pattern.NegationFilter != null && pattern.NegationFilter.AppliesTo(pattern.PatternIdx))
        {
            int negStart = Math.Max(0, match.Index - pattern.NegationFilter.WindowChars);
            int negEnd = Math.Min(content.Length, match.Index + match.Length + pattern.NegationFilter.WindowChars);
            string negSurrounding = content.Substring(negStart, negEnd - negStart);

            bool hasTriggerWord = pattern.NegationFilter.TriggerWords.Any(w => negSurrounding.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (hasTriggerWord)
            {
                if (pattern.NegationFilter.Action == "suppress")
                {
                    return;
                }

                negationDowngrade = true;
            }
        }

        // [Thay đổi] Bậc 3 #1: escalation — tìm từ khoá nguy hiểm trong CÙNG vùng ContextWindow của rule
        // (không có vùng riêng). Chỉ ĐÁNH DẤU cờ ở đây; nâng mức thật sự làm trong ResolveBaseSeverity.
        bool escalated = false;
        if (pattern.Escalation != null)
        {
            int escStart = Math.Max(0, match.Index - pattern.ContextWindow);
            int escEnd = Math.Min(content.Length, match.Index + match.Length + pattern.ContextWindow);
            string escSurrounding = content.Substring(escStart, escEnd - escStart);

            escalated = pattern.Escalation.Keywords.Any(k => escSurrounding.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        survivors.Add((pattern, match, negationDowngrade, escalated, score));
    }

    // [Thay đổi] Bậc 3 #5: 1 chỗ khớp có phải giá trị mẫu/giả không.
    //  - known_test_values: so chính xác sau khi chỉ giữ chữ số ("4111 1111 1111 1111" = "4111111111111111").
    //  - value_blocklist_patterns: regex rule khớp CẢ nhãn lẫn giá trị ("password=changeme123", "CVV: 123") nhưng
    //    blocklist viết cho PHẦN GIÁ TRỊ (có neo ^...$), nên thử trên nhiều "ứng viên giá trị" (ValueCandidates),
    //    chỉ cần 1 ứng viên khớp là coi như placeholder.
    private static bool IsPlaceholder(PlaceholderRule placeholder, string matchValue)
    {
        if (placeholder.KnownTestValues.Count > 0)
        {
            string digits = new string(matchValue.Where(char.IsDigit).ToArray());
            if (placeholder.KnownTestValues.Contains(digits))
            {
                return true;
            }
        }

        foreach (var candidate in ValueCandidates(matchValue))
        {
            foreach (var blockRegex in placeholder.Blocklist)
            {
                try
                {
                    if (blockRegex.IsMatch(candidate))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Regex blocklist quá chậm: coi như không khớp (không loại) — thà giữ lại còn hơn bỏ sót dữ liệu thật
                }
            }
        }

        return false;
    }

    // [Thay đổi] Bậc 3 #5: các cách "cắt ra phần giá trị" từ 1 chỗ khớp, VD:
    //   "password=changeme123"      → sau '='/':' cuối     → "changeme123"
    //   "\"password\": \"changeme\"" → sau ':' cuối + bỏ nháy → "changeme"
    //   "mã OTP của bạn là 123456"  → từ cuối cùng         → "123456"
    //   "1\nNguyễn Văn A"            → sau xuống dòng cuối → "Nguyễn Văn A"
    // Mỗi ứng viên bỏ khoảng trắng, dấu nháy, dấu câu thừa ở cuối (. , ; )). Ứng viên rỗng thì bỏ qua.
    private static IEnumerable<string> ValueCandidates(string value)
    {
        var candidates = new List<string> { value };

        int labelEnd = value.LastIndexOfAny(new[] { ':', '=' });
        if (labelEnd >= 0)
        {
            candidates.Add(value[(labelEnd + 1)..]);
        }

        int lineEnd = value.LastIndexOf('\n');
        if (lineEnd >= 0)
        {
            candidates.Add(value[(lineEnd + 1)..]);
        }

        string trimmed = value.TrimEnd();
        int lastSpace = -1;
        for (int i = trimmed.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(trimmed[i]))
            {
                lastSpace = i;
                break;
            }
        }
        if (lastSpace >= 0)
        {
            candidates.Add(trimmed[(lastSpace + 1)..]);
        }

        return candidates
            .Select(c => c.Trim().Trim('"', '\'').TrimEnd('.', ',', ';', ')').Trim())
            .Where(c => c.Length > 0)
            .Distinct();
    }

    // [Thay đổi] Bậc 3 #2: số lần khớp tối thiểu của 1 rule trong 1 file để coi là "bảng/danh sách" (chế độ bảng)
    private const int TableModeMinMatches = 5;

    // [Thay đổi] Bậc 3 #2: nhóm từ khoá mà proximity được phép xét, theo keyword_scope
    // [Thay đổi] Chẩn đoán nhánh B: tách bản nhận proximity làm tham số (FailsGoKeywordCheck cần xét proximity cấp RULE)
    private static IEnumerable<string> ProximityKeywords(SensitivePattern pattern) => ProximityKeywords(pattern, pattern.Proximity!);

    private static IEnumerable<string> ProximityKeywords(SensitivePattern pattern, ProximityRule proximity) =>
        proximity.IncludeSecondary
            ? pattern.PrimaryKeywords.Concat(pattern.SecondaryKeywords)
            : pattern.PrimaryKeywords;

    // [Thay đổi] Bậc 3 #2: từ khoá có nằm trong MaxChars ký tự quanh chỗ khớp không — nếu SameSentence thì vùng tìm
    // còn bị cắt thêm ở ranh giới câu (lấy phần GIAO giữa "vùng MaxChars" và "câu chứa chỗ khớp").
    private static bool HasKeywordInProximity(SensitivePattern pattern, Match match, string content) =>
        HasKeywordInProximity(pattern, pattern.Proximity!, match, content);

    // [Thay đổi] Chẩn đoán nhánh B: bản nhận proximity làm tham số — thân hàm giữ nguyên
    private static bool HasKeywordInProximity(SensitivePattern pattern, ProximityRule proximity, Match match, string content)
    {
        int start = Math.Max(0, match.Index - proximity.MaxChars);
        int end = Math.Min(content.Length, match.Index + match.Length + proximity.MaxChars);

        if (proximity.SameSentence)
        {
            start = Math.Max(start, FindSentenceStart(content, match.Index));
            end = Math.Min(end, FindSentenceEnd(content, match.Index + match.Length));
        }

        string surrounding = content.Substring(start, end - start);
        return ProximityKeywords(pattern, proximity).Any(k => surrounding.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    // [Thay đổi] Bậc 3 #2: ranh giới câu = xuống dòng, HOẶC 1 trong ". ! ? ;" mà THEO SAU là khoảng trắng (hoặc hết
    // văn bản). Điều kiện "theo sau là khoảng trắng" để dấu chấm NẰM TRONG số (10.20.30.40, 5.500, 12.05.1990) không
    // bị hiểu nhầm là hết câu.
    private static bool IsSentenceEnd(string content, int i) =>
        content[i] == '\n'
        || (content[i] is '.' or '!' or '?' or ';' && (i + 1 >= content.Length || char.IsWhiteSpace(content[i + 1])));

    // Lùi từ vị trí chỗ khớp về đầu văn bản, gặp ranh giới câu đầu tiên thì câu bắt đầu ngay sau nó
    private static int FindSentenceStart(string content, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (IsSentenceEnd(content, i))
            {
                return i + 1;
            }
        }
        return 0;
    }

    // Tiến từ cuối chỗ khớp về cuối văn bản, gặp ranh giới câu đầu tiên thì câu kết thúc tại đó
    private static int FindSentenceEnd(string content, int index)
    {
        for (int i = index; i < content.Length; i++)
        {
            if (IsSentenceEnd(content, i))
            {
                return i;
            }
        }
        return content.Length;
    }

    // [Thay đổi] Bậc 3 #1: tính mức gốc của 1 chỗ khớp — override_level của pattern con (nếu có), không thì Severity
    // chung của rule; nếu có escalation thì lấy mức CAO HƠN giữa mức đó và EscalateTo (chỉ nâng, không bao giờ hạ).
    // Tách thành hàm vì cả vòng chuẩn bị strongRuleNames lẫn vòng gộp đều cần, tránh 2 chỗ tính lệch nhau.
    // [Thay đổi] Bậc 3 #4: thêm tham số volumeLevel — mức gốc giờ là mức CAO NHẤT trong 3 nguồn: override_level/
    // Severity, escalation (nếu có từ khoá gần đó), volume (nếu rule đạt ngưỡng số lượng trong file).
    private static string ResolveBaseSeverity(SensitivePattern pattern, bool escalated, string? volumeLevel)
    {
        string baseSeverity = pattern.OverrideLevel ?? pattern.Severity;

        if (escalated && pattern.Escalation != null && SeverityRank(pattern.Escalation.EscalateTo) > SeverityRank(baseSeverity))
        {
            baseSeverity = pattern.Escalation.EscalateTo;
        }

        if (volumeLevel != null && SeverityRank(volumeLevel) > SeverityRank(baseSeverity))
        {
            baseSeverity = volumeLevel;
        }

        return baseSeverity;
    }

    // [Thay đổi] Bậc 3 #4: chuẩn hoá giá trị trước khi đếm "giá trị khác nhau" — bỏ khoảng trắng, '-', '.' và viết
    // hoa, để "0123 4567 8901" và "012345678901" (hay "B1234567" và "b1234567") chỉ tính là 1 bản ghi.
    private static string NormalizeValue(string value) =>
        new string(value.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '.').ToArray()).ToUpperInvariant();

    // [Thay đổi] Công thức Go: bỏ ComputeConfidence + hằng số boost cố định (PrimaryBoost 0.15 / SecondaryBoost 0.05 /
    // "mỗi từ khoá 1 lần"). Thay bằng boost theo khoảng cách như distanceWeightedBoost của Go: MỖI LẦN từ khoá xuất hiện
    // trong vòng BoostWindow (200) ký tự quanh chỗ khớp đều được cộng theo vùng (xem CandidateScorer.ZoneBoost), tổng
    // tối đa 0.30. Vùng tìm cố định 200 như Go (ContextWindow của engine), KHÔNG dùng ContextWindow riêng của rule
    // (cái đó vẫn chỉ phục vụ context_required/escalation như cũ).
    private static double KeywordBoost(SensitivePattern pattern, Match match, string content)
    {
        int matchStart = match.Index;
        int matchEnd = match.Index + match.Length;

        double total = SumKeywordBoost(pattern.PrimaryKeywords, primary: true, matchStart, matchEnd, content);
        if (total < CandidateScorer.MaxKeywordBoost)
        {
            total += SumKeywordBoost(pattern.SecondaryKeywords, primary: false, matchStart, matchEnd, content);
        }
        return Math.Min(total, CandidateScorer.MaxKeywordBoost);
    }

    // [Thay đổi] Công thức Go: cộng boost của mọi lần xuất hiện của 1 nhóm từ khoá. Vị trí BẮT ĐẦU của từ khoá phải nằm
    // trong [matchStart − 200, matchEnd + 199] (tức khoảng cách ≤ 200 theo CandidateScorer.Distance). Dừng sớm khi đã
    // chạm trần — tổng cuối vẫn bị cắt ở 0.30 nên thứ tự cộng không làm đổi kết quả.
    private static double SumKeywordBoost(List<string> keywords, bool primary, int matchStart, int matchEnd, string content)
    {
        double total = 0;
        int firstStart = Math.Max(0, matchStart - CandidateScorer.BoostWindow);
        int lastStart = matchEnd + CandidateScorer.BoostWindow - 1;

        foreach (var keyword in keywords)
        {
            if (keyword.Length == 0)
            {
                continue;
            }

            // Vùng tìm kết thúc ở (vị trí bắt đầu muộn nhất + độ dài từ khoá), không vượt quá cuối nội dung
            int searchEnd = Math.Min(content.Length, lastStart + keyword.Length);
            int from = firstStart;
            while (searchEnd - from >= keyword.Length)
            {
                int idx = content.IndexOf(keyword, from, searchEnd - from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    break;
                }

                total += CandidateScorer.ZoneBoost(CandidateScorer.Distance(idx, matchStart, matchEnd), primary);
                if (total >= CandidateScorer.MaxKeywordBoost)
                {
                    return total;
                }
                from = idx + 1; // +1 (không phải + độ dài) để không bỏ sót lần xuất hiện chồng nhau, giống Aho-Corasick của Go
            }
        }
        return total;
    }

    // [Thay đổi] Công thức Go: bước 5 "CVV + Expiry context boost" — rule khai cvv_expiry_boost (thẻ tín dụng) và trong
    // ±300 ký tự quanh chỗ khớp có CẢ CVV lẫn ngày hết hạn thì được cộng. Regex quá thời gian → coi như không thấy.
    private static double CvvExpiryBoostFor(SensitivePattern pattern, Match match, string content)
    {
        if (pattern.CvvExpiryBoost <= 0)
        {
            return 0;
        }

        int start = Math.Max(0, match.Index - CvvExpiryWindow);
        int end = Math.Min(content.Length, match.Index + match.Length + CvvExpiryWindow);
        string window = content.Substring(start, end - start);
        try
        {
            return CvvRegex.IsMatch(window) && ExpiryRegex.IsMatch(window) ? pattern.CvvExpiryBoost : 0;
        }
        catch (RegexMatchTimeoutException)
        {
            return 0;
        }
    }

    // [Thay đổi] S1 giai đoạn 1: hệ số penalty theo ngữ cảnh TOÀN tài liệu (doc_context_discount_*): tài liệu có 1 trong
    // các từ khoá "hướng dẫn", "tutorial", "ví dụ"... → nhân điểm với factor. Rule không khai thì 1.0 (không giảm).
    private static double DocDiscountFactor(SensitivePattern pattern, string content, Dictionary<PlaceholderRule, double> cache)
    {
        var placeholder = pattern.Placeholder;
        if (placeholder == null || placeholder.DocDiscountKeywords.Count == 0)
        {
            return 1.0;
        }

        if (!cache.TryGetValue(placeholder, out double factor))
        {
            bool isGuideDocument = placeholder.DocDiscountKeywords.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase));
            factor = isGuideDocument ? placeholder.DocDiscountFactor : 1.0;
            cache[placeholder] = factor;
        }
        return factor;
    }

    // [Thay đổi] S1 giai đoạn 1: cho FileScorer (Classification) dùng lại đúng cách chuẩn hoá giá trị khi gộp theo rule
    internal static string NormalizeForDedup(string value) => NormalizeValue(value);

    // [Thay đổi checklist Bậc 2 - "Level gate đầy đủ 4 điều kiện"]: xếp hạng mức độ nghiêm trọng CHỈ để so sánh
    // nội bộ trong Scanning layer (vd "có rule khác đạt ≥ CONFIDENTIAL không") — KHÔNG dùng enum SensitivityLevel
    // của Classification layer để tránh Scanning phụ thuộc ngược vào Classification (Classification mới là bên
    // phụ thuộc Scanning, không phải chiều ngược lại). Cùng thứ tự với SensitivityLevelExtensions bên đó.
    private static int SeverityRank(string severity) => severity.Trim().ToUpperInvariant() switch
    {
        "RESTRICTED" or "SECRET" or "HIGH" => 3,
        "CONFIDENTIAL" or "MEDIUM" => 2,
        "INTERNAL" => 1,
        "PUBLIC" => 0,
        _ => 0
    };
}
