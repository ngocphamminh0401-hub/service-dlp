// [Thay đổi] File mới (FileConfidence 2 nhánh — port công thức Pha 21 của engine Go, D:\dlp-classifier\tailieu\
// pha21-danh-gia-lop1-regex.md): ước lượng "nhãn regex của file này có đủ tin để dùng ngay không". Giai đoạn SHADOW:
// chỉ tính + log + ghi DB, KHÔNG thay Router (S1 vẫn quyết định vùng xám) và KHÔNG đổi mức của file.
using System.Text.Json;
using System.Text.Json.Serialization;
using DlpService.Scanning;

namespace DlpService.Classification;

/// <summary>Nhánh tính điểm: A = có chỗ khớp quyết định mức (sợ chấm THẤP hơn thật), B = không có (sợ nhầm là Public),
/// Short = nội dung quá ngắn để đánh giá (gán hằng số).</summary>
public enum ConfidenceBranch
{
    A,
    B,
    Short
}

/// <summary>Kết quả FileConfidence của 1 file.</summary>
/// <param name="Branch">Nhánh đã dùng.</param>
/// <param name="Confidence">Điểm 0-1.</param>
/// <param name="Domain">Domain dùng cho intercept (đoán từ rule, hoặc truyền vào).</param>
/// <param name="KeepThreshold">Ngưỡng giữ Lớp 1 của mức file (P19).</param>
public sealed record FileConfidenceResult(ConfidenceBranch Branch, double Confidence, string Domain, double KeepThreshold)
{
    /// <summary>true nếu đủ tin để giữ nhãn regex (conf ≥ ngưỡng của mức); false = nên gửi LLM.</summary>
    public bool KeepAtLayer1 => Confidence >= KeepThreshold;
}

/// <summary>Hệ số + bảng tra của công thức, đọc từ confidence/file-confidence-v1.json (snake_case).</summary>
public sealed class FileConfidenceModel
{
    public string Version { get; set; } = "";
    public int MinLengthBytes { get; set; } = 200;
    public double ShortConfidence { get; set; }
    public string FallbackDomain { get; set; } = "hr";
    public BranchAModel BranchA { get; set; } = new();
    public BranchBModel BranchB { get; set; } = new();
    public double PrecisionPriorDefault { get; set; } = 0.9;
    public double PrecisionPriorFpProne { get; set; } = 0.7;
    public List<string> FpProneRules { get; set; } = [];
    public Dictionary<string, double> RulePrecision { get; set; } = [];
    public List<string> AlwaysSecretRules { get; set; } = [];
    public Dictionary<string, double> KeepThresholdByLevel { get; set; } = [];

    public sealed class BranchAModel
    {
        public Dictionary<string, double> Gamma0ByDomain { get; set; } = [];
        public double RiskCoef { get; set; }
        public double UnstructuredNumericCoef { get; set; }
        public double UnstructuredNumericK { get; set; } = 1;
        public double EmptyDecisiveRisk { get; set; } = 0.5;
    }

    public sealed class BranchBModel
    {
        public Dictionary<string, double> Beta0ByDomain { get; set; } = [];
        public CoefK KeywordHitNoMatchPrimary { get; set; } = new();
        public CoefK ProximityReject { get; set; } = new();
        public CoefK LevelGateDowngrade { get; set; } = new();
        public CoefK UnstructuredNumericDensity { get; set; } = new();
        public CoefK IdentifierVolumeNearGate { get; set; } = new();
        public double DocContextDiscountCoef { get; set; }
        public double PlattA { get; set; } = 1;
        public double PlattB { get; set; }
    }

    /// <summary>1 hệ số áp lên φ(x; k).</summary>
    public sealed class CoefK
    {
        public double Coef { get; set; }
        public double K { get; set; } = 1;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Đọc model từ file JSON.</summary>
    /// <exception cref="IOException">Không đọc được file.</exception>
    /// <exception cref="JsonException">JSON sai cú pháp.</exception>
    /// <exception cref="InvalidDataException">Thiếu intercept domain hoặc ngưỡng mức — model không dùng được.</exception>
    public static FileConfidenceModel Load(string path)
    {
        var model = JsonSerializer.Deserialize<FileConfidenceModel>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"File model rỗng: {path}");

        // Thiếu mấy bảng này thì công thức âm thầm ra số vô nghĩa → chặn ngay lúc nạp
        if (model.BranchA.Gamma0ByDomain.Count == 0 || model.BranchB.Beta0ByDomain.Count == 0)
        {
            throw new InvalidDataException($"Model thiếu intercept theo domain: {path}");
        }
        foreach (var level in Enum.GetNames<SensitivityLevel>())
        {
            if (!model.KeepThresholdByLevel.ContainsKey(level))
            {
                throw new InvalidDataException($"Model thiếu keep_threshold_by_level.{level}: {path}");
            }
        }
        return model;
    }
}

