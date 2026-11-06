// [Thay đổi] File mới (kế hoạch tách lõi, bước 2): công tắc Llm:Enabled=false — không file nào được xếp nhóm gửi LLM.
using DlpService.Classification;

namespace DlpService.Tests.Classification;

public class EscalationDisabledTests
{
    private const string Hash = "00000000ffffffffffffffffffffffffffffffffffffffffffffffffffffffff"; // 8 ký tự hex đầu = 0 → luôn trúng mẫu P3 nếu bật

    [Theory]
    [InlineData(RouteZone.Grey)]    // sẽ là P1 nếu LLM bật
    [InlineData(RouteZone.NoMatch)] // sẽ là P2/P3 nếu LLM bật
    [InlineData(RouteZone.Certain)]
    public void LLM_tat_thi_khong_file_nao_duoc_gui(RouteZone zone)
    {
        var options = new EscalationOptions { Enabled = false, SampleRatePct = 100, SuspiciousKeywords = ["luong"] };

        var decision = EscalationPolicy.Decide(zone, textLength: 5000, filePath: "/a/luong.docx", relativePath: "luong.docx", Hash, options);

        Assert.Null(decision.Group);
    }

    [Fact]
    public void LLM_bat_mac_dinh_vung_xam_van_la_P1()
    {
        // Bảo đảm mặc định (Enabled = true) không đổi hành vi của bản Windows hiện tại
        var decision = EscalationPolicy.Decide(RouteZone.Grey, 5000, "/a/b.docx", "b.docx", Hash, new EscalationOptions());

        Assert.Equal(EscalationGroup.P1, decision.Group);
    }
}
