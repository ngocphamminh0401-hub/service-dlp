// [Thay đổi] File mới (mở rộng định dạng - Bước 9): trích xuất nội dung file .eml (email) bằng EmlMimeParser tự viết.
// [Thay đổi] M6:
//  - Đọc file 1 lần vào mảng đúng kích thước (bỏ MemoryStream + ToArray = 2 bản sao).
//  - Giải mã LƯỜI: phần thân chỉ được giải mã khi được duyệt tới; FileReaderService dừng ở giới hạn 2 triệu ký tự / timeout thì các phần
//    sau không bao giờ bị giải mã (trước đây Parse giải mã MỌI phần trước khi trả khối đầu tiên).
//  - QUÉT ATTACHMENT trong bộ nhớ (trước đây attachment không phải text/* bị bỏ im lặng → email kèm file nhạy cảm ghi Public/final):
//    giải mã vào MemoryStream rồi đưa cho extractor của định dạng đó (qua IUsesExtractorLookup) — KHÔNG ghi file tạm (dữ liệu nhạy cảm).
//    Không đọc được (ảnh đính kèm, định dạng không hỗ trợ, file hỏng/có mật khẩu, vượt giới hạn) → khối Unreadable → file "incomplete".
using DlpService.Reading;

namespace DlpService.Reading.Extractors;

/// <summary>Đọc file .eml: tiêu đề quan trọng (From/To/Cc/Bcc/Subject/Date), phần thân dạng text, và attachment (docx/xlsx/pptx/pdf/csv/txt,
/// email chuyển tiếp) qua extractor tương ứng. Hỗ trợ multipart, base64/quoted-printable, tiêu đề có dấu.</summary>
public class EmlExtractor : IContentExtractor, IUsesExtractorLookup
{
    // Các header đáng quan tâm cho DLP — bỏ qua header kỹ thuật (Received, Message-ID, MIME-Version...) vì không
    // mang dữ liệu người dùng, chỉ làm rác kết quả quét.
    private static readonly string[] HeadersOfInterest = ["Subject", "From", "To", "Cc", "Bcc", "Date"];

    // [Thay đổi] M6: email chuyển tiếp lồng trong email... tối đa 3 lớp attachment
    internal const int MaxAttachmentDepth = 3;

    // [Thay đổi] M6: tổng byte attachment được giải mã cho 1 email (= MaxFileSizeBytes của .eml — email hợp lệ không thể chứa nhiều hơn)
    internal const long MaxAttachmentBytesPerEmail = 25L * 1024 * 1024;

    private const string AttachmentPrefix = "Đính kèm: ";

    // [Thay đổi] M6: tra extractor theo đuôi (do FileReaderService cấp sau khi nạp); null = chưa cấp (VD test gọi thẳng) → attachment = Unreadable
    private Func<string, IContentExtractor?>? _lookup;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = [".eml"];

    /// <inheritdoc/>
    public long MaxFileSizeBytes => 25 * 1024 * 1024;

    /// <inheritdoc/>
    public bool SupportsStream => true;

    /// <inheritdoc/>
    void IUsesExtractorLookup.SetLookup(Func<string, IContentExtractor?> byExtension) => _lookup = byExtension;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(string filePath)
    {
        // [Thay đổi] M6: đọc 1 lần vào mảng ĐÚNG kích thước (bản cũ: CopyTo(MemoryStream) + ToArray() = buffer tới 32 MB + mảng 25 MB)
        byte[] rawBytes;
        using (var stream = ExtractorHelpers.OpenShared(filePath))
        {
            rawBytes = new byte[stream.Length];
            stream.ReadExactly(rawBytes);
        }
        return ExtractMessage(rawBytes, [], attachmentDepth: 0, new Budget());
    }

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(Stream stream, string fileName)
    {
        stream.Position = 0;
        byte[] rawBytes = new byte[stream.Length];
        stream.ReadExactly(rawBytes);
        return ExtractMessage(rawBytes, [], attachmentDepth: 0, new Budget());
    }

    // Ngân sách dùng chung cho cả cây email (kể cả email chuyển tiếp lồng bên trong)
    private sealed class Budget
    {
        public long RemainingAttachmentBytes = MaxAttachmentBytesPerEmail;
    }

    private IEnumerable<ContentBlock> ExtractMessage(byte[] rawBytes, IReadOnlyList<string> prefix, int attachmentDepth, Budget budget)
    {
        var message = EmlMimeParser.Parse(rawBytes);

        foreach (var name in HeadersOfInterest)
        {
            if (message.Headers.TryGetValue(name, out var value) && value.Trim().Length > 0)
            {
                yield return new ContentBlock(BlockKind.Heading, $"{name}: {value}", [.. prefix, "Header"]);
            }
        }

        foreach (var block in ExtractBody(message, [.. prefix, "Body"], prefix, attachmentDepth, budget))
        {
            yield return block;
        }
    }

