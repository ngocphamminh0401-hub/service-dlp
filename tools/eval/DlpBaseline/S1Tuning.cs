// [Thay đổi] File mới (S1 giai đoạn 2 - "chọn ngưỡng trên dữ liệu"): dò tìm 3 ngưỡng S1 (Internal/Confidential/Restricted)
// trên 70% dữ liệu (train), rồi kiểm tra trên 30% còn lại (test) và so với cách phân loại hiện tại.
using System.Text;
using DlpService.Classification;

namespace DlpBaseline;

/// <summary>Kết quả chọn ngưỡng S1.</summary>
internal sealed record TuningResult(
    S1Thresholds Thresholds,
    bool MetRecallConstraint,
    double RequiredRecall,
    int TrainFiles,
    int TestFiles,
    BinaryScore CurrentTestBinary,
    BinaryScore S1TestBinary,
    ConfusionMatrix CurrentTestMatrix,
    ConfusionMatrix S1TestMatrix,
    double GreyZoneShare,
    IReadOnlyList<(string Label, double MeanI, double MeanC, double MeanR)> MeanByLabel);

/// <summary>Chia train/test và dò lưới ngưỡng S1.</summary>
/// <remarks>
/// Mục tiêu khi chọn ngưỡng (trên train):
///   1. Ràng buộc: recall "nhạy cảm" (confidential + top_secret) KHÔNG thấp hơn cách phân loại hiện tại trên cùng tập
///      train — S1 không được bỏ sót nhiều hơn.
///   2. Trong các bộ ngưỡng thoả ràng buộc: chọn F1 "nhạy cảm" cao nhất (tức precision cao nhất có thể); hoà thì chọn
///      Macro-F1 4 mức cao hơn (ngưỡng Internal chỉ ảnh hưởng tiêu chí này).
/// Lưới: mỗi ngưỡng 0,05 → 0,95, bước 0,05 (19 giá trị) → 19³ = 6.859 bộ.
/// </remarks>
internal static class S1Tuning
{
    /// <summary>Tỷ lệ mẫu đưa vào train (theo id mẫu).</summary>
    public const int TrainPercent = 70;

    /// <summary>Khoảng quanh ngưỡng coi là "vùng xám" (file nên hỏi LLM/người xem lại ở giai đoạn 3).</summary>
    public const double GreyZoneWidth = 0.10;

    private static readonly double[] Grid = Enumerable.Range(1, 19).Select(i => i * 0.05).ToArray();

