// [Thay đổi] File mới (mở rộng định dạng - Bước 9): bộ phân tích MIME TỐI GIẢN cho file .eml — project chưa có
// thư viện đọc email nào, và .eml chỉ là text theo chuẩn RFC822/MIME (header + thân, thân có thể chia nhiều "part").
// Đủ dùng cho email do Outlook/Gmail tạo; KHÔNG đảm bảo đúng 100% các trường hợp email dị dạng hiếm gặp.
//
// [Thay đổi] M6 — viết lại phần lõi để không nhân bản dữ liệu. Bản cũ: Latin-1 cả file thành chuỗi (×2 dung lượng), cắt chuỗi con ở MỖI lớp
// multipart, rồi GIẢI MÃ MỌI phần (kể cả attachment ảnh/PDF) trước khi bên gọi kịp lọc → email 19 MB cấp phát 635 MB (EmlMemoryTests).
// Bản mới:
//  - Cả email nằm trong 1 mảng byte; mỗi MimePart chỉ nhớ VỊ TRÍ (BodyStart, BodyLength) — không cắt chuỗi con. Tìm ranh giới bằng
//    Span<byte>.IndexOf (tương đương HOÀN TOÀN tìm chuỗi trên chuỗi Latin-1 của bản cũ, vì Latin-1 = đúng 1 byte ↔ 1 ký tự).
//  - Chỉ phần HEADER (nhỏ) được đổi sang chuỗi để dùng lại nguyên ParseHeaders / DecodeEncodedWords / ExtractParam cũ.
//  - Giải mã LƯỜI: DecodeText (phần text) / DecodeBytes (attachment) chỉ chạy khi EmlExtractor duyệt tới phần đó.
//  - Giới hạn cấu trúc (số phần, độ sâu) → ExtractionLimitException → FileReaderService ghi "incomplete" (limit_exceeded), không lọt im lặng.
// Quy tắc tách header/thân và multipart giữ Y HỆT bản cũ — khoá bằng EmlCharacterizationTests (golden của bản cũ).
using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.RegularExpressions;

namespace DlpService.Reading.Extractors;

internal static class EmlMimeParser
{
    // [Thay đổi] Chặn đệ quy quá sâu (Bước 11): multipart lồng nhau quá nhiều lớp có thể làm tràn stack — chặn
    // TRƯỚC khi đệ quy tới lớp đó (StackOverflowException không bắt được bằng try/catch).
    private const int MaxDepth = 20;

    // [Thay đổi] M6: số phần tối đa của 1 email (email thật: vài – vài chục). Trước đây không giới hạn: 25 MB toàn "--b\r\n\r\n" → hàng trăm
    // nghìn MimePart + Dictionary header.
    internal const int MaxParts = 1000;

    private static readonly byte[] CrlfCrlf = "\r\n\r\n"u8.ToArray();
    private static readonly byte[] LfLf = "\n\n"u8.ToArray();

    /// <summary>1 phần MIME: lá (Children rỗng, thân ở <see cref="Source"/>[BodyStart..BodyStart+BodyLength]) hoặc multipart (Children khác rỗng).</summary>
    /// <param name="ContentTypeRaw">Giá trị đầy đủ của Content-Type (có charset/name...), để giải mã lười.</param>
    /// <param name="Preamble">[Thay đổi] Điểm D: vị trí phần TRƯỚC "--boundary" đầu tiên (multipart); Length = 0 nếu không có.</param>
    /// <param name="Epilogue">[Thay đổi] Điểm D: vị trí phần SAU "--boundary--" (multipart); Length = 0 nếu không có.</param>
    public sealed record MimePart(
        IReadOnlyDictionary<string, string> Headers,
        string ContentType,
        string ContentTypeRaw,
        byte[] Source,
        int BodyStart,
        int BodyLength,
        IReadOnlyList<MimePart> Children,
        (int Start, int Length) Preamble = default,
        (int Start, int Length) Epilogue = default)
    {
        /// <summary>Byte thân (chưa giải mã Content-Transfer-Encoding) — không sao chép.</summary>
        public ReadOnlySpan<byte> RawBody => Source.AsSpan(BodyStart, BodyLength);
    }