    // Duyệt phần thân: multipart → đi vào từng phần con; phần lá text/* → giải mã (lười) như trước; phần lá khác → attachment (M6)
    private IEnumerable<ContentBlock> ExtractBody(EmlMimeParser.MimePart part, IReadOnlyList<string> contextPath, IReadOnlyList<string> prefix,
        int attachmentDepth, Budget budget)
    {
        if (part.Children.Count > 0)
        {
            // [Thay đổi] Điểm D: preamble (trước "--boundary" đầu) — trình đọc email không hiển thị nhưng vẫn nằm trong file .eml → quét
            foreach (var block in PlainLines(EmlMimeParser.DecodePlain(part.Source, part.Preamble), [.. contextPath, "preamble"]))
            {
                yield return block;
            }
            foreach (var child in part.Children)
            {
                foreach (var block in ExtractBody(child, contextPath, prefix, attachmentDepth, budget))
                {
                    yield return block;
                }
            }
            // [Thay đổi] Điểm D: epilogue (sau "--boundary--")
            foreach (var block in PlainLines(EmlMimeParser.DecodePlain(part.Source, part.Epilogue), [.. contextPath, "epilogue"]))
            {
                yield return block;
            }
            yield break;
        }

        // [Thay đổi] Điểm B + C: multipart nhưng KHÔNG tách được phần nào (thân không có dòng "--boundary", hoặc thiếu tham số boundary) → quét cả
        // thân như text/plain. Trước đây (và bản M6 đầu) thân bị bỏ / thành "attachment không tên" → chỗ giấu dữ liệu chỉ bằng khai boundary sai.
        if (part.ContentType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            foreach (var block in PlainLines(EmlMimeParser.DecodeText(part), contextPath))
            {
                yield return block;
            }
            yield break;
        }

        if (!part.ContentType.StartsWith("text/", StringComparison.Ordinal))
        {
            // [Thay đổi] M6: trước đây "yield break" (bỏ im lặng mọi phần không phải text, kể cả .docx/.xlsx/.pdf đính kèm)
            foreach (var block in ExtractAttachment(part, prefix, attachmentDepth, budget))
            {
                yield return block;
            }
            yield break;
        }

        // [Thay đổi] M6: giải mã TẠI ĐÂY (lười), không còn giải mã sẵn trong Parse
        string bodyText = EmlMimeParser.DecodeText(part);
        bool isHtml = part.ContentType.Equals("text/html", StringComparison.Ordinal);
        string text = isHtml ? StripHtmlTags(bodyText) : bodyText;
        IReadOnlyList<string> partPath = isHtml ? [.. contextPath, "html"] : contextPath;

        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            yield return new ContentBlock(BlockKind.Paragraph, line, partPath);
        }
    }

    // [Thay đổi] M6: 1 phần đính kèm không phải text
    private IEnumerable<ContentBlock> ExtractAttachment(EmlMimeParser.MimePart part, IReadOnlyList<string> prefix, int attachmentDepth, Budget budget)
    {
        // Ảnh inline (logo chữ ký, có Content-ID hoặc Content-Disposition: inline) → bỏ qua. Nếu tính là "không đọc được" thì gần như mọi email
        // công ty đều thành incomplete. Ảnh ĐÍNH KÈM thật (attachment) vẫn được ghi Unreadable bên dưới (có thể là ảnh chụp CCCD — cần OCR).
        string disposition = EmlMimeParser.GetHeader(part.Headers, "Content-Disposition") ?? "";
        bool inline = EmlMimeParser.GetHeader(part.Headers, "Content-ID") != null || disposition.TrimStart().StartsWith("inline", StringComparison.OrdinalIgnoreCase);
        if (part.ContentType.StartsWith("image/", StringComparison.Ordinal) && inline)
        {
            yield break;
        }

        bool forwarded = part.ContentType == "message/rfc822";
        string name = EmlMimeParser.FileNameOf(part) ?? (forwarded ? "email chuyển tiếp.eml" : "(không tên)");
        IReadOnlyList<string> path = [.. prefix, AttachmentPrefix + name];

        // Tên file cũng là nội dung cần quét (hay chứa số CCCD / số hợp đồng)
        yield return new ContentBlock(BlockKind.Heading, AttachmentPrefix + name, path);

        string? problem = null;
        byte[]? bytes = null;
        if (attachmentDepth + 1 > MaxAttachmentDepth)
        {
            problem = "lồng quá sâu";
        }
        else if (part.ContentType.StartsWith("image/", StringComparison.Ordinal) || part.ContentType.StartsWith("audio/", StringComparison.Ordinal)
                 || part.ContentType.StartsWith("video/", StringComparison.Ordinal))
        {
            // Không bao giờ đọc được chữ (chưa có OCR) → kết luận TRƯỚC khi giải mã, không tốn RAM giải mã cả ảnh chỉ để bỏ đi
            problem = "ảnh/âm thanh/video — chưa hỗ trợ OCR";
        }
        else if (EmlMimeParser.EstimatedDecodedLength(part) > budget.RemainingAttachmentBytes)
        {
            problem = "vượt giới hạn dung lượng attachment";
        }
        else
        {
            try
            {
                bytes = EmlMimeParser.DecodeBytes(part);
                budget.RemainingAttachmentBytes -= bytes.Length;
            }
            catch (FormatException)
            {
                problem = "giải mã lỗi";
            }
        }

        if (problem != null)
        {
            yield return Unreadable(path, problem);
            yield break;
        }

        using var stream = new MemoryStream(bytes!, writable: false);
        IEnumerable<ContentBlock> inner;
        if (forwarded)
        {
            inner = ExtractMessage(bytes!, path, attachmentDepth + 1, budget);
        }
        else
        {
            var (extractor, why) = ChooseExtractor(stream, name, part.ContentType, bytes!.Length);
            if (extractor == null)
            {
                yield return Unreadable(path, why!);
                yield break;
            }
            inner = extractor.Extract(stream, name);
        }

        // Không được "yield" bên trong try có catch (giới hạn của C# iterator) → tự gọi MoveNext trong try, yield ở ngoài.
        // Attachment lỗi giữa chừng (hỏng, có mật khẩu, zip bomb...) chỉ biến ATTACHMENT đó thành Unreadable; phần còn lại của email vẫn quét.
        using var e = inner.GetEnumerator();
        while (true)
        {
            ContentBlock block;
            try
            {
                if (!e.MoveNext())
                {
                    break;
                }
                block = e.Current;
            }
            catch (ExtractionLimitException)
            {
                problem = "vượt giới hạn tài nguyên";
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Chỉ ghi TÊN loại lỗi, không ghi ex.Message (có thể chứa trích đoạn nội dung)
                problem = $"không đọc được ({ex.GetType().Name})";
                break;
            }
            yield return forwarded ? block : block with { ContextPath = [.. path, .. block.ContextPath] };
        }
        if (problem != null)
        {
            yield return Unreadable(path, problem);
        }
    }

    // Chọn bộ đọc theo NỘI DUNG trước (attachment đặt sai đuôi vẫn đọc đúng), rồi tới đuôi tên file, rồi Content-Type
    private (IContentExtractor? Extractor, string? Why) ChooseExtractor(Stream stream, string name, string contentType, long length)
    {
        if (_lookup == null)
        {
            return (null, "chưa cấu hình bộ đọc attachment");
        }

        stream.Position = 0;
        var detected = FileTypeSniffer.Detect(stream);
        stream.Position = 0;
        string? ext = FileTypeSniffer.CanonicalExtension(detected) ?? NullIfEmpty(Path.GetExtension(name).ToLowerInvariant()) ?? ExtensionOf(contentType);
        if (detected == DetectedFormat.OtherZip)
        {
            return (null, "file nén / tài liệu có mật khẩu");
        }
        if (ext == null || _lookup(ext) is not { SupportsStream: true } extractor)
        {
            return (null, $"định dạng không hỗ trợ ({(ext ?? contentType)})");
        }
        if (length > extractor.MaxFileSizeBytes)
        {
            return (null, "vượt giới hạn dung lượng của bộ đọc");
        }
        return (extractor, null);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static string? ExtensionOf(string contentType) => contentType switch
    {
        "application/pdf" => ".pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        "application/vnd.ms-excel" => ".xls",
        _ => null
    };

    // Khối đánh dấu H3: FileReaderService không quét Text của khối này, chỉ ghi nhận file "chưa đọc hết". Text chỉ có tên attachment + lý do ngắn.
    private static ContentBlock Unreadable(IReadOnlyList<string> path, string reason) =>
        new(BlockKind.Unreadable, $"{path[^1]} ({reason})", path);

    // [Thay đổi] Điểm B/C/D: chuỗi → khối Paragraph theo dòng; chuỗi chỉ toàn khoảng trắng → không phát gì (email bình thường thường có
    // preamble/epilogue rỗng — không thêm khối rác)
    private static IEnumerable<ContentBlock> PlainLines(string text, IReadOnlyList<string> path)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            yield return new ContentBlock(BlockKind.Paragraph, line, path);
        }
    }

    // Bỏ thẻ HTML thô để không quét nhầm tên thẻ/thuộc tính là dữ liệu; giữ lại phần chữ hiển thị
    private static string StripHtmlTags(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
}
