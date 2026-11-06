// [Thay đổi] File mới: unit test cho toàn bộ bảng quyết định Merger (kiến trúc mục 7.1/7.2/7.3). Mỗi test ứng với
// đúng 1 dòng của bảng quyết định trong docs/kien-truc-trien-khai.md, đặt tên theo đúng câu chữ của dòng đó để đối
// chiếu ngược lại tài liệu cho dễ.
using DlpService.Classification;
using DlpService.Scanning;
using DlpService.Storage;
using Xunit;

namespace DlpService.Tests.Classification;

public class MergerTests
{
    // ===== MergeOne — từng dòng của bảng quyết định mục 7.2 =====

    [Fact]
    public void MergeOne_RegexVaLlmCungChoLaNhayCam_NangDiem()
    {
        var verdict = new RuleVerdict("r1", TruePositive: true, Confidence: 0.5);

        var (sFinal, needsReview) = Merger.MergeOne(s1Rule: 0.5, hardRule: false, LlmPolicy.VerifyIfAmbiguous, verdict);

        Assert.Equal(0.5 + Merger.WUp * 0.5, sFinal, precision: 10);
        Assert.False(needsReview);
    }

    [Fact]
    public void MergeOne_XacNhan_ClampKhongVuotQua1()
    {
        var verdict = new RuleVerdict("r1", TruePositive: true, Confidence: 1.0);

        var (sFinal, _) = Merger.MergeOne(s1Rule: 0.9, hardRule: false, LlmPolicy.VerifyIfAmbiguous, verdict);

        Assert.Equal(1.0, sFinal);
    }

    [Fact]
    public void MergeOne_RegexNghi_LlmBacBo_ConfCaoHonNguong_HaDiemTheoCongThuc()
    {
        var verdict = new RuleVerdict("r1", TruePositive: false, Confidence: 0.8);

        var (sFinal, needsReview) = Merger.MergeOne(s1Rule: 0.6, hardRule: false, LlmPolicy.VerifyIfAmbiguous, verdict);

        Assert.Equal(0.6 - Merger.WDown * 0.8, sFinal, precision: 10);
        Assert.False(needsReview);
    }

    [Fact]
    public void MergeOne_BacBo_ClampKhongXuongDuoi0()
    {
        var verdict = new RuleVerdict("r1", TruePositive: false, Confidence: 1.0);

        var (sFinal, _) = Merger.MergeOne(s1Rule: 0.1, hardRule: false, LlmPolicy.VerifyIfAmbiguous, verdict);

        Assert.Equal(0.0, sFinal);
    }

    [Fact]
    public void MergeOne_RegexNghi_LlmBacBo_ConfThapHonNguong_GiuDiem_NeedsReview()
    {
        var verdict = new RuleVerdict("r1", TruePositive: false, Confidence: 0.79);

        var (sFinal, needsReview) = Merger.MergeOne(s1Rule: 0.6, hardRule: false, LlmPolicy.VerifyIfAmbiguous, verdict);

        Assert.Equal(0.6, sFinal);
        Assert.True(needsReview);
    }

    [Fact]
    public void MergeOne_NguongConf08_LaBienDung_KhongPhaiThap()
    {
        // Đúng ranh giới "conf >= 0.8" (mục 7.1) — 0.8 phải đi theo nhánh hạ điểm, không phải nhánh needs_review
        var verdict = new RuleVerdict("r1", TruePositive: false, Confidence: Merger.ConfidenceThreshold);

        var (sFinal, needsReview) = Merger.MergeOne(s1Rule: 0.6, hardRule: false, LlmPolicy.VerifyIfAmbiguous, verdict);

        Assert.NotEqual(0.6, sFinal);
        Assert.False(needsReview);
    }

    [Fact]
    public void MergeOne_HardRule_LlmBacBo_ConfCao_VanKhongBiHaDiem()
    {
        // Luật an toàn mục 7.2: "Rule hard_rule=true (Luhn, checksum đúng) → LLM không được bác bỏ"
        var verdict = new RuleVerdict("r1", TruePositive: false, Confidence: 0.99);

        var (sFinal, needsReview) = Merger.MergeOne(s1Rule: 0.6, hardRule: true, LlmPolicy.VerifyIfAmbiguous, verdict);

        Assert.Equal(0.6, sFinal);
        Assert.False(needsReview);
    }

