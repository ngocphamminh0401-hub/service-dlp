// [Thay đổi] File mới (mở rộng định dạng - Bước 9): trích xuất nội dung file .eml (email) bằng EmlMimeParser tự viết.
namespace DlpService.Reading.Extractors;

/// <summary>Đọc file .eml: tiêu đề quan trọng (From/To/Cc/Bcc/Subject/Date) và phần thân dạng text (bỏ qua file
/// đính kèm nhị phân). Hỗ trợ multipart (chữ thường + HTML + đính kèm), base64/quoted-printable, tiêu đề có dấu.</summary>
public class EmlExtractor : IContentExtractor
{
    // Các header đáng quan tâm cho DLP — bỏ qua header kỹ thuật (Received, Message-ID, MIME-Version...) vì không
    // mang dữ liệu người dùng, chỉ làm rác kết quả quét.
    private static readonly string[] HeadersOfInterest = ["Subject", "From", "To", "Cc", "Bcc", "Date"];

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = [".eml"];

    /// <inheritdoc/>
    public long MaxFileSizeBytes => 25 * 1024 * 1024;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(string filePath)
    {
        byte[] rawBytes;
        using (var stream = ExtractorHelpers.OpenShared(filePath))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            rawBytes = memory.ToArray();
        }

        var message = EmlMimeParser.Parse(rawBytes);

        foreach (var name in HeadersOfInterest)
        {
            if (message.Headers.TryGetValue(name, out var value) && value.Trim().Length > 0)
            {
                yield return new ContentBlock(BlockKind.Heading, $"{name}: {value}", ["Header"]);
            }
        }

        foreach (var block in ExtractBody(message, ["Body"]))
        {
            yield return block;
        }
    }

    // Duyệt phần thân: multipart → đi vào từng phần con; phần lá không phải text/* (ảnh, pdf đính kèm...) → bỏ qua
    private static IEnumerable<ContentBlock> ExtractBody(EmlMimeParser.MimePart part, IReadOnlyList<string> contextPath)
    {
        if (part.Children.Count > 0)
        {
            foreach (var child in part.Children)
            {
                foreach (var block in ExtractBody(child, contextPath))
                {
                    yield return block;
                }
            }
            yield break;
        }

        if (!part.ContentType.StartsWith("text/", StringComparison.Ordinal) || part.BodyText == null)
        {
            yield break; // file đính kèm nhị phân (ảnh, pdf...) — không phải nội dung text để quét
        }

        bool isHtml = part.ContentType.Equals("text/html", StringComparison.Ordinal);
        string text = isHtml ? StripHtmlTags(part.BodyText) : part.BodyText;
        IReadOnlyList<string> partPath = isHtml ? [.. contextPath, "html"] : contextPath;

        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            yield return new ContentBlock(BlockKind.Paragraph, line, partPath);
        }
    }

    // Bỏ thẻ HTML thô để không quét nhầm tên thẻ/thuộc tính là dữ liệu; giữ lại phần chữ hiển thị
    private static string StripHtmlTags(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
}
