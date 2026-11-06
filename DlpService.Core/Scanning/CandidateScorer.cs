// [Thay đổi] File mới (S1 giai đoạn 1 - "tính điểm cấp candidate"): gộp mọi tín hiệu của 1 chỗ khớp thành 1 điểm 0-1.
// Candidate = 1 chỗ regex đã khớp và đã qua các bước lọc (validator, placeholder, từ khoá bắt buộc...).
// [Thay đổi] Áp công thức của engine Go tham khảo (D:\dlp-classifier\internal\engine\regex.go, matchAllPatterns):
// boost từ khoá theo KHOẢNG CÁCH 3 vùng, thưởng validator +0,12 (trần 0,99), cvv_expiry_boost, và ngưỡng MinConfidence
// để LOẠI chỗ khớp điểm thấp. Bỏ khoản phạt negation −0,30 (Go chỉ hạ mức, không trừ điểm).
namespace DlpService.Scanning;

/// <summary>Tính điểm cấp candidate theo công thức của engine Go: điểm gốc + boost từ khoá theo khoảng cách + thưởng
/// validator checksum + thưởng CVV/ngày hết hạn, rồi nhân hệ số tài liệu hướng dẫn/mẫu.</summary>
/// <remarks>
///   điểm = confidence × weight                         ← do người viết rule khai (thiếu confidence → 0,75)
///   điểm = clamp(điểm + boost từ khoá)                  ← theo khoảng cách, mỗi LẦN xuất hiện đều cộng, tối đa 0,30:
///            0–20 ký tự  : primary +0,15 / secondary +0,07
///            21–50 ký tự : primary +0,10 / secondary +0,05
///            51–200 ký tự: primary +0,05 / secondary +0,02
///   nếu qua validator CHECKSUM: điểm = min(điểm + 0,12; 0,99)   ← Luhn/MST/IBAN/CCCD
///   nếu rule khai cvv_expiry_boost và quanh chỗ khớp (±300) có CVV + ngày hết hạn: điểm = clamp(điểm + boost)
///   nếu cả tài liệu là hướng dẫn/mẫu: điểm = clamp(điểm × doc_context_discount_factor)
///   điểm &lt; MinConfidence (mặc định 0,60) → RegexScanner bỏ chỗ khớp
/// Ví dụ: CCCD (confidence 0,85, weight 0,90), chữ "CCCD" đứng ngay trước (vùng 1), qua validator mã tỉnh:
///        0,765 + 0,15 = 0,915 → min(0,915 + 0,12; 0,99) = 0,99.
/// Khác Go: khoảng cách tính theo KÝ TỰ (C# string), Go tính theo BYTE UTF-8 — với tiếng Việt có dấu, vùng của C# rộng
/// hơn một chút về mặt "số chữ" nhìn thấy.
/// </remarks>
public static class CandidateScorer
{
    /// <summary>Confidence dùng khi pattern không khai (rule dạng cũ, VD credit-card.json).</summary>
    public const double DefaultConfidence = 0.75;

    // [Thay đổi] 0,10 → 0,12 và thêm trần 0,99 cho giống Go ("additive, giữ relative ranking giữa các IIN")
    /// <summary>Thưởng khi chỗ khớp đã qua validator checksum.</summary>
    public const double ChecksumBonus = 0.12;

    /// <summary>Điểm tối đa sau khi cộng thưởng checksum.</summary>
    public const double ChecksumCap = 0.99;

    // [Thay đổi] Ngưỡng MinConfidence mặc định của Go (DefaultEngineConfig)
    /// <summary>Chỗ khớp có điểm dưới ngưỡng này bị loại (mặc định của engine Go).</summary>
    public const double DefaultMinConfidence = 0.60;

    // [Thay đổi] Boost theo khoảng cách (Go: contextZones, maxTotalBoost, ContextWindow = 200)
    /// <summary>Khoảng cách tối đa (ký tự) từ từ khoá tới chỗ khớp để còn được tính boost.</summary>
    public const int BoostWindow = 200;

    /// <summary>Tổng boost từ khoá tối đa, dù có bao nhiêu từ khoá.</summary>
    public const double MaxKeywordBoost = 0.30;

    // 3 vùng khoảng cách, boost giảm dần khi từ khoá xa chỗ khớp
    private static readonly (int MaxDistance, double Primary, double Secondary)[] Zones =
    [
        (20, 0.15, 0.07),  // vùng 1: từ khoá ngay cạnh (trong cụm)
        (50, 0.10, 0.05),  // vùng 2: cùng câu gần
        (200, 0.05, 0.02)  // vùng 3: cùng đoạn văn
    ];

    /// <summary>Khoảng cách từ vị trí BẮT ĐẦU của từ khoá tới chỗ khớp [matchStart, matchEnd) — giống kwDistance của Go:
    /// nằm trong chỗ khớp = 0; đứng trước = matchStart − vị trí; đứng sau = vị trí − matchEnd + 1.</summary>
    public static int Distance(int keywordIndex, int matchStart, int matchEnd)
    {
        if (keywordIndex >= matchStart && keywordIndex < matchEnd)
        {
            return 0;
        }
        return keywordIndex < matchStart ? matchStart - keywordIndex : keywordIndex - matchEnd + 1;
    }

    /// <summary>Boost của 1 lần từ khoá xuất hiện ở khoảng cách <paramref name="distance"/>; ngoài mọi vùng → 0.</summary>
    public static double ZoneBoost(int distance, bool primary)
    {
        foreach (var zone in Zones)
        {
            if (distance <= zone.MaxDistance)
            {
                return primary ? zone.Primary : zone.Secondary;
            }
        }
        return 0;
    }

    /// <summary>Tính điểm candidate 0-1.</summary>
    /// <param name="confidence">Confidence gốc của pattern con; null = dùng DefaultConfidence.</param>
    /// <param name="weight">Trọng số của rule.</param>
    /// <param name="keywordBoost">Tổng boost từ khoá theo khoảng cách (đã giới hạn ≤ MaxKeywordBoost).</param>
    /// <param name="checksumPassed">Chỗ khớp đã qua validator checksum.</param>
    /// <param name="cvvExpiryBoost">[Thay đổi] Thưởng CVV + ngày hết hạn nếu tìm thấy quanh chỗ khớp; 0 = không áp.</param>
    /// <param name="docDiscountFactor">Hệ số giảm theo ngữ cảnh tài liệu; 1.0 = không giảm.</param>
    // [Thay đổi] Bỏ tham số negationDowngrade (Go không trừ điểm khi negation, chỉ hạ mức); thêm cvvExpiryBoost.
    // Thứ tự các bước giữ ĐÚNG như Go vì có clamp/min xen giữa (đổi thứ tự sẽ ra số khác).
    public static double Score(double? confidence, double weight, double keywordBoost, bool checksumPassed,
        double cvvExpiryBoost, double docDiscountFactor)
    {
        double score = Math.Clamp((confidence ?? DefaultConfidence) * weight + keywordBoost, 0, 1);

        if (checksumPassed)
        {
            // Giống Go: min(score + 0,12; 0,99) — kể cả khi score đã là 1,0 thì cũng về 0,99
            score = Math.Min(score + ChecksumBonus, ChecksumCap);
        }

        if (cvvExpiryBoost > 0)
        {
            score = Math.Clamp(score + cvvExpiryBoost, 0, 1);
        }

        return Math.Clamp(score * docDiscountFactor, 0, 1);
    }
}