    [Fact]
    public void MergeOne_LlmPolicyNever_BoQuaVerdict_DuLaXacNhan()
    {
        // Luật an toàn mục 7.2: "Rule có llm_policy: never → Bỏ qua kết quả LLM cho rule đó"
        var verdict = new RuleVerdict("r1", TruePositive: true, Confidence: 1.0);

        var (sFinal, needsReview) = Merger.MergeOne(s1Rule: 0.6, hardRule: false, LlmPolicy.Never, verdict);

        Assert.Equal(0.6, sFinal);
        Assert.False(needsReview);
    }

    [Fact]
    public void MergeOne_KhongCoVerdict_GiuNguyenS1()
    {
        var (sFinal, needsReview) = Merger.MergeOne(s1Rule: 0.6, hardRule: false, LlmPolicy.VerifyIfAmbiguous, verdict: null);

        Assert.Equal(0.6, sFinal);
        Assert.False(needsReview);
    }

    // ===== MergeFile — gộp nhiều rule/chunk (mục 7.3) + trường hợp LLM phát hiện thêm (mục 7.2) =====

    [Fact]
    public void MergeFile_KhongCoVerdictNao_SourceRegex_SFinalBangS1File()
    {
        var findings = new[] { new FindingRecord("r1", 0.3, null, false, 1, "") };

        var result = Merger.MergeFile(s1File: 0.5, findings, verdicts: [], _ => LlmPolicy.VerifyIfAmbiguous);

        Assert.Equal(0.5, result.SFinal);
        Assert.False(result.NeedsReview);
        Assert.Equal("regex", result.Source);
    }

    [Fact]
    public void MergeFile_GopNhieuRule_LayDiemCaoNhat()
    {
        var findings = new[]
        {
            new FindingRecord("r1", 0.4, null, false, 1, ""),
            new FindingRecord("r2", 0.5, null, false, 1, ""),
        };
        var verdicts = new[] { new RuleVerdict("r2", TruePositive: true, Confidence: 1.0) };

        var result = Merger.MergeFile(s1File: 0.5, findings, verdicts, _ => LlmPolicy.VerifyIfAmbiguous);

        double expectedR2 = 0.5 + Merger.WUp * 1.0;
        Assert.Equal(expectedR2, result.SFinal, precision: 10);
        Assert.Equal("llm", result.Source);
    }

    [Fact]
    public void MergeFile_NhieuChunkDongThuanXacNhanCungRule_GopThanhMotVerdict_LayConfCaoNhat()
    {
        var findings = new[] { new FindingRecord("r1", 0.5, null, false, 1, "") };
        var verdicts = new[]
        {
            new RuleVerdict("r1", TruePositive: true, Confidence: 0.3),
            new RuleVerdict("r1", TruePositive: true, Confidence: 0.9), // tín hiệu mạnh nhất, phải được dùng
        };

        var result = Merger.MergeFile(s1File: 0.5, findings, verdicts, _ => LlmPolicy.VerifyIfAmbiguous);

        Assert.Equal(0.5 + Merger.WUp * 0.9, result.SFinal, precision: 10);
        Assert.False(result.NeedsReview);
    }

    [Fact]
    public void MergeFile_CacChunkMauThuan_KhongChac_GiuNguyenDiem_NeedsReview()
    {
        // Mục 7.3: "Verdict theo rule: nếu các chunk mâu thuẫn thì coi là không chắc và không hạ điểm"
        var findings = new[] { new FindingRecord("r1", 0.5, null, false, 1, "") };
        var verdicts = new[]
        {
            new RuleVerdict("r1", TruePositive: true, Confidence: 0.9),
            new RuleVerdict("r1", TruePositive: false, Confidence: 0.9), // mâu thuẫn với verdict trên
        };

        var result = Merger.MergeFile(s1File: 0.5, findings, verdicts, _ => LlmPolicy.VerifyIfAmbiguous);

        Assert.Equal(0.5, result.SFinal);
        Assert.True(result.NeedsReview);
    }

