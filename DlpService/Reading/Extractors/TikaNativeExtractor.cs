// [Thay đổi] File mới (tích hợp Tika native): trích text bằng Apache Tika (biên dịch native bằng GraalVM, gọi qua P/Invoke).
// Chỉ nhận các định dạng mà các extractor khác CHƯA đọc được (.doc, .ppt, .rtf); mặc định TẮT. Khi tắt hoặc DLL không nạp được,
// Extensions rỗng → FileReaderService không đăng ký đuôi nào và service chạy y như cũ.
using DlpService.Reading.Native;

namespace DlpService.Reading.Extractors;

/// <summary>Đọc .doc (Word cũ), .ppt (PowerPoint cũ), .rtf bằng Tika native. Trả từng dòng text là 1 đoạn (Paragraph).</summary>
public class TikaNativeExtractor : IContentExtractor
{
    /// <summary>Các đuôi file mà bộ đọc này nhận khi đã bật. Đã thử với file thật: 4 doc, 6 ppt, 40 rtf.</summary>
    internal static readonly string[] SupportedExtensions = [".doc", ".ppt", ".rtf"];

    // [Thay đổi] Tika native chế độ Replace: thêm 8 đuôi mà bộ đọc cũ đang giữ — Tika nhận chúng nhờ Priority cao hơn.
    internal static readonly string[] ReplaceExtensions =
        [".txt", ".csv", ".docx", ".pptx", ".xlsx", ".xls", ".pdf", ".eml", .. SupportedExtensions];

    /// <inheritdoc/>
    // Truy cập TikaNativeRuntime.IsAvailable lần đầu sẽ nạp DLL; các lần sau chỉ là kiểm tra cờ.
    // [Thay đổi] chế độ Replace → nhận cả 11 đuôi
    public IReadOnlyCollection<string> Extensions =>
        TikaNativeRuntime.ReplacesAll ? ReplaceExtensions : TikaNativeRuntime.IsAvailable ? SupportedExtensions : [];

    /// <inheritdoc/>
    // [Thay đổi] chế độ Replace: ưu tiên cao hơn mọi extractor cũ (mặc định 0) để thắng khi trùng đuôi
    public int Priority => TikaNativeRuntime.ReplacesAll ? 100 : 0;

    /// <inheritdoc/>
    public long MaxFileSizeBytes => 50 * 1024 * 1024;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(string filePath)
    {
        var library = TikaNativeRuntime.Library
            ?? throw new TikaNativeException("Tika native chưa sẵn sàng");

        // [Thay đổi] Sửa triệt để lỗi Windows-1258: txt/csv KHÔNG phải UTF-8 (không BOM, không phải nhị phân) thì giải mã bằng
        // TextFileReader, KHÔNG dùng Tika. Lý do: với văn bản thuần, Tika chỉ đoán bảng mã — và đoán SAI với file ANSI tiếng Việt có
        // nhiều chữ không dấu (coi là Windows-1252 → "Nguyá»…n", đã đo: 0/40 cụm tiếng Việt đúng). File UTF-8/BOM và file nhị phân đặt
        // đuôi .txt (PK, %PDF, OLE...) vẫn đi qua Tika như cũ → vẫn phát hiện được file đổi đuôi.
        if (PlainTextExtensions.Contains(Path.GetExtension(filePath)) && IsLegacyEncodedText(filePath))
        {
            foreach (var block in ToBlocks(TextFileReader.ReadAll(filePath)))
            {
                yield return block;
            }
            yield break;
        }

        var (status, text) = library.Extract(filePath);

        // [Thay đổi] Sửa lỗi Windows-1258 (lớp bảo vệ thứ 2): Tika coi txt/csv là nhị phân và trả rỗng → giải mã lại bằng
        // TextFileReader (UTF-8/BOM → Windows-1258) — chỉ là giải mã bảng mã.
        if (NeedsPlainTextFallback(Path.GetExtension(filePath), status, text, new FileInfo(filePath).Length))
        {
            text = TextFileReader.ReadAll(filePath);
            status = 0;
        }

        switch (status)
        {
            case 0:
                break;
            case 5:
                yield break; // file không có chữ nào (VD tài liệu trống): FileReaderService xử lý như file rỗng
            case 4:
                throw new TikaNativeEncryptedException("File có mật khẩu");
            case 7:
                throw new TikaNativeTimeoutException("Quá hạn thời gian đọc file");
            default:
                // [Thay đổi] Sửa lỗi "file bị khoá báo là hỏng": phía Java không mở được file cũng trả mã 3 (hỏng). Thử mở lại
                // từ C# để ném ĐÚNG loại lỗi (IOException = đang bị khoá, UnauthorizedAccessException = không có quyền) —
                // FileReaderService đổi chúng thành thông điệp riêng, sau này thử lại được. Chỉ tốn thêm khi đã có lỗi.
                ThrowIfLockedOrDenied(filePath);
                throw new TikaNativeException($"Tika native trả mã lỗi {status}"); // 2 không hỗ trợ, 3 hỏng, 6 lỗi nội bộ/hết bộ nhớ
        }

        foreach (var block in ToBlocks(text))
        {
            yield return block;
        }
    }