/// <summary>Tính FileConfidence 2 nhánh cho 1 file đã quét + phân loại.</summary>
/// <remarks>
/// Chia nhánh (Pha 21): A nếu (mức file &gt; Public VÀ có chỗ khớp đúng bằng mức file) HOẶC có rule "luôn bí mật";
///   không thì Short nếu nội dung &lt; 200 byte UTF-8; còn lại B.
/// Nhánh A: z = γ0[domain] + risk_coef·risk + numeric_coef·φ(unstructured_numeric_hits); conf = σ(z)
///   risk = 1 − min(độ chính xác lịch sử của các rule quyết định); không có rule quyết định → 0,5.
/// Nhánh B: z = β0[domain] + Σ coef·φ(counter) + doc_coef·[tài liệu mẫu]; conf = σ(platt_a·z + platt_b).
/// φ(x; k) = 1 − e^(−x/k). Mọi hệ số phạt đều ÂM → điểm chỉ bị KÉO XUỐNG khỏi mức nền của domain.
/// </remarks>
public sealed class FileConfidenceScorer(FileConfidenceModel model)
{
    private readonly HashSet<string> _alwaysSecret = new(model.AlwaysSecretRules, StringComparer.Ordinal);
    private readonly HashSet<string> _fpProne = new(model.FpProneRules, StringComparer.Ordinal);

    /// <summary>Phiên bản hệ số (ghi log/DB để biết điểm tính bằng bộ nào).</summary>
    public string Version => model.Version;

    /// <summary>Tính điểm.</summary>
    /// <param name="fileLevel">Mức engine đã gán cho file.</param>
    /// <param name="matches">Chỗ khớp cuối cùng của RegexScanner.</param>
    /// <param name="telemetry">Counter near-miss của cùng lần quét.</param>
    /// <param name="domain">Domain đã biết (VD nhãn trong DlpBaseline); null = tự đoán từ rule đã khớp.</param>
    public FileConfidenceResult Compute(SensitivityLevel fileLevel, IReadOnlyList<ScanMatch> matches, ScanTelemetry telemetry,
        string? domain = null)
    {
        domain ??= GuessDomain(matches, telemetry); // [Thay đổi] Đoán domain: + từ khoá rule
        double keepThreshold = model.KeepThresholdByLevel[fileLevel.ToString()];

        // Rule quyết định = rule có chỗ khớp ĐÚNG BẰNG mức cuối của file (Go: m.Level == FinalLevel)
        var decisive = fileLevel > SensitivityLevel.Public
            ? matches.Where(m => m.Severity.ParseSensitivityLevel() == fileLevel).Select(RuleKey).Distinct().ToList()
            : [];

        // Go: 5 rule "luôn bí mật", cộng health_001 khi file đã bị chấm Restricted
        bool alwaysSecret = matches.Any(m => _alwaysSecret.Contains(RuleKey(m)))
            || (fileLevel == SensitivityLevel.Restricted && matches.Any(m => RuleKey(m) == "health_001"));

        if (decisive.Count > 0 || alwaysSecret)
        {
            return new FileConfidenceResult(ConfidenceBranch.A, BranchA(decisive, telemetry, domain), domain, keepThreshold);
        }
        if (telemetry.EffectiveLengthBytes < model.MinLengthBytes)
        {
            return new FileConfidenceResult(ConfidenceBranch.Short, model.ShortConfidence, domain, keepThreshold);
        }
        return new FileConfidenceResult(ConfidenceBranch.B, BranchB(telemetry, domain), domain, keepThreshold);
    }

    private double BranchA(List<string> decisive, ScanTelemetry t, string domain)
    {
        var a = model.BranchA;
        double risk = decisive.Count == 0 ? a.EmptyDecisiveRisk : 1 - decisive.Min(Precision);
        double z = Intercept(a.Gamma0ByDomain, domain)
            + a.RiskCoef * risk
            + a.UnstructuredNumericCoef * Phi(t.UnstructuredNumericHits, a.UnstructuredNumericK);
        return Sigmoid(z);
    }

