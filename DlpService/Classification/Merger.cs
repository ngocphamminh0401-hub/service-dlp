// [Thay đổi] File mới (Merger - kiến trúc mục 7): tính S_final theo công thức mục 7.1 + bảng quyết định mục 7.2.
// Nguyên tắc: LLM không quyết định; chỉ điều chỉnh điểm và đề xuất nhãn. Code phía Regex service (hàm này) quyết định.
using DlpService.Scanning;
using DlpService.Storage;

namespace DlpService.Classification;

/// <summary>Ý kiến LLM về 1 rule cụ thể (rút gọn từ Verdict trong contracts/inference.proto — tách khỏi kiểu gRPC để
/// Classification không phụ thuộc ngược vào Llm/contracts).</summary>
/// <param name="RuleId">Rỗng nếu LLM tự phát hiện thêm (không gắn với rule regex đã có).</param>
/// <param name="TruePositive">true = LLM xác nhận nhạy cảm; false = LLM bác bỏ.</param>
/// <param name="Confidence">Độ tin cậy của LLM (0-1).</param>
public sealed record RuleVerdict(string RuleId, bool TruePositive, double Confidence);

/// <summary>Kết quả merge S_final của 1 file.</summary>
/// <param name="SFinal">Điểm cuối cùng sau merge, trong [0, 1].</param>
/// <param name="NeedsReview">true nếu còn cần người/LLM khác xem lại (mục 7.2: LLM bác bỏ nhưng conf thấp, hoặc LLM phát hiện thêm).</param>
/// <param name="Source">"llm" nếu có ít nhất 1 rule được LLM tác động tới điểm hoặc LLM phát hiện thêm; ngược lại "regex".</param>
public sealed record MergeResult(double SFinal, bool NeedsReview, string Source);

/// <summary>Merger — áp công thức S_final (mục 7.1) + bảng quyết định (mục 7.2) lên kết quả regex (S1, findings) và ý
/// kiến LLM (verdicts theo rule).</summary>
public static class Merger
{
    /// <summary>Trọng số nâng điểm khi LLM xác nhận (mục 7.1, chỉnh bằng số liệu shadow mode).</summary>
    public const double WUp = 0.30;

    /// <summary>Trọng số hạ điểm khi LLM bác bỏ (mục 7.1).</summary>
    public const double WDown = 0.40;

    /// <summary>Ngưỡng tin cậy tối thiểu để LLM được phép hạ điểm khi bác bỏ (mục 7.1/7.2).</summary>
    public const double ConfidenceThreshold = 0.80;

    /// <summary>Merge 1 rule/finding theo bảng quyết định mục 7.2.</summary>
    /// <param name="s1Rule">Điểm S1 của rule (RuleScore.Score, hoặc FindingRecord.Score).</param>
    /// <param name="hardRule">true nếu rule có validator checksum (Luhn/MST/IBAN...) — LLM không được bác bỏ.</param>
    /// <param name="policy">Chính sách dùng ý kiến LLM của rule này.</param>
    /// <param name="verdict">Ý kiến LLM cho ĐÚNG rule này; null nếu LLM không nói gì về rule này (không gửi kèm, hoặc
    /// policy=Never nên không xét tới).</param>
    /// <returns>(S_final của rule, cần xem lại hay không).</returns>
    public static (double SFinal, bool NeedsReview) MergeOne(double s1Rule, bool hardRule, LlmPolicy policy, RuleVerdict? verdict)
    {
        // llm_policy: never → bỏ qua hoàn toàn ý kiến LLM cho rule đó (mục 7.2). Không có verdict cũng coi như giữ nguyên.
        if (policy == LlmPolicy.Never || verdict == null)
        {
            return (s1Rule, false);
        }

        if (verdict.TruePositive)
        {
            // Regex và LLM cùng cho là nhạy cảm → nâng điểm
            return (Math.Clamp(s1Rule + WUp * verdict.Confidence, 0, 1), false);
        }

        // LLM bác bỏ
        if (hardRule)
        {
            // Luật an toàn (mục 7.2): rule hard_rule=true (Luhn, checksum đúng) → LLM không được bác bỏ
            return (s1Rule, false);
        }

        if (verdict.Confidence >= ConfidenceThreshold)
        {
            // Regex nghi, LLM bác bỏ, conf >= 0.8 → hạ điểm theo công thức
            return (Math.Clamp(s1Rule - WDown * verdict.Confidence, 0, 1), false);
        }

        // Regex nghi, LLM bác bỏ, conf < 0.8 → giữ điểm; needs_review = 1
        return (s1Rule, true);
    }

