// [Thay đổi] File mới (S1 giai đoạn 1 - "tính S1 cấp file"): gộp điểm candidate của mọi chỗ khớp trong 1 file thành
// điểm S1 cho từng mức (Internal/Confidential/Restricted). Giai đoạn 1-2: chỉ TÍNH + GHI RA (log Debug, CSV của
// DlpBaseline) để chọn ngưỡng, CHƯA dùng để quyết định mức — cách phân loại hiện tại (mức cao nhất của module) giữ nguyên.
// Chưa gộp theo cột bảng (người dùng chọn để sau).
using DlpService.Scanning;

namespace DlpService.Classification;

/// <summary>Điểm S1 của 1 rule trong file (để ghi log/gỡ lỗi).</summary>
/// <param name="RuleKey">Mã rule (RuleId, không có thì tên rule).</param>
/// <param name="Level">Mức cao nhất trong các chỗ khớp của rule này (đã qua level_gate).</param>
/// <param name="DistinctValues">Số giá trị khác nhau (sau chuẩn hoá).</param>
/// <param name="NoisyOr">Điểm gộp noisy-OR của tối đa TopValuesPerRule giá trị điểm cao nhất.</param>
/// <param name="DensityFactor">Hệ số mật độ (DensityFloor..1).</param>
/// <param name="Score">Điểm cuối của rule = NoisyOr × DensityFactor.</param>
public sealed record RuleScore(string RuleKey, SensitivityLevel Level, int DistinctValues, double NoisyOr, double DensityFactor, double Score);

/// <summary>Kết quả S1 của 1 file: điểm "file có dữ liệu ở mức ≥ L" cho từng mức L.</summary>
public sealed record FileScore(double Internal, double Confidential, double Restricted, IReadOnlyList<RuleScore> Rules)
{
    /// <summary>File không có chỗ khớp nào.</summary>
    public static readonly FileScore Empty = new(0, 0, 0, []);
}

/// <summary>Ngưỡng S1 cho từng mức (chọn ở giai đoạn 2 trên dữ liệu có nhãn).</summary>
public sealed record S1Thresholds(double Internal, double Confidential, double Restricted);

/// <summary>Tính S1 cấp file theo 3 bước: gộp theo rule (noisy-OR) → điều chỉnh mật độ → gộp giữa các rule theo mức.</summary>
/// <remarks>
/// Bước 1 — gộp theo rule. Cùng 1 rule khớp nhiều lần thì các lần khớp KHÔNG độc lập với nhau, nên:
///   - bỏ trùng giá trị (100 lần cùng 1 email = 1 giá trị), mỗi giá trị lấy điểm cao nhất;
///   - chỉ lấy TopValuesPerRule (5) giá trị điểm cao nhất rồi gộp noisy-OR: S = 1 − Π(1 − sᵢ).
///   Ví dụ 2 giá trị điểm 0,6 và 0,5 → 1 − 0,4 × 0,5 = 0,80. Giới hạn 5 giá trị để 1 bảng 200 email không tự đẩy điểm
///   lên sát 1 (số lượng lớn đã có volume_escalation lo).
/// Bước 2 — mật độ = số giá trị khác nhau / 1.000 ký tự. Tài liệu rất dài chỉ có 1-2 giá trị (mật độ thấp) bị giảm điểm
///   nhẹ, tối đa còn DensityFloor (70%): hệ số = 0,7 + 0,3 × min(1, mật độ / DensityReference).
/// Bước 3 — gộp giữa các rule theo mức: S1(≥ L) = noisy-OR điểm của các rule có mức ≥ L. Nên luôn có
///   S1(Internal) ≥ S1(Confidential) ≥ S1(Restricted). Rule mức Public (VD MST doanh nghiệp) không góp vào mức nào.
/// Cảnh báo đã biết: noisy-OR giả định các bằng chứng ĐỘC LẬP. Nhiều tín hiệu yếu có liên quan với nhau (tên + email +
///   "bệnh viện" trong 1 bài báo public) vẫn đẩy điểm lên — giai đoạn 2 phải kiểm chứng trên dữ liệu.
/// </remarks>
public static class FileScorer
{
    /// <summary>Số giá trị điểm cao nhất của mỗi rule được đưa vào noisy-OR.</summary>
    public const int TopValuesPerRule = 5;

