// [Thay đổi] File mới (Benchmark T02): dựng PDF chữ / PDF scan (trang toàn ảnh) / PDF trộn bằng PdfPig PdfDocumentBuilder.
// Font: mặc định Helvetica chuẩn (Standard14, chỉ ký tự Latin cơ bản) → chữ được BỎ DẤU; truyền --font <ttf> (VD DejaVuSans.ttf trên máy
// bench) thì nhúng TTF và giữ nguyên tiếng Việt. Ghi rõ trong dataset.md vì PDF bỏ dấu cho số chỗ khớp khác PDF có dấu.
using System.Text;
using DlpService.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DlpBenchData;

internal sealed class PdfWriter(byte[]? ttf)
{
    private const int LinesPerPage = 52;
    private const int WrapWidth = 95;

    /// <summary>true nếu chữ trong PDF giữ dấu (có TTF); false = đã bỏ dấu (Helvetica).</summary>
    public bool KeepsDiacritics => ttf != null;

    /// <summary>PDF gồm <paramref name="scanPages"/> trang chỉ có ảnh (đặt TRƯỚC) rồi các trang chữ từ <paramref name="lines"/>.</summary>
    public byte[] Build(IReadOnlyList<string> lines, int scanPages = 0, byte[]? png = null)
    {
        var builder = new PdfDocumentBuilder();
        var font = ttf != null ? builder.AddTrueTypeFont(ttf) : builder.AddStandard14Font(Standard14Font.Helvetica);

        for (int s = 0; s < scanPages; s++)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddPng(png!, new PdfRectangle(36, 36, 559, 806));
        }

        var wrapped = lines.SelectMany(l => Wrap(ttf != null ? l : ToAscii(l))).ToList();
        for (int p = 0; p * LinesPerPage < wrapped.Count; p++)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            double y = 805;
            foreach (var l in wrapped.Skip(p * LinesPerPage).Take(LinesPerPage))
            {
                if (l.Length > 0) page.AddText(l, 9, new PdfPoint(36, y), font);
                y -= 15;
            }
        }
        return FixId(builder.Build());
    }

    // PdfPig ghi /ID [ <guid><guid> ] NGẪU NHIÊN vào trailer → mỗi lần sinh 1 sha256 khác. Ghi đè 2 chuỗi hex (giữ nguyên độ dài nên bảng
    // xref không lệch) bằng SHA-256 của phần nội dung đứng trước → tất định, PDF vẫn hợp lệ.
    private static byte[] FixId(byte[] pdf)
    {
        byte[] marker = Encoding.ASCII.GetBytes("/ID [ <");
        int at = pdf.AsSpan().LastIndexOf(marker);
        if (at < 0) return pdf;
        int hexStart = at + marker.Length;
        string hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdf.AsSpan(0, at)));
        int first = Array.IndexOf(pdf, (byte)'>', hexStart);
        int len1 = first - hexStart;               // độ dài hex của ID thứ nhất
        int second = first + 2;                    // bỏ qua "><"
        int len2 = Array.IndexOf(pdf, (byte)'>', second) - second;
        if (len1 <= 0 || len2 <= 0 || len1 + len2 > hex.Length) return pdf;
        Encoding.ASCII.GetBytes(hex, 0, len1, pdf, hexStart);
        Encoding.ASCII.GetBytes(hex, len1, len2, pdf, second);
        return pdf;
    }

    /// <summary>Số dòng văn bản (trước khi ngắt) cần để được đúng <paramref name="pages"/> trang chữ — dòng đệm ~3 câu ≈ 230 ký tự ≈ 3 dòng PDF.</summary>
    public static int LinesForPages(int pages) => Math.Max(1, pages * LinesPerPage / 3);

    // Helvetica chuẩn không có ký tự tiếng Việt → bỏ dấu (đ → d), ký tự còn lại ngoài ASCII in được thay bằng '?'
    private static string ToAscii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in Normalizer.RemoveDiacritics(s)) sb.Append(c is >= ' ' and <= '~' ? c : '?');
        return sb.ToString();
    }

    private static IEnumerable<string> Wrap(string line)
    {
        for (int start = 0; start < line.Length; start += WrapWidth)
        {
            yield return line.Substring(start, Math.Min(WrapWidth, line.Length - start));
        }
        if (line.Length == 0) yield return line;
    }
}