    /// <summary>Mẫu thuộc train hay test — chia theo id (không theo file) để bản docx và pdf cùng nội dung luôn chung 1
    /// phía, tránh "lộ đề" (thấy pdf lúc chọn ngưỡng rồi lại chấm điểm trên docx cùng nội dung). Hash FNV-1a cố định nên
    /// lần chạy nào cũng chia giống nhau.</summary>
    public static bool IsTrain(string sampleId)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(sampleId))
        {
            hash = (hash ^ b) * 16777619;
        }
        return hash % 100 < TrainPercent;
    }

    private static bool IsSensitive(SensitivityLevel level) => level >= SensitivityLevel.Confidential;

    public static TuningResult Tune(List<FileResult> results)
    {
        var train = results.Where(r => IsTrain(r.Sample.Id)).ToArray();
        var test = results.Where(r => !IsTrain(r.Sample.Id)).ToList();

        // Mốc: recall của cách phân loại hiện tại trên train
        double requiredRecall = BinaryScore.From(train, r => IsSensitive(r.Sample.Truth), r => IsSensitive(r.Predicted)).Recall ?? 0;

        // Chép ra mảng cho vòng lặp 6.859 × ~18.000 lần chạy nhanh
        int n = train.Length;
        var truth = train.Select(r => (int)r.Sample.Truth).ToArray();
        var s1i = train.Select(r => r.Scores.Internal).ToArray();
        var s1c = train.Select(r => r.Scores.Confidential).ToArray();
        var s1r = train.Select(r => r.Scores.Restricted).ToArray();
        int sensitiveCount = truth.Count(t => t >= (int)SensitivityLevel.Confidential);

        (S1Thresholds Th, double F1, double Macro, bool Feasible)? best = null;
        (S1Thresholds Th, double F1, double Macro, bool Feasible)? bestIgnoringConstraint = null;

        foreach (double tr in Grid)
        foreach (double tc in Grid)
        {
            // Nhị phân chỉ phụ thuộc tc, tr: dự đoán "nhạy cảm" khi S1R ≥ tr hoặc S1C ≥ tc
            int tp = 0, fp = 0;
            for (int k = 0; k < n; k++)
            {
                bool predicted = s1r[k] >= tr || s1c[k] >= tc;
                if (!predicted) continue;
                if (truth[k] >= (int)SensitivityLevel.Confidential) tp++; else fp++;
            }
            double recall = sensitiveCount == 0 ? 0 : (double)tp / sensitiveCount;
            double precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
            bool feasible = recall >= requiredRecall;

            foreach (double ti in Grid)
            {
                var th = new S1Thresholds(ti, tc, tr);
                double macro = MacroF1(truth, s1i, s1c, s1r, th);
                var candidate = (th, f1, macro, feasible);

                if (feasible && (best == null || Better(candidate, best.Value)))
                {
                    best = candidate;
                }
                if (bestIgnoringConstraint == null || Better(candidate, bestIgnoringConstraint.Value))
                {
                    bestIgnoringConstraint = candidate;
                }
            }
        }

        // Không bộ nào đạt ràng buộc recall → dùng bộ F1 cao nhất và báo rõ trong báo cáo
        var chosen = best ?? bestIgnoringConstraint!.Value;
        var thresholds = chosen.Th;

        // Kiểm tra trên test: so S1 với cách hiện tại
        var s1Predicted = test.Select(r => r with { Predicted = FileScorer.LevelFor(r.Scores, thresholds) }).ToList();

        // Vùng xám = quyết định "nhạy cảm / không" sát biên. Biên = max(S1C − τC, S1R − τR): dương là nhạy cảm, âm là không.
        // (Không xét từng ngưỡng riêng: ngưỡng Restricted nhỏ, VD 0,05, sẽ khiến mọi file có S1R = 0 bị tính là "sát ngưỡng".)
        int grey = test.Count(r =>
            Math.Abs(Math.Max(r.Scores.Confidential - thresholds.Confidential, r.Scores.Restricted - thresholds.Restricted)) < GreyZoneWidth);

        var meanByLabel = results
            .GroupBy(r => r.Sample.Truth)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key.ToString(), g.Average(r => r.Scores.Internal), g.Average(r => r.Scores.Confidential), g.Average(r => r.Scores.Restricted)))
            .ToList();

        return new TuningResult(
            thresholds, best != null, requiredRecall, train.Length, test.Count,
            BinaryScore.From(test, r => IsSensitive(r.Sample.Truth), r => IsSensitive(r.Predicted)),
            BinaryScore.From(s1Predicted, r => IsSensitive(r.Sample.Truth), r => IsSensitive(r.Predicted)),
            ConfusionMatrix.From(test),
            ConfusionMatrix.From(s1Predicted),
            test.Count == 0 ? 0 : (double)grey / test.Count,
            meanByLabel);
    }

    // So sánh 2 bộ ngưỡng: F1 nhị phân trước, hoà thì Macro-F1 4 mức
    private static bool Better((S1Thresholds Th, double F1, double Macro, bool Feasible) a, (S1Thresholds Th, double F1, double Macro, bool Feasible) b) =>
        a.F1 > b.F1 + 1e-12 || (Math.Abs(a.F1 - b.F1) <= 1e-12 && a.Macro > b.Macro + 1e-12);

    // Macro-F1 4 mức của 1 bộ ngưỡng (tính thẳng trên mảng, không tạo đối tượng — chạy 6.859 lần)
    private static double MacroF1(int[] truth, double[] s1i, double[] s1c, double[] s1r, S1Thresholds th)
    {
        const int levels = 4;
        Span<int> tp = stackalloc int[levels];
        Span<int> predCount = stackalloc int[levels];
        Span<int> trueCount = stackalloc int[levels];

        for (int k = 0; k < truth.Length; k++)
        {
            int p = s1r[k] >= th.Restricted ? 3 : s1c[k] >= th.Confidential ? 2 : s1i[k] >= th.Internal ? 1 : 0;
            predCount[p]++;
            trueCount[truth[k]]++;
            if (p == truth[k]) tp[p]++;
        }

        double sum = 0;
        int present = 0;
        for (int l = 0; l < levels; l++)
        {
            if (trueCount[l] == 0) continue;
            present++;
            double precision = predCount[l] == 0 ? 0 : (double)tp[l] / predCount[l];
            double recall = (double)tp[l] / trueCount[l];
            sum += precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        }
        return present == 0 ? 0 : sum / present;
    }
}