    /// <summary>Hệ số mật độ thấp nhất (tài liệu rất dài, rất ít giá trị).</summary>
    public const double DensityFloor = 0.7;

    /// <summary>Mật độ (giá trị khác nhau / 1.000 ký tự) từ mức này trở lên thì không giảm điểm.</summary>
    public const double DensityReference = 0.5;

    /// <summary>Tính S1 của 1 file từ các chỗ khớp của RegexScanner.</summary>
    /// <param name="matches">Chỗ khớp (đã có Score, Severity).</param>
    /// <param name="contentLength">Số ký tự của nội dung file (để tính mật độ).</param>
    public static FileScore Compute(IReadOnlyList<ScanMatch> matches, int contentLength)
    {
        if (matches.Count == 0)
        {
            return FileScore.Empty;
        }

        double thousandsOfChars = Math.Max(1.0, contentLength / 1000.0);
        var ruleScores = new List<RuleScore>();

        // Bước 1 + 2: từng rule
        foreach (var group in matches.GroupBy(m => m.RuleId ?? m.PatternName))
        {
            // Bỏ trùng giá trị, mỗi giá trị giữ điểm cao nhất
            var bestPerValue = group
                .GroupBy(m => RegexScanner.NormalizeForDedup(m.Value))
                .Select(g => g.Max(m => m.Score))
                .OrderByDescending(s => s)
                .ToList();

            double noisyOr = NoisyOr(bestPerValue.Take(TopValuesPerRule));

            double density = bestPerValue.Count / thousandsOfChars;
            double densityFactor = DensityFloor + (1 - DensityFloor) * Math.Min(1.0, density / DensityReference);

            var level = group.Max(m => m.Severity.ParseSensitivityLevel());
            ruleScores.Add(new RuleScore(group.Key, level, bestPerValue.Count, noisyOr, densityFactor, noisyOr * densityFactor));
        }

        // Bước 3: gộp giữa các rule theo mức
        return new FileScore(
            NoisyOr(ruleScores.Where(r => r.Level >= SensitivityLevel.Internal).Select(r => r.Score)),
            NoisyOr(ruleScores.Where(r => r.Level >= SensitivityLevel.Confidential).Select(r => r.Score)),
            NoisyOr(ruleScores.Where(r => r.Level >= SensitivityLevel.Restricted).Select(r => r.Score)),
            ruleScores);
    }

    /// <summary>Đổi S1 thành mức theo ngưỡng: mức cao nhất có S1 ≥ ngưỡng của mức đó, không mức nào đạt thì Public.
    /// Dùng ở giai đoạn 2 (chọn ngưỡng) và giai đoạn 3 (chuyển sang dùng S1).</summary>
    public static SensitivityLevel LevelFor(FileScore score, S1Thresholds thresholds)
    {
        if (score.Restricted >= thresholds.Restricted) return SensitivityLevel.Restricted;
        if (score.Confidential >= thresholds.Confidential) return SensitivityLevel.Confidential;
        if (score.Internal >= thresholds.Internal) return SensitivityLevel.Internal;
        return SensitivityLevel.Public;
    }

    /// <summary>Noisy-OR: xác suất "ít nhất 1 bằng chứng đúng" = 1 − Π(1 − sᵢ). Không có bằng chứng → 0.</summary>
    public static double NoisyOr(IEnumerable<double> scores)
    {
        double allWrong = 1.0;
        foreach (double s in scores)
        {
            allWrong *= 1 - Math.Clamp(s, 0, 1);
        }
        return 1 - allWrong;
    }
}