    /// <summary>Phân tích cấu trúc file .eml (đã đọc hết thành byte). KHÔNG giải mã thân phần nào.</summary>
    /// <param name="rawBytes">Byte gốc của file .eml.</param>
    /// <exception cref="ExtractionLimitException">Lồng quá sâu hoặc quá nhiều phần.</exception>
    public static MimePart Parse(byte[] rawBytes)
    {
        int parts = 0;
        return Parse(rawBytes, 0, rawBytes.Length, depth: 0, ref parts);
    }

    private static MimePart Parse(byte[] src, int start, int length, int depth, ref int parts)
    {
        if (depth > MaxDepth)
        {
            // [Thay đổi] M6: InvalidDataException ("file hỏng" → đọc thất bại, không ghi DB) → ExtractionLimitException (ghi nhận incomplete)
            throw new ExtractionLimitException($"File .eml lồng multipart quá sâu (> {MaxDepth} lớp) — nghi ngờ file bất thường");
        }
        if (++parts > MaxParts)
        {
            throw new ExtractionLimitException($"File .eml có quá nhiều phần (> {MaxParts})");
        }

        var (headerLength, bodyStart, bodyLength) = SplitHeaderBody(src.AsSpan(start, length));
        var headers = ParseHeaders(Encoding.Latin1.GetString(src, start, headerLength));
        bodyStart += start;

        string contentTypeRaw = GetHeader(headers, "Content-Type") ?? "text/plain";
        string mainType = contentTypeRaw.Split(';')[0].Trim().ToLowerInvariant();
        string? boundary = ExtractParam(contentTypeRaw, "boundary");

        if (mainType.StartsWith("multipart/", StringComparison.Ordinal) && boundary != null)
        {
            var split = SplitMultipart(src, bodyStart, bodyLength, boundary);
            var children = new List<MimePart>();
            foreach (var (partStart, partLength) in split.Parts)
            {
                children.Add(Parse(src, partStart, partLength, depth + 1, ref parts));
            }
            // [Thay đổi] Điểm B: không tách được phần nào (khai boundary nhưng thân không có dòng "--boundary") → giữ CẢ thân (BodyLength) để
            // EmlExtractor quét như text. Trước đây BodyLength = 0 → cả thân bị bỏ (chỗ giấu dữ liệu).
            return new MimePart(headers, mainType, contentTypeRaw, src, bodyStart, children.Count == 0 ? bodyLength : 0, children,
                split.Preamble, split.Epilogue);
        }

        return new MimePart(headers, mainType, contentTypeRaw, src, bodyStart, bodyLength, []);
    }

    // [Thay đổi] M6: giải mã LƯỜI phần text — đúng các bước của bản cũ (transfer encoding → charset), chỉ chạy khi EmlExtractor duyệt tới
    /// <summary>Thân 1 phần lá đã giải mã thành chuỗi.</summary>
    public static string DecodeText(MimePart part)
    {
        string transferEncoding = GetHeader(part.Headers, "Content-Transfer-Encoding") ?? "7bit";
        byte[] decodedBytes = DecodeTransferEncoding(part.RawBody, transferEncoding);
        string charset = ExtractParam(part.ContentTypeRaw, "charset") ?? "utf-8";
        return DecodeCharset(decodedBytes, charset);
    }