    /// <summary>Merge S_final cấp file: gộp mọi finding đã merge riêng (mục 7.3: lấy điểm cao nhất trong các rule) +
    /// xét trường hợp LLM phát hiện thêm rule mà regex bỏ sót.</summary>
    /// <param name="s1File">S1 cấp file (điểm regex ban đầu) — dùng làm sàn, đề phòng findings rỗng.</param>
    /// <param name="findings">Các finding regex đã lưu của file (Storage.FindingRecord).</param>
    /// <param name="verdicts">Verdict LLM trả về cho job này (rỗng nếu LLM không chạy/lỗi — khi đó không gọi hàm này).</param>
    /// <param name="policyOf">Tra llm_policy theo RuleId (RegexScanner.PolicyOf).</param>
    /// <remarks>[Thay đổi] Mục 7.3 "Gộp nhiều chunk": 1 file có thể chia nhiều chunk gửi LLM, mỗi chunk trả verdict
    /// riêng cho CÙNG 1 rule_id (VD rule khớp cả ở chunk "context" lẫn chunk "sample") → verdicts truyền vào đây có thể
    /// có NHIỀU phần tử cùng RuleId. Các verdict cùng rule ĐỒNG THUẬN (tất cả xác nhận, hoặc tất cả bác bỏ) thì gộp
    /// thành 1 (lấy độ tin cậy cao nhất — tín hiệu mạnh nhất) rồi merge bình thường; MÂU THUẪN (vừa xác nhận vừa bác
    /// bỏ) thì "coi là không chắc": GIỮ NGUYÊN S1 (không nâng, không hạ) và bật needs_review, đúng nguyên văn mục 7.3.</remarks>
    public static MergeResult MergeFile(double s1File, IReadOnlyList<FindingRecord> findings, IReadOnlyList<RuleVerdict> verdicts,
        Func<string, LlmPolicy> policyOf)
    {
        var verdictsByRule = verdicts
            .Where(v => !string.IsNullOrEmpty(v.RuleId))
            .GroupBy(v => v.RuleId)
            .ToDictionary(g => g.Key, g => g.ToList());

        double sFinal = s1File;
        bool needsReview = false;
        bool llmTouched = false;

        foreach (var finding in findings)
        {
            var policy = policyOf(finding.RuleId);
            double ruleSFinal = finding.Score;
            bool ruleNeedsReview = false;

            if (policy != LlmPolicy.Never && verdictsByRule.TryGetValue(finding.RuleId, out var group))
            {
                llmTouched = true;
                bool allConfirm = group.All(v => v.TruePositive);
                bool allReject = group.All(v => !v.TruePositive);

                if (allConfirm || allReject)
                {
                    // Đồng thuận giữa các chunk → gộp thành 1 verdict, lấy độ tin cậy CAO NHẤT rồi merge như bình thường
                    var merged = new RuleVerdict(finding.RuleId, allConfirm, group.Max(v => v.Confidence));
                    (ruleSFinal, ruleNeedsReview) = MergeOne(finding.Score, finding.HardRule, policy, merged);
                }
                else
                {
                    // Mục 7.3: các chunk mâu thuẫn (chunk này xác nhận, chunk khác bác bỏ cùng rule) → không chắc,
                    // giữ nguyên S1 (không hạ, cũng không nâng vì bằng chứng không đồng nhất), cần xem lại
                    ruleNeedsReview = true;
                }
            }

            sFinal = Math.Max(sFinal, ruleSFinal);
            needsReview |= ruleNeedsReview;
        }

        // Regex bỏ sót, LLM phát hiện thêm (mục 7.2): verdict không gắn rule_id nào đã có, LLM cho là true positive.
        // "không tự áp policy nặng (chặn, mã hoá)" → chỉ đánh dấu needs_review, KHÔNG tự nâng S_final ở đây.
        bool llmFoundNew = verdicts.Any(v => string.IsNullOrEmpty(v.RuleId) && v.TruePositive);
        needsReview |= llmFoundNew;

        string source = (llmTouched || llmFoundNew) ? "llm" : "regex";
        return new MergeResult(sFinal, needsReview, source);
    }
}
