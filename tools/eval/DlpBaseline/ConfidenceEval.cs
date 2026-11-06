// [Thay đổi] File mới (FileConfidence 2 nhánh — shadow): đánh giá điểm conf (công thức Pha 21 của Go, hệ số copy nguyên)
// trên engine C#. Đo 2 việc: (1) conf có còn "đúng nghĩa xác suất" không — AUC (phân biệt đúng/sai) và ECE (điểm có khớp tỉ
// lệ đúng thật không) theo từng nhánh; (2) nếu dùng conf để chọn file giữ ở Lớp 1 (P19 / 1 ngưỡng chung) thì được gì so với
// Router S1 hiện tại. Mục tiêu của từng nhánh giống lúc Go fit: A = "không bị chấm thấp" (thật ≤ dự đoán), B/Short = "thật là Public".
using System.Globalization;
using System.Text;
using DlpService.Classification;

namespace DlpBaseline;

internal static class ConfidenceEval
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Router S1 đang cấu hình trong DlpService/appsettings.json (mục "Score") — để so sánh cùng dữ liệu
    private static readonly RouterOptions CurrentRouter = new() { THigh = 0.80, TLow = 0.10 };

    private static readonly double[] SweepThresholds = [0.50, 0.60, 0.70, 0.80, 0.85, 0.90, 0.95, 0.97, 0.99];

    /// <summary>Kết quả của 1 luật "giữ ở Lớp 1 hay gửi LLM" trên toàn bộ file.</summary>
    private sealed record PolicyStats(string Name, int Files, int Kept, int KeptCorrect, int KeptOver, int KeptUnder, int Sensitive,
        int Wrong, int WrongSent)
    {
        public double KeptPct => Files == 0 ? 0 : (double)Kept / Files;
        public double KeptAccuracy => Kept == 0 ? 0 : (double)KeptCorrect / Kept;
        public double KeptUnderPct => Kept == 0 ? 0 : (double)KeptUnder / Kept;
        public double KeptOverPct => Kept == 0 ? 0 : (double)KeptOver / Kept;
        // Leak recall (Pha 21): trong file nhạy cảm thật, bao nhiêu % KHÔNG bị rò ở Lớp 1 (bị gửi LLM, hoặc giữ với nhãn ≥ thật)
        public double LeakRecall => Sensitive == 0 ? 0 : 1 - (double)KeptUnder / Sensitive;
        // Bắt lỗi: trong file regex xếp sai mức, bao nhiêu % được gửi LLM
        public double ErrorCapture => Wrong == 0 ? 0 : (double)WrongSent / Wrong;
    }

    private static PolicyStats Evaluate(string name, IReadOnlyList<FileResult> files, Func<FileResult, bool> keep)
    {
        int kept = 0, keptCorrect = 0, keptOver = 0, keptUnder = 0, sensitive = 0, wrong = 0, wrongSent = 0;
        foreach (var r in files)
        {
            bool k = keep(r);
            bool isWrong = r.Predicted != r.Sample.Truth;
            if (r.Sample.Truth != SensitivityLevel.Public) sensitive++;
            if (isWrong) wrong++;
            if (k)
            {
                kept++;
                if (!isWrong) keptCorrect++;
                else if (r.Predicted > r.Sample.Truth) keptOver++;
                else keptUnder++;
            }
            else if (isWrong)
            {
                wrongSent++;
            }
        }
        return new PolicyStats(name, files.Count, kept, keptCorrect, keptOver, keptUnder, sensitive, wrong, wrongSent);
    }

    // Mục tiêu "đúng" của từng nhánh — y như lúc Go fit hệ số
    private static bool Target(FileResult r, ConfidenceBranch branch) => branch == ConfidenceBranch.A
        ? r.Sample.Truth <= r.Predicted
        : r.Sample.Truth == SensitivityLevel.Public;

    /// <summary>Dựng mục báo cáo FileConfidence; trả chuỗi rỗng nếu không có file nào có conf (không nạp được model).</summary>
    public static string BuildSection(List<FileResult> results, string modelVersion)
    {
        var usable = results.Where(r => r.ReadOk && r.ScanComplete && r.Conf != null).ToList();
        if (usable.Count == 0)
        {
            return "";
        }

        var md = new StringBuilder();
        md.AppendLine();
        md.AppendLine($"## 5. FileConfidence 2 nhánh (shadow, hệ số Go `{modelVersion}`)");
        md.AppendLine();
        md.AppendLine("- Hệ số copy nguyên từ engine Go (Pha 21), **chưa fit lại cho C#**. Go fit trên chính rawdata này (train 70 %) → số dưới đây "
            + "một phần là in-sample về mặt dữ liệu, nhưng out-of-sample về mặt engine (C# ≠ Go).");
        md.AppendLine("- **Domain đoán** = cách service thật dùng (đoán từ rule đã khớp). **Domain thật** = lấy từ nhãn, chỉ để biết đoán sai domain làm lệch bao nhiêu.");
        md.AppendLine("- Mục tiêu: nhánh A = \"không bị chấm thấp\" (thật ≤ dự đoán); nhánh B/Short = \"thật là Public\". AUC 0,5 = đoán mò, 1 = hoàn hảo. "
            + "ECE = chênh lệch trung bình giữa điểm và tỉ lệ đúng thật (càng nhỏ càng đúng nghĩa xác suất; Go đạt ~0,01 nhánh A, ~0,05 nhánh B).");
        md.AppendLine();

        // 1) Phân bố + AUC/ECE theo nhánh
        md.AppendLine("### Chất lượng điểm theo nhánh");
        md.AppendLine();
        md.AppendLine("| Nhánh | Số file | Tỷ lệ | Tỉ lệ \"đúng\" thật | conf TB | AUC (domain đoán) | ECE (domain đoán) | AUC (domain thật) | ECE (domain thật) |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var g in usable.GroupBy(r => r.Conf!.Branch).OrderBy(g => g.Key))
        {
            var list = g.ToList();
            var y = list.Select(r => Target(r, g.Key)).ToList();
            var guessed = list.Select(r => r.Conf!.Confidence).ToList();
            var truth = list.Select(r => r.ConfTrueDomain!.Confidence).ToList();
            md.AppendLine($"| {g.Key} | {list.Count:N0} | {Pct((double)list.Count / usable.Count)} | {Pct(y.Average(b => b ? 1.0 : 0.0))} | "
                + $"{guessed.Average():0.000} | {Num(Auc(guessed, y))} | {Num(Ece(guessed, y))} | {Num(Auc(truth, y))} | {Num(Ece(truth, y))} |");
        }
        md.AppendLine();

        int domainHit = usable.Count(r => r.Conf!.Domain == r.Sample.Domain);
        md.AppendLine($"- Đoán đúng domain: **{Pct((double)domainHit / usable.Count)}** ({domainHit:N0}/{usable.Count:N0} file).");
        md.AppendLine();

        // 2) Luật giữ Lớp 1: P19 (domain đoán / thật) so với Router S1 hiện tại
        var policies = new List<PolicyStats>
        {
            Evaluate($"Router S1 hiện tại (T_high {CurrentRouter.THigh:0.00}, T_low {CurrentRouter.TLow:0.00}): giữ = không vào vùng xám",
                usable, r => !ScoreRouter.Route(r.Predicted, r.Scores, r.MatchCount, CurrentRouter).NeedsLlm),
            Evaluate("P19 — conf ≥ ngưỡng theo mức (domain đoán)", usable, r => r.Conf!.KeepAtLayer1),
            Evaluate("P19 — conf ≥ ngưỡng theo mức (domain thật)", usable, r => r.ConfTrueDomain!.KeepAtLayer1),
            Evaluate("G95 — conf ≥ 0,95 (domain đoán)", usable, r => r.Conf!.Confidence >= 0.95)
        };
        md.AppendLine("### Luật giữ ở Lớp 1 (không gửi LLM) — toàn bộ file");
        md.AppendLine();
        md.AppendLine("- **Leak recall** = trong file nhạy cảm thật, % không bị rò ở Lớp 1 (được gửi LLM, hoặc giữ với nhãn ≥ thật). "
            + "**Bắt lỗi** = trong file regex xếp sai mức, % được gửi LLM.");
        md.AppendLine();
        AppendPolicyTable(md, policies);

        // 3) Quét 1 ngưỡng chung (domain đoán)
        md.AppendLine("### Quét 1 ngưỡng chung: giữ ⇔ conf ≥ T (domain đoán)");
        md.AppendLine();
        AppendPolicyTable(md, SweepThresholds.Select(t => Evaluate($"T = {t.ToString("0.00", Inv)}", usable, r => r.Conf!.Confidence >= t)).ToList());
        return md.ToString();
    }

    private static void AppendPolicyTable(StringBuilder md, List<PolicyStats> rows)
    {
        md.AppendLine("| Luật | Giữ Lớp 1 | Gửi LLM | Đúng mức (file giữ) | Over (file giữ) | Under (file giữ) | Leak recall | Bắt lỗi |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in rows)
        {
            md.AppendLine($"| {s.Name} | {Pct(s.KeptPct)} | {Pct(1 - s.KeptPct)} | {Pct(s.KeptAccuracy)} | {Pct(s.KeptOverPct)} | "
                + $"{Pct(s.KeptUnderPct)} | {Pct(s.LeakRecall)} | {Pct(s.ErrorCapture)} |");
        }
        md.AppendLine();
    }

    /// <summary>AUC theo Mann-Whitney (hạng trung bình khi bằng điểm); null nếu chỉ có 1 lớp.</summary>
    internal static double? Auc(IReadOnlyList<double> scores, IReadOnlyList<bool> y)
    {
        int pos = y.Count(b => b), neg = y.Count - pos;
        if (pos == 0 || neg == 0)
        {
            return null;
        }

        var order = Enumerable.Range(0, scores.Count).OrderBy(i => scores[i]).ToArray();
        var rank = new double[scores.Count];
        for (int i = 0; i < order.Length;)
        {
            int j = i;
            while (j + 1 < order.Length && scores[order[j + 1]] == scores[order[i]]) j++;
            double avg = (i + j) / 2.0 + 1; // hạng bắt đầu từ 1
            for (int k = i; k <= j; k++) rank[order[k]] = avg;
            i = j + 1;
        }
        double sumPos = Enumerable.Range(0, y.Count).Where(i => y[i]).Sum(i => rank[i]);
        return (sumPos - pos * (pos + 1) / 2.0) / ((double)pos * neg);
    }

    /// <summary>ECE 10 khoảng đều: Σ (số file khoảng / tổng) × |tỉ lệ đúng − điểm TB| của khoảng.</summary>
    internal static double? Ece(IReadOnlyList<double> scores, IReadOnlyList<bool> y, int bins = 10)
    {
        if (scores.Count == 0)
        {
            return null;
        }
        double ece = 0;
        foreach (var bin in Enumerable.Range(0, scores.Count).GroupBy(i => Math.Min(bins - 1, (int)(scores[i] * bins))))
        {
            var idx = bin.ToList();
            double acc = idx.Average(i => y[i] ? 1.0 : 0.0);
            double conf = idx.Average(i => scores[i]);
            ece += (double)idx.Count / scores.Count * Math.Abs(acc - conf);
        }
        return ece;
    }

    private static string Pct(double v) => v.ToString("P1", Inv);
    private static string Num(double? v) => v is double d ? d.ToString("0.000", Inv) : "—";
}
