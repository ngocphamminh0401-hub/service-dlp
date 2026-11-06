// [Thay đổi] File mới (Merger mục 7.4 "Lưu vết"): phiên bản công thức merge hiện tại, ghi kèm mỗi kết quả để sau này
// so sánh được khi công thức/bảng quyết định trong Merger.cs thay đổi (cùng kiểu với DlpService.Llm.ContractVersion).
namespace DlpService.Classification;

/// <summary>Phiên bản công thức merge (Merger.cs — mục 7.1/7.2/7.3).</summary>
public static class MergePolicyVersion
{
    /// <summary>Tăng khi đổi hằng số (W_UP/W_DOWN/ConfidenceThreshold) hoặc đổi LOGIC bảng quyết định/gộp chunk.
    /// KHÔNG tăng khi chỉ sửa lỗi chính tả/comment.</summary>
    public const string Current = "1.0";
}
