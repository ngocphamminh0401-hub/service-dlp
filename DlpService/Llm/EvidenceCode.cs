// [Thay đổi] File mới (mục 6.5 - chốt tập đóng evidence): tương ứng 1-1 với enum EvidenceCode trong
// contracts/inference.proto (GIỮ NGUYÊN tên và giá trị số). Chỉ dùng khi LLM phát hiện thêm (không có rule_id
// backing) và cần giải thích "vì sao nghĩ đây là nhạy cảm" — khớp đúng các loại chunk ChunkSelector đã tạo sẵn
// (Llm/ChunkSelector.cs).
namespace DlpService.Llm;

/// <summary>Tập đóng lý do LLM tự phát hiện thêm 1 dữ liệu nhạy cảm không có rule nào khớp — khớp enum EvidenceCode
/// trong contracts/inference.proto.</summary>
public enum EvidenceCode
{
    /// <summary>Giá trị mặc định của proto3 — KHÔNG dùng làm evidence thật, chỉ báo hiệu thiếu/lỗi.</summary>
    UnknownEvidence = 0,

    /// <summary>Tên cột gợi ý (chunk "metadata", TableHeader — xem ChunkSelector.BuildMetadataText).</summary>
    ColumnName = 1,

    /// <summary>Từ khoá xung quanh (chunk "context" — xem ChunkSelector.BuildContextChunks).</summary>
    KeywordContext = 2,

    /// <summary>Hình dạng giá trị giống dữ liệu nhạy cảm (VD dãy số giống CCCD) dù rule chính không khớp.</summary>
    FormatShape = 3,

    /// <summary>Tiêu đề tài liệu (chunk "metadata", Heading/SectionMarker).</summary>
    DocumentTitle = 4,

    /// <summary>Tên file/đường dẫn đáng nghi (chunk "metadata").</summary>
    FilePath = 5
}
