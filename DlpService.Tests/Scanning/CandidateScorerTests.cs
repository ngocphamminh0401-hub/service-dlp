// [Thay đổi] File mới (Công thức Go): kiểm tra công thức điểm candidate mới (boost theo khoảng cách, thưởng checksum
// +0,12 trần 0,99, cvv_expiry_boost, hệ số tài liệu) và ngưỡng MinConfidence của RegexScanner.
using System.Text.RegularExpressions;
using DlpService.Scanning;

namespace DlpService.Tests.Scanning;

public class CandidateScorerTests
{
    // ---- Khoảng cách từ khoá → chỗ khớp (giống kwDistance của Go) ----

    [Theory]
    [InlineData(12, 10, 16, 0)]  // từ khoá nằm trong chỗ khớp
    [InlineData(5, 10, 16, 5)]   // đứng trước: matchStart − vị trí
    [InlineData(16, 10, 16, 1)]  // đứng ngay sau: vị trí − matchEnd + 1
    [InlineData(30, 10, 16, 15)]
    public void Distance_GiongGo(int keywordIndex, int matchStart, int matchEnd, int expected)
    {
        Assert.Equal(expected, CandidateScorer.Distance(keywordIndex, matchStart, matchEnd));
    }

    // ---- Boost theo vùng ----

    [Theory]
    [InlineData(0, true, 0.15)]
    [InlineData(20, true, 0.15)]
    [InlineData(21, true, 0.10)]
    [InlineData(50, false, 0.05)]
    [InlineData(51, false, 0.02)]
    [InlineData(200, true, 0.05)]
    [InlineData(201, true, 0.0)]  // ngoài vùng 3 → không cộng
    public void ZoneBoost_TheoKhoangCach(int distance, bool primary, double expected)
    {
        Assert.Equal(expected, CandidateScorer.ZoneBoost(distance, primary), 3);
    }

    // ---- Công thức điểm ----

    [Fact]
    public void Score_ViDuCccd_QuaChecksum_TranO99()
    {
        // 0,85 × 0,90 + 0,15 = 0,915 → min(0,915 + 0,12; 0,99) = 0,99
        double score = CandidateScorer.Score(0.85, 0.90, 0.15, checksumPassed: true, cvvExpiryBoost: 0, docDiscountFactor: 1.0);
        Assert.Equal(0.99, score, 3);
    }

    [Fact]
    public void Score_DiemDaLa1_QuaChecksum_VeO99_GiongGo()
    {
        double score = CandidateScorer.Score(1.0, 1.0, 0.30, checksumPassed: true, cvvExpiryBoost: 0, docDiscountFactor: 1.0);
        Assert.Equal(0.99, score, 3);
    }

    [Fact]
    public void Score_KhongKhaiConfidence_DungMacDinh075()
    {
        double score = CandidateScorer.Score(null, 1.0, 0, checksumPassed: false, cvvExpiryBoost: 0, docDiscountFactor: 1.0);
        Assert.Equal(0.75, score, 3);
    }

    [Fact]
    public void Score_CvvExpiryBoost_DuocCong()
    {
        double score = CandidateScorer.Score(0.80, 1.0, 0, checksumPassed: false, cvvExpiryBoost: 0.07, docDiscountFactor: 1.0);
        Assert.Equal(0.87, score, 3);
    }

    [Fact]
    public void Score_TaiLieuHuongDan_NhanHeSo_SauKhiClamp()
    {
        // 0,9 + 0,3 = 1,2 → clamp 1,0 → × 0,5 = 0,5 (không phải 0,6)
        double score = CandidateScorer.Score(0.90, 1.0, 0.30, checksumPassed: false, cvvExpiryBoost: 0, docDiscountFactor: 0.5);
        Assert.Equal(0.5, score, 3);
    }

    // ---- RegexScanner: boost theo từng lần xuất hiện + ngưỡng MinConfidence ----

    // Rule giả: 6 chữ số, confidence 0,5, weight 1, từ khoá primary "mã"
    private static SensitivePattern SixDigits(double cvvExpiryBoost = 0) => new()
    {
        Name = "TestSixDigits",
        Regex = new Regex(@"\b\d{6}\b", RegexOptions.None, TimeSpan.FromSeconds(1)),
        Severity = "CONFIDENTIAL",
        Confidence = 0.5,
        Weight = 1.0,
        PrimaryKeywords = ["mã"],
        CvvExpiryBoost = cvvExpiryBoost
    };

    [Fact]
    public void Scan_KhongCoTuKhoa_DuoiNguong_BiLoai()
    {
        var scanner = new RegexScanner([SixDigits()]);
        Assert.Empty(scanner.Scan("số 123456 trong văn bản").Matches);
    }

    [Fact]
    public void Scan_NguongBang0_GiuLaiChoKhopDiemThap()
    {
        var scanner = new RegexScanner([SixDigits()], minConfidence: 0);
        var match = Assert.Single(scanner.Scan("số 123456 trong văn bản").Matches);
        Assert.Equal(0.5, match.Score, 3);
    }

    [Fact]
    public void Scan_TuKhoaGan_DuocCong015_QuaNguong()
    {
        var scanner = new RegexScanner([SixDigits()]);
        var match = Assert.Single(scanner.Scan("mã 123456").Matches);
        Assert.Equal(0.65, match.Score, 3);
        Assert.Equal(0.65, match.Confidence!.Value, 3); // Confidence (bypass) = Score
    }

    [Fact]
    public void Scan_TuKhoaLapLai_MoiLanDeuCong_TranO30()
    {
        // "mã" xuất hiện 3 lần ngay cạnh → 0,15 × 3 = 0,45 → trần 0,30 → 0,5 + 0,3 = 0,8
        var scanner = new RegexScanner([SixDigits()]);
        var match = Assert.Single(scanner.Scan("mã mã mã 123456").Matches);
        Assert.Equal(0.80, match.Score, 3);
    }

    [Fact]
    public void Scan_TuKhoaXaHon200KyTu_KhongDuocCong()
    {
        var scanner = new RegexScanner([SixDigits()], minConfidence: 0);
        string content = "mã" + new string(' ', 250) + "123456";
        var match = Assert.Single(scanner.Scan(content).Matches);
        Assert.Equal(0.5, match.Score, 3);
    }

    [Fact]
    public void Scan_CvvVaNgayHetHan_GanSoThe_DuocCong()
    {
        var scanner = new RegexScanner([SixDigits(cvvExpiryBoost: 0.07)], minConfidence: 0);
        var withContext = Assert.Single(scanner.Scan("thẻ 123456 hạn 09/27 CVV: 321").Matches);
        Assert.Equal(0.57, withContext.Score, 3);

        // Chỉ có ngày hết hạn, thiếu CVV → không cộng
        var expiryOnly = Assert.Single(scanner.Scan("thẻ 123456 hạn 09/27").Matches);
        Assert.Equal(0.5, expiryOnly.Score, 3);
    }
}