    // [Thay đổi] M6: giải mã attachment thành byte (để đưa cho extractor khác qua MemoryStream). Base64 giải mã THẲNG từ byte: lọc bỏ khoảng trắng
    // vào buffer mượn từ ArrayPool rồi Base64.DecodeFromUtf8 → không tạo chuỗi UTF-16 nào (bản cũ: ASCII string + 2 lần Replace + FromBase64String).
    /// <summary>Thân 1 phần lá sau Content-Transfer-Encoding (chưa đổi bảng mã).</summary>
    /// <exception cref="FormatException">base64 hỏng.</exception>
    public static byte[] DecodeBytes(MimePart part)
    {
        string encoding = (GetHeader(part.Headers, "Content-Transfer-Encoding") ?? "7bit").Trim().ToLowerInvariant();
        var body = part.RawBody;
        if (encoding == "quoted-printable")
        {
            return DecodeQuotedPrintable(Encoding.Latin1.GetString(body), stopAtSoftBreak: true);
        }
        if (encoding != "base64")
        {
            return body.ToArray(); // 7bit/8bit/binary
        }

        byte[] compact = ArrayPool<byte>.Shared.Rent(body.Length);
        try
        {
            int n = 0;
            foreach (byte b in body)
            {
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                {
                    compact[n++] = b;
                }
            }
            byte[] output = new byte[Base64.GetMaxDecodedFromUtf8Length(n)];
            var status = Base64.DecodeFromUtf8(compact.AsSpan(0, n), output, out _, out int written);
            if (status != OperationStatus.Done)
            {
                throw new FormatException("Attachment base64 hỏng");
            }
            return written == output.Length ? output : output.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compact);
        }
    }

    /// <summary>Kích thước sau giải mã ước tính (để kiểm ngân sách TRƯỚC khi giải mã).</summary>
    public static long EstimatedDecodedLength(MimePart part)
    {
        string encoding = (GetHeader(part.Headers, "Content-Transfer-Encoding") ?? "7bit").Trim().ToLowerInvariant();
        return encoding == "base64" ? part.BodyLength / 4L * 3 : part.BodyLength;
    }

    /// <summary>Giá trị 1 header (không phân biệt hoa/thường); null nếu không có.</summary>
    public static string? GetHeader(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var v) ? v : null;

    // [Thay đổi] M6: tên file đính kèm — Content-Disposition filename*= (RFC 2231, VD UTF-8''H%E1%BB%A3p.docx) → filename= → Content-Type name=.
    // Hàm riêng (Regex.Escape) thay vì ExtractParam: "filename*" truyền thẳng vào regex của ExtractParam sẽ thành quantifier "e*".
    /// <summary>Tên file của phần đính kèm; null nếu không khai.</summary>
    public static string? FileNameOf(MimePart part)
    {
        string disposition = GetHeader(part.Headers, "Content-Disposition") ?? "";
        var rfc2231 = Regex.Match(disposition, @"filename\*\s*=\s*([^']*)'[^']*'([^;]+)", RegexOptions.IgnoreCase);
        if (rfc2231.Success)
        {
            try
            {
                var enc = rfc2231.Groups[1].Value.Length > 0 ? Encoding.GetEncoding(rfc2231.Groups[1].Value) : Encoding.UTF8;
                return enc.GetString(PercentDecode(rfc2231.Groups[2].Value.Trim().Trim('"')));
            }
            catch (ArgumentException)
            {
                // bảng mã lạ → thử các cách bên dưới
            }
        }
        foreach (var (value, param) in new[] { (disposition, "filename"), (part.ContentTypeRaw, "name") })
        {
            var m = Regex.Match(value, @"(?:^|;)\s*" + Regex.Escape(param) + @"\s*=\s*""?([^"";]+)""?", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups[1].Value.Trim();
            }
        }
        return null;
    }

    // "%E1%BB%A3p" → byte (KHÔNG dùng Uri.UnescapeDataString: hàm đó luôn hiểu %XX là UTF-8, sai với bảng mã khác khai trong RFC 2231)
    private static byte[] PercentDecode(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)s[i]);
            }
        }
        return [.. bytes];
    }

    // Tách header/thân bằng dòng trống đầu tiên (CRLF CRLF chuẩn, hoặc LF LF cho file lưu kiểu Unix)
    // [Thay đổi] M6: trả VỊ TRÍ thay vì 2 chuỗi con; thứ tự thử y hệt bản cũ (CRLFCRLF trước, không có mới thử LFLF)
    private static (int headerLength, int bodyStart, int bodyLength) SplitHeaderBody(ReadOnlySpan<byte> raw)
    {
        int idx = raw.IndexOf(CrlfCrlf);
        int sepLen = 4;
        if (idx < 0)
        {
            idx = raw.IndexOf(LfLf);
            sepLen = 2;
        }
        return idx < 0 ? (raw.Length, raw.Length, 0) : (idx, idx + sepLen, raw.Length - idx - sepLen);
    }

    // Ghép dòng tiếp nối (dòng bắt đầu bằng space/tab = "unfold" theo chuẩn RFC822), giải mã RFC 2047 cho chữ có dấu
    private static Dictionary<string, string> ParseHeaders(string headerText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentName = null;
        var currentValue = new StringBuilder();

        void Flush()
        {
            if (currentName == null)
            {
                return;
            }
            string value = DecodeEncodedWords(currentValue.ToString().Trim());
            result[currentName] = result.TryGetValue(currentName, out var existing) ? existing + ", " + value : value;
        }

        foreach (var line in headerText.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (line.Length == 0)
            {
                continue;
            }
            if ((line[0] == ' ' || line[0] == '\t') && currentName != null)
            {
                currentValue.Append(' ').Append(line.Trim());
                continue;
            }
            Flush();
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                currentName = null;
                continue;
            }
            currentName = line[..colon].Trim();
            currentValue.Clear();
            currentValue.Append(line[(colon + 1)..].Trim());
        }
        Flush();
        return result;
    }

    // "=?UTF-8?B?...?=" (Base64) hoặc "=?UTF-8?Q?...?=" (Quoted-Printable) — cách mã hoá chữ có dấu trong header
    // (Subject/From tiếng Việt sẽ ở dạng này, không mã hoá thì hiện toàn "=?UTF-8?B?...?=" thay vì tên thật)
    private static readonly Regex EncodedWordPattern = new(@"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", RegexOptions.Compiled);

    private static string DecodeEncodedWords(string value) =>
        EncodedWordPattern.Replace(value, match =>
        {
            try
            {
                var encoding = Encoding.GetEncoding(match.Groups[1].Value);
                string data = match.Groups[3].Value;
                byte[] bytes = match.Groups[2].Value.Equals("B", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(data)
                    : DecodeQuotedPrintable(data.Replace('_', ' '), stopAtSoftBreak: false);
                return encoding.GetString(bytes);
            }
            catch
            {
                return match.Value; // giải mã lỗi (header hỏng) — giữ nguyên chuỗi gốc, không làm hỏng cả file
            }
        });

    private static string? ExtractParam(string headerValue, string paramName)
    {
        var match = Regex.Match(headerValue, paramName + @"\s*=\s*""?([^"";]+)""?", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // Tách thân multipart theo boundary: dòng "--boundary" ngăn cách các phần, "--boundary--" đánh dấu kết thúc
    // [Thay đổi] M6: trả VỊ TRÍ các phần (không cắt chuỗi); bỏ CR/LF ở đầu mỗi phần, bỏ phần rỗng.
    // [Thay đổi] Điểm A + D (nguyên tắc DLP: byte nào có trong file cũng phải được quét hoặc được ghi nhận — trình đọc email chỉ HIỂN THỊ các
    // phần, nhưng file .eml gửi ra ngoài mang theo MỌI byte):
    //  - A: phần cuối không có "--boundary" đóng → kéo tới HẾT thân (trước đây bị bỏ: xoá dòng "--boundary--" là giấu được phần cuối).
    //  - D: trả thêm vị trí preamble (trước "--boundary" đầu) và epilogue (sau "--boundary--") để EmlExtractor quét.
    private static (List<(int Start, int Length)> Parts, (int, int) Preamble, (int, int) Epilogue) SplitMultipart(
        byte[] src, int bodyStart, int bodyLength, string boundary)
    {
        var parts = new List<(int, int)>();
        (int, int) preamble = default, epilogue = default;
        string markerText = "--" + boundary;
        if (markerText.Any(c => c > 0xFF))
        {
            return (parts, preamble, epilogue); // tìm trên byte Latin-1 (mọi ký tự ≤ 0xFF) → ký tự > 0xFF không bao giờ khớp
        }
        byte[] marker = Encoding.Latin1.GetBytes(markerText);
        var body = src.AsSpan(bodyStart, bodyLength);

        int pos = body.IndexOf(marker);
        if (pos > 0)
        {
            preamble = (bodyStart, pos);
        }
        while (pos >= 0)
        {
            int start = pos + marker.Length;
            if (start + 1 < body.Length && body[start] == (byte)'-' && body[start + 1] == (byte)'-')
            {
                // gặp "--boundary--" → hết phần; phần còn lại (bỏ CR/LF đầu) là epilogue
                int epiStart = start + 2;
                while (epiStart < body.Length && (body[epiStart] == (byte)'\r' || body[epiStart] == (byte)'\n'))
                {
                    epiStart++;
                }
                if (epiStart < body.Length)
                {
                    epilogue = (bodyStart + epiStart, body.Length - epiStart);
                }
                break;
            }
            int rel = start <= body.Length ? body[start..].IndexOf(marker) : -1;
            int next = rel < 0 ? body.Length : start + rel; // A: không có marker tiếp theo → tới hết thân
            int partStart = start;
            while (partStart < next && (body[partStart] == (byte)'\r' || body[partStart] == (byte)'\n'))
            {
                partStart++; // = TrimStart('\r', '\n') của bản cũ
            }
            if (next - partStart > 0)
            {
                parts.Add((bodyStart + partStart, next - partStart));
            }
            if (rel < 0)
            {
                break;
            }
            pos = next;
        }
        return (parts, preamble, epilogue);
    }

    // [Thay đổi] Điểm D: preamble/epilogue không có header riêng → giải mã như text/plain mặc định (UTF-8, không transfer encoding)
    /// <summary>Giải mã 1 khoảng byte (preamble/epilogue) thành chuỗi UTF-8.</summary>
    public static string DecodePlain(byte[] source, (int Start, int Length) range) =>
        range.Length == 0 ? string.Empty : DecodeCharset(source.AsSpan(range.Start, range.Length).ToArray(), "utf-8");

    private static byte[] DecodeTransferEncoding(ReadOnlySpan<byte> body, string encoding) => encoding.Trim().ToLowerInvariant() switch
    {
        "base64" => TryBase64(body),
        "quoted-printable" => DecodeQuotedPrintable(Encoding.Latin1.GetString(body), stopAtSoftBreak: true),
        _ => body.ToArray() // 7bit/8bit/binary — không mã hoá thêm, giữ nguyên byte gốc
    };

    private static byte[] TryBase64(ReadOnlySpan<byte> body)
    {
        try
        {
            // [Thay đổi] M6: bỏ 2 lần Replace("\r")/Replace("\n") (thêm 2 chuỗi cùng kích thước) — Convert.FromBase64String vốn bỏ qua CR/LF/space/tab.
            // Giữ ASCII.GetString như bản cũ: byte > 127 → '?' → base64 hỏng → trả nguyên bản thô (đúng hành vi cũ).
            return Convert.FromBase64String(Encoding.ASCII.GetString(body));
        }
        catch (FormatException)
        {
            return body.ToArray(); // base64 hỏng — trả nguyên bản thô còn hơn làm hỏng cả file
        }
    }

    // stopAtSoftBreak=true: dùng cho thân email ("=" cuối dòng là nối mềm, phải bỏ luôn dấu xuống dòng theo sau).
    // stopAtSoftBreak=false: dùng cho encoded-word trong header (không có khái niệm nối mềm ở đây).
    private static byte[] DecodeQuotedPrintable(string text, bool stopAtSoftBreak)
    {
        var output = new List<byte>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '=' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                output.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (text[i] == '=' && stopAtSoftBreak && i + 1 < text.Length && (text[i + 1] == '\r' || text[i + 1] == '\n'))
            {
                while (i + 1 < text.Length && (text[i + 1] == '\r' || text[i + 1] == '\n'))
                {
                    i++;
                }
            }
            else
            {
                output.AddRange(Encoding.Latin1.GetBytes(text[i].ToString()));
            }
        }
        return [.. output];
    }

    private static string DecodeCharset(byte[] bytes, string charset)
    {
        try
        {
            return Encoding.GetEncoding(charset.Trim()).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes); // charset lạ/không hỗ trợ — thử UTF-8 thay vì làm hỏng cả file
        }
    }
}
