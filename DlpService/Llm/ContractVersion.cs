// [Thay đổi] File mới (mục 5.3 - chốt hợp đồng gRPC): phiên bản hợp đồng hiện tại, khớp contract_ver trong
// contracts/inference.proto. Chỉ tăng khi thay đổi PHÁ VỠ tương thích (xoá/đổi số thứ tự field, xoá/đổi giá trị
// enum Label/EvidenceCode đã có) — thêm field tuỳ chọn hoặc thêm nhãn mới thì KHÔNG tăng.
namespace DlpService.Llm;

/// <summary>Phiên bản hợp đồng gRPC giữa Regex service và LLM process.</summary>
public static class ContractVersion
{
    /// <summary>Phiên bản hiện tại. Lệch phiên bản khi gọi GetInfo → chuyển sang regex-only, không gửi request sai schema.</summary>
    public const uint Current = 1;
}
