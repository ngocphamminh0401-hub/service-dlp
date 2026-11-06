// [Thay đổi] File mới: các phép tính chỉ số — precision/recall nhị phân, ma trận nhầm lẫn 4 mức, thống kê thời gian.
using DlpService.Classification;

namespace DlpBaseline;

/// <summary>Kết quả đo của 1 file (1 mẫu x 1 định dạng). Chỉ chứa tên rule + số đo, KHÔNG chứa giá trị nhạy cảm.</summary>
internal sealed record FileResult(
    Sample Sample,
    string Format,
    long SizeBytes,
    bool ReadOk,
    string? ReadError,
    SensitivityLevel Predicted,
    bool PiiHit,
    int Chars,
    int MatchCount,
    string RulesFired,
    double ExtractMs,
    double ScanMs,
    double ClassifyMs,
    string TimedOutRules = "", // [Thay đổi] Log timeout: tên các rule bị timeout, rỗng = quét trọn
    FileScore? S1 = null, // [Thay đổi] S1 giai đoạn 2: điểm S1 cấp file (null = đọc lỗi → coi như 0)
    FileConfidenceResult? Conf = null, // [Thay đổi] FileConfidence (shadow): domain đoán như service thật; null = không tính
    FileConfidenceResult? ConfTrueDomain = null, // [Thay đổi] FileConfidence: cùng công thức nhưng domain lấy từ nhãn
    DlpService.Scanning.ScanTelemetry? Telemetry = null) // [Thay đổi] Chẩn đoán nhánh B: counter near-miss ghi ra CSV
{
    /// <summary>S1 của file; đọc lỗi thì trả FileScore.Empty (mọi mức = 0).</summary>
    public FileScore Scores => S1 ?? FileScore.Empty;

    /// <summary>true nếu mọi rule quét xong (service sẽ kết luận mức); false thì service log "CHƯA KẾT LUẬN ĐƯỢC".</summary>
    public bool ScanComplete => TimedOutRules.Length == 0;

    public double TotalMs => ExtractMs + ScanMs + ClassifyMs;
}

/// <summary>Bảng 2x2 của bài toán có/không: TP (bắt đúng), FP (báo nhầm), FN (bỏ sót), TN (bỏ qua đúng).</summary>
internal sealed record BinaryScore(int Tp, int Fp, int Fn, int Tn)
{
    public int Total => Tp + Fp + Fn + Tn;

    /// <summary>Trong các file bị báo "có", bao nhiêu % đúng là có. Thấp = báo nhầm nhiều.</summary>
    public double? Precision => Tp + Fp == 0 ? null : (double)Tp / (Tp + Fp);

    /// <summary>Trong các file thật sự "có", bắt được bao nhiêu %. Thấp = bỏ sót nhiều.</summary>
    public double? Recall => Tp + Fn == 0 ? null : (double)Tp / (Tp + Fn);

    /// <summary>Trung bình điều hoà của precision và recall.</summary>
    public double? F1 => Precision is double p && Recall is double r && p + r > 0 ? 2 * p * r / (p + r) : null;

    public static BinaryScore From(IEnumerable<FileResult> results, Func<FileResult, bool> truth, Func<FileResult, bool> predicted)
    {
        int tp = 0, fp = 0, fn = 0, tn = 0;
        foreach (var r in results)
        {
            bool t = truth(r), p = predicted(r);
            if (t && p) tp++;
            else if (!t && p) fp++;
            else if (t && !p) fn++;
            else tn++;
        }
        return new BinaryScore(tp, fp, fn, tn);
    }
}

/// <summary>Ma trận nhầm lẫn 4 mức: Counts[mức thật, mức dự đoán].</summary>
internal sealed class ConfusionMatrix
{
    public static readonly SensitivityLevel[] Levels = Enum.GetValues<SensitivityLevel>();

    public int[,] Counts { get; } = new int[Levels.Length, Levels.Length];

    public int Total { get; private set; }

    public static ConfusionMatrix From(IEnumerable<FileResult> results)
    {
        var matrix = new ConfusionMatrix();
        foreach (var r in results)
        {
            matrix.Counts[(int)r.Sample.Truth, (int)r.Predicted]++;
            matrix.Total++;
        }
        return matrix;
    }

    /// <summary>Tỷ lệ file xếp ĐÚNG mức.</summary>
    public double? Accuracy => Total == 0 ? null : (double)Enumerable.Range(0, Levels.Length).Sum(i => Counts[i, i]) / Total;

    /// <summary>Xem 1 mức như bài toán có/không ("có phải mức này không?").</summary>
    public BinaryScore ForLevel(int level)
    {
        int tp = Counts[level, level];
        int rowSum = Enumerable.Range(0, Levels.Length).Sum(j => Counts[level, j]);
        int colSum = Enumerable.Range(0, Levels.Length).Sum(i => Counts[i, level]);
        return new BinaryScore(tp, colSum - tp, rowSum - tp, Total - rowSum - colSum + tp);
    }

    /// <summary>Trung bình F1 của các mức CÓ mẫu (mỗi mức nặng như nhau, không phụ thuộc số lượng).</summary>
    public double? MacroF1
    {
        get
        {
            var f1s = Enumerable.Range(0, Levels.Length)
                .Select(ForLevel)
                .Where(s => s.Tp + s.Fn > 0)
                .Select(s => s.F1 ?? 0)
                .ToList();
            return f1s.Count == 0 ? null : f1s.Average();
        }
    }
}

/// <summary>Tóm tắt 1 dãy thời gian (ms).</summary>
internal sealed record TimingSummary(int Count, double Avg, double P50, double P95, double Max, double Sum)
{
    public static TimingSummary From(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return new TimingSummary(0, 0, 0, 0, 0, 0);
        }
        return new TimingSummary(sorted.Count, sorted.Average(), Percentile(sorted, 50), Percentile(sorted, 95), sorted[^1], sorted.Sum());
    }

    // Phân vị kiểu "nearest-rank": p95 = giá trị mà 95% số mẫu nhỏ hơn hoặc bằng
    private static double Percentile(List<double> sorted, double p)
    {
        int rank = (int)Math.Ceiling(p / 100 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}
