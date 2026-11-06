// [Thay đổi] File mới: ghép SensitivityLevel của hợp đồng gRPC (Dlp.Inference, mục 6.5 mở rộng) với SensitivityLevel
// đã dùng sẵn ở DlpService.Classification (baseline regex, tools/eval/DlpBaseline) — CÙNG Ý NGHĨA, CÙNG THỨ TỰ,
// khác namespace vì 1 bên sinh từ .proto, 1 bên viết tay cho Scanning layer. Không gộp làm 1 type — 2 tầng có vòng
// đời/mục đích khác nhau (proto dùng chung DlpFakeLlm/DlpLlmHost, enum kia chỉ dùng nội bộ Scanning).
using Dlp.Inference;
using ClsLevel = DlpService.Classification.SensitivityLevel;

namespace DlpLlmBenchmark;

internal static class LevelMapper
{
    public static ClsLevel ToClsLevel(SensitivityLevel level) => level switch
    {
        SensitivityLevel.Public => ClsLevel.Public,
        SensitivityLevel.Internal => ClsLevel.Internal,
        SensitivityLevel.Confidential => ClsLevel.Confidential,
        SensitivityLevel.Restricted => ClsLevel.Restricted,
        _ => ClsLevel.Public
    };
}
