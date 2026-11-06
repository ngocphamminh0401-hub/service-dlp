// [Thay đổi] File mới (FileConfidence 2 nhánh): kiểm tra nạp đúng file hệ số thật, chia nhánh A/B/Short, công thức (tính tay
// đối chiếu), đoán domain, và các counter near-miss RegexScanner đếm (ScanTelemetry).
using System.Text.RegularExpressions;
using DlpService.Classification;
using DlpService.Scanning;

namespace DlpService.Tests.Classification;

public class FileConfidenceTests
{
    // File hệ số thật được copy ra thư mục chạy test (ProjectReference tới DlpService, CopyToOutputDirectory)
    private static readonly FileConfidenceModel Model =
        FileConfidenceModel.Load(Path.Combine(AppContext.BaseDirectory, "confidence", "file-confidence-v1.json"));

    private static readonly FileConfidenceScorer Scorer = new(Model);

    private static ScanMatch Match(string ruleId, string severity, string category) => new()
    {
        PatternName = ruleId,
        RuleId = ruleId,
        Value = "x",
        Index = 0,
        Severity = severity,
        Category = category
    };

    private static ScanTelemetry Tel(long bytes = 1000, int proximity = 0, int numeric = 0) => new()
    {
        EffectiveLengthBytes = bytes,
        ProximityRejectCount = proximity,
        UnstructuredNumericHits = numeric
    };

    // ---- Nạp model ----

    [Fact]
    public void Load_FileThat_DuHeSo()
    {
        Assert.Equal("go-pha21-v1", Model.Version);
        Assert.Equal(5.0397, Model.BranchA.Gamma0ByDomain["finance_banking"], 3);
        Assert.Equal(2.5926, Model.BranchB.Beta0ByDomain["hr"], 3);
        Assert.Equal(1.1347, Model.BranchB.PlattA, 3);
        Assert.Equal(44, Model.RulePrecision.Count);
        Assert.Equal(0.961, Model.KeepThresholdByLevel["Confidential"], 3);
    }

    // ---- Chia nhánh + công thức (tính tay) ----

    [Fact]
    public void NhanhA_TheTinDung_DomainTaiChinh()
    {
        // decisive = credit_card_001 (precision 0,9231) → risk 0,0769 → z = 5,0397 − 9,7326 × 0,0769 = 4,291 → σ = 0,9865
        var r = Scorer.Compute(SensitivityLevel.Restricted, [Match("credit_card_001", "SECRET", "financial")], Tel());
        Assert.Equal(ConfidenceBranch.A, r.Branch);
        Assert.Equal("finance_banking", r.Domain);
        Assert.Equal(0.9865, r.Confidence, 3);
        Assert.True(r.KeepAtLayer1); // ngưỡng Restricted 0,001
    }

    [Fact]
    public void NhanhA_DaySoLa_KeoDiemXuong()
    {
        var clean = Scorer.Compute(SensitivityLevel.Confidential, [Match("vn_id_001", "CONFIDENTIAL", "pii")], Tel());
        var noisy = Scorer.Compute(SensitivityLevel.Confidential, [Match("vn_id_001", "CONFIDENTIAL", "pii")], Tel(numeric: 5));
        Assert.True(noisy.Confidence < clean.Confidence);
    }

    [Fact]
    public void FileSach_Ngan_LaShort()
    {
        var r = Scorer.Compute(SensitivityLevel.Public, [], Tel(bytes: 50));
        Assert.Equal(ConfidenceBranch.Short, r.Branch);
        Assert.Equal(0.9576, r.Confidence, 3);
    }

    [Fact]
    public void FileSach_KhongCounter_NhanhB_DomainFallbackHr()
    {
        // z = β0[hr] 2,5926 → σ(1,1347 × 2,5926 − 0,1372) = σ(2,8047) = 0,9429
        var r = Scorer.Compute(SensitivityLevel.Public, [], Tel());
        Assert.Equal(ConfidenceBranch.B, r.Branch);
        Assert.Equal("hr", r.Domain);
        Assert.Equal(0.9429, r.Confidence, 3);
        Assert.True(r.KeepAtLayer1); // ngưỡng Public 0,889
    }