    [Fact]
    public void MergeFile_MauThuan_KhongDuocHaDiem_DuTatCaConfDeuCaoHonNguong()
    {
        // Nhấn mạnh riêng nửa câu "không hạ điểm" của mục 7.3 — kể cả khi mọi verdict đều đủ conf để hạ nếu xét riêng lẻ
        var findings = new[] { new FindingRecord("r1", 0.7, null, false, 1, "") };
        var verdicts = new[]
        {
            new RuleVerdict("r1", TruePositive: false, Confidence: 0.95),
            new RuleVerdict("r1", TruePositive: true, Confidence: 0.95),
        };

        var result = Merger.MergeFile(s1File: 0.7, findings, verdicts, _ => LlmPolicy.VerifyIfAmbiguous);

        Assert.Equal(0.7, result.SFinal);
    }

    [Fact]
    public void MergeFile_RegexBoSot_LlmPhatHienThem_NeedsReview_SourceLlm()
    {
        // Mục 7.2: "Regex bỏ sót, LLM phát hiện → source=llm, needs_review=1; không tự áp policy nặng"
        var findings = Array.Empty<FindingRecord>();
        var verdicts = new[] { new RuleVerdict("", TruePositive: true, Confidence: 0.9) }; // rule_id rỗng = LLM tự phát hiện

        var result = Merger.MergeFile(s1File: 0.0, findings, verdicts, _ => LlmPolicy.VerifyIfAmbiguous);

        Assert.True(result.NeedsReview);
        Assert.Equal("llm", result.Source);
        // "không tự áp policy nặng" — không có finding nào để nâng điểm, S_final vẫn ở sàn s1File
        Assert.Equal(0.0, result.SFinal);
    }

    [Fact]
    public void MergeFile_LlmTuPhatHienNhungLaFalsePositive_KhongAnhHuongGiCa()
    {
        var findings = Array.Empty<FindingRecord>();
        var verdicts = new[] { new RuleVerdict("", TruePositive: false, Confidence: 0.9) };

        var result = Merger.MergeFile(s1File: 0.2, findings, verdicts, _ => LlmPolicy.VerifyIfAmbiguous);

        Assert.False(result.NeedsReview);
        Assert.Equal("regex", result.Source);
        Assert.Equal(0.2, result.SFinal);
    }

    [Fact]
    public void MergeFile_LlmPolicyNeverChiApDungRieng1Rule_RuleKhacVanMergeBinhThuong()
    {
        var findings = new[]
        {
            new FindingRecord("never_rule", 0.5, null, false, 1, ""),
            new FindingRecord("normal_rule", 0.5, null, false, 1, ""),
        };
        var verdicts = new[]
        {
            new RuleVerdict("never_rule", TruePositive: false, Confidence: 0.99), // đáng lẽ hạ điểm mạnh nếu không bị chặn
            new RuleVerdict("normal_rule", TruePositive: true, Confidence: 0.5),
        };

        var result = Merger.MergeFile(s1File: 0.5, findings, verdicts,
            ruleId => ruleId == "never_rule" ? LlmPolicy.Never : LlmPolicy.VerifyIfAmbiguous);

        // never_rule giữ nguyên 0.5, normal_rule được nâng lên 0.5 + 0.3*0.5 = 0.65 → max = 0.65
        Assert.Equal(0.5 + Merger.WUp * 0.5, result.SFinal, precision: 10);
        Assert.Equal("llm", result.Source);
    }

    [Fact]
    public void MergeFile_HardRule_LanTrongNhieuRule_KhongBiRuleKhacKeoHaDiem()
    {
        var findings = new[]
        {
            new FindingRecord("hard", 0.6, true, true, 1, ""),
            new FindingRecord("soft", 0.6, null, false, 1, ""),
        };
        var verdicts = new[]
        {
            new RuleVerdict("hard", TruePositive: false, Confidence: 0.99),
            new RuleVerdict("soft", TruePositive: false, Confidence: 0.99),
        };

        var result = Merger.MergeFile(s1File: 0.6, findings, verdicts, _ => LlmPolicy.VerifyIfAmbiguous);

        // hard giữ nguyên 0.6 (luật an toàn); soft bị hạ theo công thức → max vẫn là 0.6 (từ hard, không phải trùng hợp)
        Assert.Equal(0.6, result.SFinal);
    }
}