    private double BranchB(ScanTelemetry t, string domain)
    {
        var b = model.BranchB;
        double z = Intercept(b.Beta0ByDomain, domain)
            + b.KeywordHitNoMatchPrimary.Coef * Phi(t.KeywordHitNoMatchPrimaryRules, b.KeywordHitNoMatchPrimary.K)
            + b.ProximityReject.Coef * Phi(t.ProximityRejectCount, b.ProximityReject.K)
            + b.LevelGateDowngrade.Coef * Phi(t.LevelGateDowngradeCount, b.LevelGateDowngrade.K)
            + b.UnstructuredNumericDensity.Coef * Phi(t.UnstructuredNumericDensity, b.UnstructuredNumericDensity.K)
            + b.IdentifierVolumeNearGate.Coef * Phi(t.IdentifierVolumeNearGate, b.IdentifierVolumeNearGate.K)
            + (t.DocContextDiscount ? b.DocContextDiscountCoef : 0);
        // Platt: σ(a·logit(σ(z)) + b) = σ(a·z + b)
        return Sigmoid(b.PlattA * z + b.PlattB);
    }

    // Độ chính xác lịch sử của rule; rule chưa có trong bảng → prior (0,70 cho rule hay báo nhầm, 0,90 còn lại)
    private double Precision(string ruleKey) =>
        model.RulePrecision.TryGetValue(ruleKey, out double p) ? p
        : _fpProne.Contains(ruleKey) ? model.PrecisionPriorFpProne : model.PrecisionPriorDefault;

    private double Intercept(Dictionary<string, double> byDomain, string domain) =>
        byDomain.TryGetValue(domain, out double v) ? v : byDomain[model.FallbackDomain];

    /// <summary>Đoán domain (service thật không biết domain của file), 2 tầng:
    /// (1) mỗi chỗ khớp bỏ 1 phiếu theo rule của nó; (2) [Thay đổi] chỉ khi (1) không có phiếu nào: mỗi rule có từ khoá
    /// primary trong file bỏ 1 phiếu. Nhiều phiếu nhất thắng, hoà thì ưu tiên domain có intercept thấp hơn (thận trọng hơn);
    /// không phiếu nào → fallback.</summary>
    /// <param name="matches">Chỗ khớp cuối cùng.</param>
    /// <param name="telemetry">[Thay đổi] Counter của cùng lần quét (lấy rule có từ khoá); null = chỉ dùng chỗ khớp.</param>
    public string GuessDomain(IReadOnlyList<ScanMatch> matches, ScanTelemetry? telemetry = null)
    {
        var votes = Vote(matches.Select(m => (RuleKey(m), m.Category)));
        if (votes.Count == 0 && telemetry != null)
        {
            // Tầng 2 chỉ dùng khi tầng 1 trống: chỗ khớp là bằng chứng mạnh hơn từ khoá, không để từ khoá lấn át
            votes = Vote(telemetry.PrimaryKeywordRules.Select(k => (k.RuleKey, k.Category)));
        }
        if (votes.Count == 0)
        {
            return model.FallbackDomain;
        }
        return votes
            .OrderByDescending(v => v.Value)
            .ThenBy(v => Intercept(model.BranchA.Gamma0ByDomain, v.Key))
            .First().Key;
    }

    private static Dictionary<string, int> Vote(IEnumerable<(string RuleKey, string? Category)> rules)
    {
        var votes = new Dictionary<string, int>();
        foreach (var (key, category) in rules)
        {
            string? d = DomainOf(key, category);
            if (d != null)
            {
                votes[d] = votes.GetValueOrDefault(d) + 1;
            }
        }
        return votes;
    }

    // hr kiểm tra TRƯỚC "health" vì có rule hr_*_health_* (VD hr_disciplinary_health_investigation_001) thuộc HR.
    // Rule pii/org chung (CCCD, tên, email, hợp đồng...) không bỏ phiếu — xuất hiện ở mọi domain.
    // [Thay đổi] Đoán domain: nhận (khoá rule, category) thay cho ScanMatch để dùng chung cho cả chỗ khớp lẫn từ khoá
    private static string? DomainOf(string key, string? category)
    {
        if (string.Equals(category, "hr", StringComparison.OrdinalIgnoreCase)) return "hr";
        if (key.Contains("insurance_context", StringComparison.Ordinal)) return "insurance";
        if (key.Contains("health", StringComparison.Ordinal)) return "healthcare";
        if (string.Equals(category, "financial", StringComparison.OrdinalIgnoreCase)) return "finance_banking";
        return null;
    }

    private static string RuleKey(ScanMatch m) => m.RuleId ?? m.PatternName;

    /// <summary>φ(x; k) = 1 − e^(−x/k), bão hoà về 1 khi x lớn; k ≤ 0 coi như không dùng (0).</summary>
    public static double Phi(double x, double k) => k <= 0 ? 0 : 1 - Math.Exp(-x / k);

    /// <summary>σ(z) = 1 / (1 + e^(−z)).</summary>
    public static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));
}
