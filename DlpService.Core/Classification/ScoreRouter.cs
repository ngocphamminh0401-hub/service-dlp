// [Thay đổi] File mới (Router ba vùng - kiến trúc mục 4.7): dựa vào độ tin cậy S1 để quyết định file nào đã đủ chắc
// (final), file nào cần LLM thẩm định. BASELINE v1: mức nhạy cảm VẪN do regex/engine quyết định như cũ (đã đo: precision
// 87%, recall 96,5%) — S1 chỉ là độ tin cậy CỦA MỨC ĐÓ, dùng để định tuyến. Chưa có LLM process nên "cần LLM" hiện chỉ là
// cờ needs_review + log.
namespace DlpService.Classification;

/// <summary>3 vùng của Router (+ trường hợp không có chỗ khớp nào).</summary>
public enum RouteZone
{
    /// <summary>Không có chỗ khớp nào → sạch, final.</summary>
    NoMatch,

    /// <summary>S1 &lt; T_low: tín hiệu quá yếu/nhiễu → final theo regex, không tốn LLM.</summary>
    Low,

    /// <summary>T_low ≤ S1 &lt; T_high: vùng xám → cần LLM thẩm định (hiện: needs_review).</summary>
    Grey,

    /// <summary>S1 ≥ T_high: chắc chắn → final theo regex, không qua LLM.</summary>
    Certain
}

/// <summary>Ngưỡng của Router, đọc từ appsettings.json mục "Score".</summary>
public sealed class RouterOptions
{
    /// <summary>S1 từ mức này trở lên là "chắc chắn" (kiến trúc mục 4.7: mặc định 0,90).</summary>
    public double THigh { get; set; } = 0.90;

    /// <summary>S1 dưới mức này là "nhiễu" (kiến trúc mục 4.7: mặc định 0,30).</summary>
    public double TLow { get; set; } = 0.30;

    /// <summary>true nếu 0 ≤ TLow &lt; THigh ≤ 1.</summary>
    public bool IsValid => TLow >= 0 && THigh <= 1 && TLow < THigh;
}

/// <summary>Kết quả định tuyến 1 file.</summary>
/// <param name="Zone">Vùng.</param>
/// <param name="Confidence">Độ tin cậy S1 dùng để định tuyến.</param>
/// <param name="NeedsLlm">true nếu vùng xám (cần LLM thẩm định / người xem lại).</param>
public sealed record RouteDecision(RouteZone Zone, double Confidence, bool NeedsLlm);

/// <summary>Router ba vùng dựa trên S1.</summary>
/// <remarks>
/// Độ tin cậy dùng để định tuyến = S1 của ĐÚNG mức regex đã gán, tức "xác suất file có dữ liệu ở mức ≥ mức đó":
///   - regex gán Confidential → dùng S1(≥ Confidential);
///   - regex gán Public nhưng có chỗ khớp → dùng S1(≥ Internal) — "có khả năng bị xếp thấp không?".
/// Ví dụ: regex gán Confidential, S1(≥ Confidential) = 0,72 → vùng xám (0,30 ≤ 0,72 &lt; 0,90) → cần LLM.
/// </remarks>
public static class ScoreRouter
{
    /// <summary>Định tuyến 1 file.</summary>
    /// <param name="regexLevel">Mức engine/regex đã gán.</param>
    /// <param name="score">S1 của file (FileScorer).</param>
    /// <param name="matchCount">Số chỗ khớp; 0 → NoMatch.</param>
    /// <param name="options">Ngưỡng T_high/T_low.</param>
    public static RouteDecision Route(SensitivityLevel regexLevel, FileScore score, int matchCount, RouterOptions options)
    {
        if (matchCount == 0)
        {
            return new RouteDecision(RouteZone.NoMatch, 0, NeedsLlm: false);
        }

        double confidence = ConfidenceFor(regexLevel, score);

        if (confidence >= options.THigh)
        {
            return new RouteDecision(RouteZone.Certain, confidence, NeedsLlm: false);
        }
        if (confidence >= options.TLow)
        {
            return new RouteDecision(RouteZone.Grey, confidence, NeedsLlm: true);
        }
        return new RouteDecision(RouteZone.Low, confidence, NeedsLlm: false);
    }

    /// <summary>S1 tương ứng với mức regex đã gán (Public → S1 ≥ Internal).</summary>
    public static double ConfidenceFor(SensitivityLevel regexLevel, FileScore score) => regexLevel switch
    {
        SensitivityLevel.Restricted => score.Restricted,
        SensitivityLevel.Confidential => score.Confidential,
        _ => score.Internal
    };
}
