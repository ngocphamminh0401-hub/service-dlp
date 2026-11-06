// [Thay đổi] File mới (Router ba vùng): đánh giá Router trên rawdata — mỗi cặp ngưỡng (T_high, T_low) gửi bao nhiêu % file
// xuống LLM, và bắt được bao nhiêu % file mà regex xếp SAI mức. Chọn cặp đề xuất trên train, báo cáo trên test.
using System.Globalization;
using System.Text;
using DlpService.Classification;

namespace DlpBaseline;

internal static class RouterEval
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Kiến trúc mục 4.7: tỷ lệ escalate mục tiêu 2-10%
    private const double MinEscalation = 0.02;
    private const double MaxEscalation = 0.10;

    private static readonly double[] THighs = [0.70, 0.80, 0.85, 0.90, 0.95, 0.98, 0.99, 0.995];
    private static readonly double[] TLows = [0.10, 0.30, 0.50];

    /// <summary>Số liệu của 1 cặp ngưỡng trên 1 tập file.</summary>
    private sealed record Stats(double THigh, double TLow, int Files, int Grey, int Errors, int ErrorsInGrey, int ErrorsInCertain,
        int CertainFiles, int CertainCorrect)
    {
        public double Escalation => Files == 0 ? 0 : (double)Grey / Files;
        public double ErrorCapture => Errors == 0 ? 0 : (double)ErrorsInGrey / Errors;
        public double CertainAccuracy => CertainFiles == 0 ? 0 : (double)CertainCorrect / CertainFiles;
    }

    private static Stats Evaluate(IReadOnlyList<FileResult> files, RouterOptions o)
    {
        int grey = 0, errors = 0, errorsInGrey = 0, errorsInCertain = 0, certain = 0, certainCorrect = 0;
        foreach (var r in files)
        {
            var route = ScoreRouter.Route(r.Predicted, r.Scores, r.MatchCount, o);
            bool wrong = r.Predicted != r.Sample.Truth;
            if (wrong) errors++;
            if (route.Zone == RouteZone.Grey)
            {
                grey++;
                if (wrong) errorsInGrey++;
            }
            if (route.Zone == RouteZone.Certain)
            {
                certain++;
                if (wrong) errorsInCertain++; else certainCorrect++;
            }
        }
        return new Stats(o.THigh, o.TLow, files.Count, grey, errors, errorsInGrey, errorsInCertain, certain, certainCorrect);
    }

    /// <summary>Dựng mục báo cáo Router: mặc định kiến trúc (0,90/0,30), bảng quét ngưỡng (train), cặp đề xuất (test).</summary>
    public static string BuildSection(List<FileResult> results)
    {
        // Chỉ xét file đọc được và quét trọn (file quét chưa trọn không được gắn mức)
        var usable = results.Where(r => r.ReadOk && r.ScanComplete).ToList();
        var train = usable.Where(r => S1Tuning.IsTrain(r.Sample.Id)).ToList();
        var test = usable.Where(r => !S1Tuning.IsTrain(r.Sample.Id)).ToList();

        var md = new StringBuilder();
        md.AppendLine();
        md.AppendLine("## 4. Router ba vùng (mức do regex gán, độ tin cậy = S1 của mức đó)");
        md.AppendLine();
        md.AppendLine("- **Gửi LLM** = % file rơi vào vùng xám. **Bắt lỗi** = trong các file regex xếp SAI mức, bao nhiêu % rơi vào vùng xám "
            + "(LLM có cơ hội sửa). **Lỗi lọt** = file xếp sai nhưng bị coi là \"chắc chắn\" (không ai xem lại).");
        md.AppendLine();

        // 1) Mặc định kiến trúc trên test, chia theo vùng
        var defaults = new RouterOptions();
        md.AppendLine($"### Mặc định kiến trúc (T_high {defaults.THigh:0.00}, T_low {defaults.TLow:0.00}) — tập test");
        md.AppendLine();
        md.AppendLine("| Vùng | Số file | Tỷ lệ | Regex xếp đúng mức |");
        md.AppendLine("|---|---|---|---|");
        foreach (var group in test.GroupBy(r => ScoreRouter.Route(r.Predicted, r.Scores, r.MatchCount, defaults).Zone).OrderBy(g => g.Key))
        {
            int n = group.Count();
            md.AppendLine($"| {ZoneName(group.Key)} | {n:N0} | {Pct((double)n / test.Count)} | {Pct((double)group.Count(r => r.Predicted == r.Sample.Truth) / n)} |");
        }
        md.AppendLine();

        // 2) Quét ngưỡng trên train
        var sweep = (from th in THighs from tl in TLows where tl < th select Evaluate(train, new RouterOptions { THigh = th, TLow = tl })).ToList();
        md.AppendLine("### Quét ngưỡng trên tập train");
        md.AppendLine();
        md.AppendLine("| T_high | T_low | Gửi LLM | Bắt lỗi | Lỗi lọt (chắc chắn nhưng sai) | Vùng chắc chắn xếp đúng |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var s in sweep)
        {
            md.AppendLine($"| {s.THigh.ToString("0.000", Inv)} | {s.TLow:0.00} | {Pct(s.Escalation)} | {Pct(s.ErrorCapture)} | {s.ErrorsInCertain:N0} / {s.Errors:N0} | {Pct(s.CertainAccuracy)} |");
        }
        md.AppendLine();

        // 3) Đề xuất: trong khoảng gửi LLM 2-10%, bắt được nhiều lỗi nhất; không có cặp nào trong khoảng thì lấy cặp gần nhất
        var inRange = sweep.Where(s => s.Escalation >= MinEscalation && s.Escalation <= MaxEscalation).ToList();
        var pick = inRange.Count > 0
            ? inRange.OrderByDescending(s => s.ErrorCapture).ThenBy(s => s.Escalation).First()
            : sweep.OrderBy(s => Math.Abs(s.Escalation - MaxEscalation)).First();
        var onTest = Evaluate(test, new RouterOptions { THigh = pick.THigh, TLow = pick.TLow });

        md.AppendLine($"### Cặp đề xuất (chọn trên train: gửi LLM trong khoảng {Pct(MinEscalation)}-{Pct(MaxEscalation)}, bắt lỗi nhiều nhất)"
            + (inRange.Count == 0 ? " — **không cặp nào nằm trong khoảng mục tiêu**, lấy cặp gần 10% nhất" : ""));
        md.AppendLine();
        md.AppendLine($"- **T_high = {pick.THigh.ToString("0.000", Inv)}, T_low = {pick.TLow:0.00}**");
        md.AppendLine($"- Trên tập test: gửi LLM **{Pct(onTest.Escalation)}** file, bắt **{Pct(onTest.ErrorCapture)}** số file regex xếp sai "
            + $"({onTest.ErrorsInGrey:N0}/{onTest.Errors:N0}), lỗi lọt {onTest.ErrorsInCertain:N0}, vùng chắc chắn xếp đúng {Pct(onTest.CertainAccuracy)}.");
        return md.ToString();
    }

    public static string ZoneName(RouteZone zone) => zone switch
    {
        RouteZone.NoMatch => "Không khớp (sạch)",
        RouteZone.Low => "Tín hiệu yếu (< T_low)",
        RouteZone.Grey => "Vùng xám → LLM",
        RouteZone.Certain => "Chắc chắn (≥ T_high)",
        _ => zone.ToString()
    };

    private static string Pct(double v) => v.ToString("P1", Inv);
}
