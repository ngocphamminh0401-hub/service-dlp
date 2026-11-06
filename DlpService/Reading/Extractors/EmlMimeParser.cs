// [Thay đổi] File mới (mở rộng định dạng - Bước 9): bộ phân tích MIME TỐI GIẢN cho file .eml — project chưa có
// thư viện đọc email nào, và .eml chỉ là text theo chuẩn RFC822/MIME (header + thân, thân có thể chia nhiều "part").
// Đủ dùng cho email do Outlook/Gmail tạo; KHÔNG đảm bảo đúng 100% các trường hợp email dị dạng hiếm gặp.
using System.Text;
using System.Text.RegularExpressions;

namespace DlpService.Reading.Extractors;

internal static class EmlMimeParser
{
    // [Thay đổi] Chặn đệ quy quá sâu (Bước 11): multipart lồng nhau quá nhiều lớp có thể làm tràn stack — chặn
    // TRƯỚC khi đệ quy tới lớp đó (StackOverflowException không bắt được bằng try/catch).
    private const int MaxDepth = 20;

    /// <summary>1 phần MIME: có thể là phần lá (có BodyText, Children rỗng) hoặc phần multipart (Children khác rỗng, BodyText null).</summary>
    public sealed record MimePart(
        IReadOnlyDictionary<string, string> Headers,
        string ContentType,
        string? BodyText,
        IReadOnlyList<MimePart> Children);

    /// <summary>Phân tích toàn bộ nội dung file .eml (đã đọc hết thành byte).</summary>
    /// <param name="rawBytes">Byte gốc của file .eml.</param>
    public static MimePart Parse(byte[] rawBytes) =>
        // Latin-1: mỗi byte ↔ đúng 1 ký tự, không mất/đổi byte nào — dùng để định vị header/boundary (luôn là
        // ASCII) an toàn bằng các hàm xử lý chuỗi, thay vì tự tính offset byte thủ công.
        Parse(Encoding.Latin1.GetString(rawBytes), depth: 0);

    private static MimePart Parse(string raw, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException($"File .eml lồng multipart quá sâu (> {MaxDepth} lớp) — nghi ngờ file bất thường");
        }

        var (headerText, bodyText) = SplitHeaderBody(raw);
        var headers = ParseHeaders(headerText);

        string contentTypeRaw = GetHeader(headers, "Content-Type") ?? "text/plain";
        string mainType = contentTypeRaw.Split(';')[0].Trim().ToLowerInvariant();
        string? boundary = ExtractParam(contentTypeRaw, "boundary");

        if (mainType.StartsWith("multipart/", StringComparison.Ordinal) && boundary != null)
        {
            var children = SplitMultipart(bodyText, boundary).Select(part => Parse(part, depth + 1)).ToList();
            return new MimePart(headers, mainType, null, children);
        }

        string transferEncoding = GetHeader(headers, "Content-Transfer-Encoding") ?? "7bit";
        byte[] rawBodyBytes = Encoding.Latin1.GetBytes(bodyText); // khôi phục đúng byte gốc từ chuỗi Latin-1
        byte[] decodedBytes = DecodeTransferEncoding(rawBodyBytes, transferEncoding);
        string charset = ExtractParam(contentTypeRaw, "charset") ?? "utf-8";
        string text = DecodeCharset(decodedBytes, charset);
        return new MimePart(headers, mainType, text, []);
    }

    // Tách header/thân bằng dòng trống đầu tiên (CRLF CRLF chuẩn, hoặc LF LF cho file lưu kiểu Unix)
    private static (string headers, string body) SplitHeaderBody(string raw)
    {
        int idx = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int sepLen = 4;
        if (idx < 0)
        {
            idx = raw.IndexOf("\n\n", StringComparison.Ordinal);
            sepLen = 2;
        }
        return idx < 0 ? (raw, string.Empty) : (raw[..idx], raw[(idx + sepLen)..]);
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

    private static string? GetHeader(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var v) ? v : null;

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
    private static List<string> SplitMultipart(string body, string boundary)
    {
        string marker = "--" + boundary;
        var parts = new List<string>();
        int pos = body.IndexOf(marker, StringComparison.Ordinal);
        while (pos >= 0)
        {
            int start = pos + marker.Length;
            if (start + 1 < body.Length && body[start] == '-' && body[start + 1] == '-')
            {
                break; // gặp "--boundary--" → hết phần
            }
            int next = body.IndexOf(marker, start, StringComparison.Ordinal);
            if (next < 0)
            {
                break;
            }
            string part = body[start..next].TrimStart('\r', '\n');
            if (part.Length > 0)
            {
                parts.Add(part);
            }
            pos = next;
        }
        return parts;
    }

    private static byte[] DecodeTransferEncoding(byte[] body, string encoding) => encoding.Trim().ToLowerInvariant() switch
    {
        "base64" => TryBase64(body),
        "quoted-printable" => DecodeQuotedPrintable(Encoding.Latin1.GetString(body), stopAtSoftBreak: true),
        _ => body // 7bit/8bit/binary — không mã hoá thêm, giữ nguyên byte gốc
    };

    private static byte[] TryBase64(byte[] body)
    {
        try
        {
            string cleaned = Encoding.ASCII.GetString(body).Replace("\r", "").Replace("\n", "");
            return Convert.FromBase64String(cleaned);
        }
        catch (FormatException)
        {
            return body; // base64 hỏng — trả nguyên bản thô còn hơn làm hỏng cả file
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