    [Fact]
    public void NhanhB_ProximityReject_KeoDiemXuong_DuoiNguong()
    {
        var r = Scorer.Compute(SensitivityLevel.Public, [], Tel(proximity: 3));
        Assert.Equal(ConfidenceBranch.B, r.Branch);
        Assert.False(r.KeepAtLayer1);
    }

    [Fact]
    public void MucCaoHonMoiChoKhop_KhongCoDecisive_KhongVaoNhanhA()
    {
        // Compound nâng file lên Restricted nhưng không chỗ khớp nào ở mức Restricted → không có decisive (Pha 21: không vào A)
        var r = Scorer.Compute(SensitivityLevel.Restricted, [Match("vn_id_001", "CONFIDENTIAL", "pii")], Tel());
        Assert.Equal(ConfidenceBranch.B, r.Branch);
    }

    [Fact]
    public void Health001_FileRestricted_LaLuonBiMat_VaoNhanhA()
    {
        var r = Scorer.Compute(SensitivityLevel.Restricted, [Match("health_001", "CONFIDENTIAL", "pii")], Tel());
        Assert.Equal(ConfidenceBranch.A, r.Branch);
    }

    // ---- Đoán domain ----

    [Fact]
    public void GuessDomain_RuleHrCoChuHealth_VanLaHr()
    {
        Assert.Equal("hr", Scorer.GuessDomain([Match("hr_disciplinary_health_investigation_001", "CONFIDENTIAL", "hr")]));
    }

    [Fact]
    public void GuessDomain_NhieuPhieuNhat_Thang()
    {
        var matches = new[]
        {
            Match("health_001", "CONFIDENTIAL", "pii"),
            Match("health_001", "CONFIDENTIAL", "pii"),
            Match("bank_account_001", "CONFIDENTIAL", "financial")
        };
        Assert.Equal("healthcare", Scorer.GuessDomain(matches));
    }

    [Fact]
    public void GuessDomain_ChiRulePiiChung_DungFallback()
    {
        Assert.Equal("hr", Scorer.GuessDomain([Match("vn_id_001", "CONFIDENTIAL", "pii")]));
    }

    // ---- Counter near-miss của RegexScanner ----

    private static SensitivePattern SixDigitsWithKeyword(bool contextRequired) => new()
    {
        Name = "TestSix",
        RuleId = "test_six_001",
        Regex = new Regex(@"\b\d{6}\b", RegexOptions.None, TimeSpan.FromSeconds(1)),
        Severity = "CONFIDENTIAL",
        Confidence = 0.9,
        Weight = 1.0,
        ContextRequired = contextRequired,
        ContextWindow = 10,
        Keywords = ["mã"],
        PrimaryKeywords = ["mã"]
    };

    [Fact]
    public void Telemetry_DaySoLa_BoManhCuaSoDai()
    {
        var scanner = new RegexScanner([]);
        // 1 dãy 12 số đứng riêng (đếm) + 1 dãy 22 số (regex cắt 19+3 → mảnh 19 sát chữ số → bỏ)
        var tel = scanner.Scan("số 123456789012 và 1234567890123456789012 hết").Telemetry;
        Assert.Equal(1, tel.UnstructuredNumericHits);
    }

    [Fact]
    public void Telemetry_TuKhoaPrimaryCoMa_RuleKhongKhop_DuocDem()
    {
        var scanner = new RegexScanner([SixDigitsWithKeyword(contextRequired: false)]);
        Assert.Equal(1, scanner.Scan("có chữ mã nhưng không có số nào").Telemetry.KeywordHitNoMatchPrimaryRules);
        Assert.Equal(0, scanner.Scan("mã 123456").Telemetry.KeywordHitNoMatchPrimaryRules);
    }

    // [Thay đổi] Chẩn đoán nhánh B: proximity_reject theo đúng định nghĩa Go — chỉ rule exclude_if_no_keywords
    private static SensitivePattern Excluding(bool contextRequired, ProximityRule? ruleProximity) => new()
    {
        Name = "TestEx",
        RuleId = "test_ex_001",
        Regex = new Regex(@"\b\d{6}\b", RegexOptions.None, TimeSpan.FromSeconds(1)),
        Severity = "CONFIDENTIAL",
        Confidence = 0.9,
        ContextRequired = contextRequired,
        ContextWindow = 10,
        Keywords = ["mã"],
        PrimaryKeywords = ["mã"],
        ExcludeIfNoKeywords = true,
        Proximity = ruleProximity,
        RuleProximity = ruleProximity
    };

