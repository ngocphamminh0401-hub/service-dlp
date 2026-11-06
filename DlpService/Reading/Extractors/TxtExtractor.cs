// [Thay đổi] File mới (mở rộng định dạng - Bước 1): trích xuất file .txt — đọc trực tiếp
// [Thay đổi] Streaming + giữ cấu trúc (Bước 3): trả dần từng dòng thành 1 Paragraph block (yield return) thay vì
// gộp cả file thành 1 chuỗi — FileReaderService sẽ tự ghép lại và có thể dừng sớm nếu file quá dài.
namespace DlpService.Reading.Extractors;

/// <summary>Đọc file .txt trực tiếp, trả từng dòng là 1 đoạn văn bản.</summary>
public class TxtExtractor : IContentExtractor
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

    /// <inheritdoc/>
    public long MaxFileSizeBytes => 10 * 1024 * 1024; // giữ nguyên giới hạn 10MB như trước

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(string filePath)
    {
        // Txt không có khái niệm sheet/heading → mỗi dòng chỉ là 1 đoạn văn bản (Paragraph), không có ContextPath
        string text = TextFileReader.ReadAll(filePath);
        // Tách theo "\r\n" hoặc "\n" (không tách theo từng ký tự) để không sinh dòng rỗng thừa do "\r\n" bị chẻ đôi
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            yield return new ContentBlock(BlockKind.Paragraph, line, ContextPath: []);
        }
    }
}