    // [Thay đổi] Sửa lỗi Windows-1258: các đuôi là văn bản thuần — giải mã bảng mã là đủ, không cần parser định dạng
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv" };

    /// <summary>
    /// [Thay đổi] true nếu nên giải mã lại bằng TextFileReader: file văn bản thuần, CÓ dung lượng, mà Tika trả rỗng
    /// (mã 5), không hỗ trợ (mã 2), hoặc "thành công" nhưng text toàn khoảng trắng. File 0 byte thì giữ nguyên là rỗng.
    /// </summary>
    internal static bool NeedsPlainTextFallback(string extension, int status, string text, long fileSize) =>
        PlainTextExtensions.Contains(extension) && fileSize > 0
        && (status is 2 or 5 || (status == 0 && string.IsNullOrWhiteSpace(text)));

    // [Thay đổi] Chữ ký đầu file cho biết KHÔNG phải văn bản bảng mã cũ: BOM (UTF-8, UTF-16 LE/BE) và file nhị phân (zip/docx, pdf, rtf, OLE)
    private static readonly byte[][] NotLegacyTextSignatures =
    [
        [0xEF, 0xBB, 0xBF],                                  // BOM UTF-8
        [0xFF, 0xFE],                                        // BOM UTF-16 LE
        [0xFE, 0xFF],                                        // BOM UTF-16 BE
        [(byte)'P', (byte)'K', 0x03, 0x04],                  // zip: docx/xlsx/pptx
        [(byte)'%', (byte)'P', (byte)'D', (byte)'F'],        // pdf
        [(byte)'{', (byte)'\\', (byte)'r', (byte)'t', (byte)'f'], // rtf
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1],    // OLE: doc/xls/ppt
    ];

    /// <summary>[Thay đổi] Đọc file rồi kiểm tra bằng <see cref="IsLegacyEncodedText(ReadOnlySpan{byte})"/>.</summary>
    internal static bool IsLegacyEncodedText(string filePath)
    {
        using var stream = ExtractorHelpers.OpenShared(filePath);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return IsLegacyEncodedText(memory.GetBuffer().AsSpan(0, (int)memory.Length));
    }

    /// <summary>
    /// [Thay đổi] true nếu nội dung là văn bản bảng mã cũ 8-bit (VD Windows-1258): KHÔNG rỗng, KHÔNG có BOM (UTF-8/UTF-16), KHÔNG có
    /// chữ ký file nhị phân (zip/docx, pdf, OLE doc/xls, rtf), KHÔNG có byte 0 ở 8 KB đầu (nhị phân hoặc UTF-16 không BOM),
    /// và KHÔNG phải UTF-8 hợp lệ.
    /// </summary>
    internal static bool IsLegacyEncodedText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return false;
        }
        foreach (byte[] signature in NotLegacyTextSignatures)
        {
            if (bytes.StartsWith(signature))
            {
                return false;
            }
        }
        if (bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0))
        {
            return false;
        }
        return !System.Text.Unicode.Utf8.IsValid(bytes);
    }

    /// <summary>
    /// [Thay đổi] Thử mở file: ném IOException nếu file đang bị chương trình khác khoá, UnauthorizedAccessException nếu không có
    /// quyền; mở được thì không làm gì (lỗi thật nằm ở nội dung file).
    /// </summary>
    internal static void ThrowIfLockedOrDenied(string filePath)
    {
        using var probe = ExtractorHelpers.OpenShared(filePath);
    }

    /// <summary>
    /// Tách text thành từng dòng, mỗi dòng 1 block. QUAN TRỌNG: FileReaderService ghép các block bằng "\n", nên chỉ được cắt ở
    /// ranh giới dòng sẵn có — cắt giữa dòng (VD theo khối byte cố định) sẽ chèn xuống dòng vào giữa số thẻ/CCCD và làm
    /// regex không còn nhận ra chúng nữa.
    /// </summary>
    public static IEnumerable<ContentBlock> ToBlocks(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            yield return new ContentBlock(BlockKind.Paragraph, line, ContextPath: []);
        }
    }
}