    [Fact]
    public void ProximityReject_RuleKhongKhaiExclude_KhongDem()
    {
        var scanner = new RegexScanner([SixDigitsWithKeyword(contextRequired: true)], minConfidence: 0);
        Assert.Equal(0, scanner.Scan("mã" + new string(' ', 50) + "123456").Telemetry.ProximityRejectCount);
    }

    [Fact]
    public void ProximityReject_KhongProximityWindow_ChiDemKhiCaFileKhongCoTuKhoa()
    {
        var scanner = new RegexScanner([Excluding(contextRequired: false, ruleProximity: null)], minConfidence: 0);
        Assert.Equal(1, scanner.Scan("số 123456").Telemetry.ProximityRejectCount);
        Assert.Equal(0, scanner.Scan("mã" + new string(' ', 50) + "123456").Telemetry.ProximityRejectCount);
    }

    [Fact]
    public void ProximityReject_ContextRequired_CaFileKhongCoTuKhoa_GoBoQuaKhongDem()
    {
        var scanner = new RegexScanner([Excluding(contextRequired: true, ruleProximity: null)], minConfidence: 0);
        Assert.Equal(0, scanner.Scan("số 123456").Telemetry.ProximityRejectCount);
    }

    [Fact]
    public void ProximityReject_CoProximityWindow_TuKhoaXa_Dem()
    {
        var proximity = new ProximityRule { MaxChars = 20, SameSentence = false, IncludeSecondary = false, AllowTableFallback = false };
        var scanner = new RegexScanner([Excluding(contextRequired: true, ruleProximity: proximity)], minConfidence: 0);
        Assert.Equal(1, scanner.Scan("mã" + new string(' ', 50) + "123456").Telemetry.ProximityRejectCount);
        Assert.Equal(0, scanner.Scan("mã 123456").Telemetry.ProximityRejectCount);
    }

    // [Thay đổi] Sửa "dãy số lạ" (A): số bị validator loại (rule đã nhận ra định dạng) không còn tính là số lạ
    [Fact]
    public void Telemetry_SoBiValidatorLoai_KhongTinhLaSoLa()
    {
        var tenDigits = new SensitivePattern
        {
            Name = "TestTen",
            RuleId = "test_ten_001",
            Regex = new Regex(@"\b\d{10}\b", RegexOptions.None, TimeSpan.FromSeconds(1)),
            Severity = "PUBLIC",
            Validator = _ => false // luôn trượt validator, VD MST sai checksum
        };
        Assert.Equal(0, new RegexScanner([tenDigits]).Scan("MST 0123456789").Telemetry.UnstructuredNumericHits);
        Assert.Equal(1, new RegexScanner([]).Scan("MST 0123456789").Telemetry.UnstructuredNumericHits);
    }

    // [Thay đổi] Đoán domain (B): không có chỗ khớp → đoán theo rule có từ khoá primary
    [Fact]
    public void GuessDomain_KhongChoKhop_DungTuKhoaRule()
    {
        var tel = new ScanTelemetry { PrimaryKeywordRules = [new KeywordRuleHit("healthcare_internal_business_001", "org")] };
        Assert.Equal("healthcare", Scorer.GuessDomain([], tel));
        // Có chỗ khớp thì chỗ khớp thắng, từ khoá không được dùng
        Assert.Equal("finance_banking", Scorer.GuessDomain([Match("bank_account_001", "CONFIDENTIAL", "financial")], tel));
    }

    [Fact]
    public void Telemetry_GhiRuleCoTuKhoaPrimary()
    {
        var tel = new RegexScanner([SixDigitsWithKeyword(contextRequired: false)]).Scan("mã 123456").Telemetry;
        Assert.Contains(tel.PrimaryKeywordRules, k => k.RuleKey == "test_six_001");
    }

    [Fact]
    public void Telemetry_DoDaiTinhTheoByteUtf8_VaTaiLieuMau()
    {
        var tel = new RegexScanner([]).Scan("ví dụ").Telemetry;
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("ví dụ"), tel.EffectiveLengthBytes);
        Assert.True(tel.DocContextDiscount);
    }
}
