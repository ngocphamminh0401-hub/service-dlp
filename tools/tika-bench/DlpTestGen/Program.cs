// [Thay đổi] File mới: sinh bộ file test cho benchmark Tika vs extractor hiện tại của DlpService.
// Mỗi file chứa các "giá trị đánh dấu" TỔNG HỢP (họ tên + mã QX…, số giống CCCD, email @example.com) đặt ở nhiều vị trí
// (thân, bảng, header/footer, ghi chú, sheet ẩn...) — đáp án đúng ghi vào manifest.json. Toàn bộ dữ liệu là giả.
// Cách dùng: DlpTestGen <thư-mục-ra> <python-có-xlwt> [số-file-mỗi-định-dạng=20]
using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace DlpTestGen;

internal sealed record Marker(string Kind, string Loc, string Value);

internal sealed class Entry
{
    public string File { get; set; } = "";
    public string Format { get; set; } = "";     // định dạng THẬT của nội dung (khác đuôi file khi Category = renamed)
    public string Category { get; set; } = "";   // normal | large | renamed | corrupt | garbage | empty
    public string Phase { get; set; } = "";      // normal | large (để đo RAM đỉnh từng giai đoạn)
    public long Size { get; set; }
    public List<Marker> Markers { get; set; } = [];
}

internal static class Gen
{
    private static Random Rng = new(20261012); // seed mặc định cố định: sinh lại ra đúng bộ file cũ. [Thay đổi] bỏ readonly để nhận hạt giống từ tham số (tập kiểm tra thứ hai)
    private static string _out = "";
    private static string _python = "python";
    private static readonly List<Entry> Manifest = [];

    private static readonly string[] Names =
    [
        "Trần Thị Mỹ Lệ", "Nguyễn Văn Đạt", "Lê Hoàng Phương", "Phạm Thị Thuỷ",
        "Đặng Quốc Việt", "Võ Thị Ánh Tuyết", "Huỳnh Minh Khôi", "Bùi Thị Hồng Nhung"
    ];

    // Câu độn trung tính: không chứa số hay mẫu nhạy cảm, để không làm nhiễu số chỗ khớp regex
    private static readonly string[] Filler =
    [
        "Báo cáo tổng kết hoạt động quý được trình bày trước ban lãnh đạo vào cuối tháng.",
        "Phòng hành chính phối hợp cùng các bộ phận để hoàn thiện quy trình làm việc mới.",
        "Mọi đề xuất cải tiến cần được gửi qua hệ thống quản lý nội bộ để được xem xét.",
        "Kế hoạch đào tạo nhân sự tập trung vào kỹ năng giao tiếp và quản lý thời gian.",
        "Các bên liên quan đã thống nhất lịch trình triển khai và phân công trách nhiệm cụ thể.",
        "Hệ thống sao lưu dữ liệu hoạt động ổn định, không ghi nhận sự cố đáng kể nào.",
        "Cuộc họp định kỳ bàn về tiến độ dự án và những khó khăn phát sinh trong quá trình thực hiện.",
        "Tài liệu hướng dẫn sử dụng được cập nhật để phù hợp với phiên bản phần mềm hiện hành."
    ];

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Cách dùng: DlpTestGen <thư-mục-ra> <python-có-xlwt> [số-file-mỗi-định-dạng=20]");
            return 1;
        }
        _out = Path.GetFullPath(args[0]);
        _python = args[1];
        int n = args.Length > 2 ? int.Parse(args[2]) : 20;
        // [Thay đổi] Tham số thứ 4 (tuỳ chọn): hạt giống ngẫu nhiên khác để sinh tập KIỂM TRA chưa từng thấy (cùng cấu trúc, giá trị khác)
        if (args.Length > 3) Rng = new Random(int.Parse(args[3]));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, true); // sinh lại từ đầu cho sạch
        }
        Directory.CreateDirectory(_out);

        string[] formats = ["txt", "csv", "docx", "pptx", "xlsx", "xls", "pdf", "eml"];
        var firstSample = new Dictionary<string, (byte[] Data, List<Marker> Markers)>();

        // ---- 1. File bình thường: n file mỗi định dạng ----
        foreach (var fmt in formats)
        {
            for (int i = 0; i < n; i++)
            {
                var (data, ms) = Make(fmt, i, large: false);
                Add("normal", "normal", fmt, $"normal/{fmt}/{fmt}_{i:00}.{fmt}", data, ms);
                if (i == 0) firstSample[fmt] = (data, ms);
            }
            Console.WriteLine($"  {fmt}: {n} file");
        }

        // ---- 2. File lỗi: cắt cụt, rác, rỗng (kỳ vọng: xử lý êm, không treo/sập) ----
        foreach (var fmt in formats)
        {
            var (data, _) = firstSample[fmt];
            bool zipOrBinary = fmt is "docx" or "pptx" or "xlsx" or "xls" or "pdf";
            if (zipOrBinary)
            {
                Add("corrupt", "normal", fmt, $"fault/{fmt}_trunc40.{fmt}", data[..(data.Length * 4 / 10)], []);
                Add("corrupt", "normal", fmt, $"fault/{fmt}_trunc70.{fmt}", data[..(data.Length * 7 / 10)], []);
            }
            var junk = new byte[20_000];
            Rng.NextBytes(junk);
            Add("garbage", "normal", fmt, $"fault/{fmt}_garbage.{fmt}", junk, []);
            Add("empty", "normal", fmt, $"fault/{fmt}_empty.{fmt}", [], []);
        }

        // ---- 3. File đổi đuôi: nội dung thật khác với đuôi (Tika nhận theo nội dung, extractor cũ theo đuôi) ----
        (string From, string To)[] renames = [("docx", "txt"), ("pdf", "docx"), ("xlsx", "xls"), ("docx", "pdf"), ("txt", "docx"), ("xls", "xlsx")];
        foreach (var (from, to) in renames)
        {
            var (data, ms) = firstSample[from];
            Add("renamed", "normal", from, $"renamed/{from}_as.{to}", data, ms);
        }

        // ---- 4. File lớn: mỗi định dạng 1 file (đo thời gian/RAM, và việc cắt ở 2 triệu ký tự) ----
        foreach (var fmt in formats)
        {
            var (data, ms) = Make(fmt, 0, large: true);
            Add("large", "large", fmt, $"large/{fmt}_large.{fmt}", data, ms);
            Console.WriteLine($"  large {fmt}: {data.Length / 1024.0 / 1024.0:N1} MB");
        }

        File.WriteAllText(Path.Combine(_out, "manifest.json"),
            JsonSerializer.Serialize(Manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
        Console.WriteLine($"Xong: {Manifest.Count} file, manifest tại {Path.Combine(_out, "manifest.json")}");
        return 0;
    }

    private static void Add(string category, string phase, string fmt, string rel, byte[] data, List<Marker> ms)
    {
        string path = Path.Combine(_out, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        Manifest.Add(new Entry { File = rel, Format = fmt, Category = category, Phase = phase, Size = data.Length, Markers = ms });
    }

    private static (byte[], List<Marker>) Make(string fmt, int i, bool large) => fmt switch
    {
        "txt" => MakeTxt(i, large),
        "csv" => MakeCsv(i, large),
        "docx" => MakeDocx(i, large),
        "pptx" => MakePptx(large),
        "xlsx" => MakeXlsx(large),
        "xls" => MakeXls(large),
        "pdf" => MakePdf(large),
        "eml" => MakeEml(large),
        _ => throw new ArgumentException(fmt)
    };

    // ===================== Giá trị đánh dấu =====================

    // Sinh 1 câu chứa 3 giá trị đánh dấu (kiểu CCCD giả, họ tên + mã QX, email giả) và ghi vào danh sách đáp án
    private static string Sent(string loc, List<Marker> ms)
    {
        var (name, tok, id, email) = Pieces(loc, ms);
        return $"Khách hàng {name} {tok}, CCCD {id}, email {email}.";
    }

    private static (string Name, string Tok, string Id, string Email) Pieces(string loc, List<Marker> ms)
    {
        string tok = "QX" + Rng.Next(100000, 999999);
        string name = Names[Rng.Next(Names.Length)];
        string id = "079" + Rng.NextInt64(100_000_000, 999_999_999);
        string email = $"kh{Rng.Next(1000, 9999)}@example.com";
        ms.Add(new("cccd", loc, id));
        ms.Add(new("vi", loc, $"{name} {tok}"));
        ms.Add(new("email", loc, email));
        return (name, tok, id, email);
    }

    private static string FillerLine() => Filler[Rng.Next(Filler.Length)];

    private static string X(string s) => SecurityElement.Escape(s) ?? "";

    // ===================== TXT =====================
    private static (byte[], List<Marker>) MakeTxt(int i, bool large)
    {
        var ms = new List<Marker>();
        var sb = new StringBuilder();
        if (!large)
        {
            int lines = 150;
            for (int k = 0; k < lines; k++)
            {
                if (k == 0) sb.AppendLine(Sent("top", ms));
                else if (k == lines / 2) sb.AppendLine(Sent("mid", ms));
                else if (k == lines - 1) sb.AppendLine(Sent("end", ms));
                else sb.AppendLine(FillerLine());
            }
        }
        else
        {
            // Mốc theo SỐ KÝ TỰ: top / ~1 triệu ký tự (trong giới hạn 2 triệu) / cuối (~6,8 triệu, ngoài giới hạn quét; giữ file dưới 10 MB là giới hạn của extractor txt)
            bool at1m = false;
            while (sb.Length < 6_800_000)
            {
                if (sb.Length == 0) sb.AppendLine(Sent("top", ms));
                else if (!at1m && sb.Length >= 1_000_000) { sb.AppendLine(Sent("at-1M", ms)); at1m = true; }
                else sb.AppendLine(FillerLine());
            }
            sb.AppendLine(Sent("end-6.8M", ms));
        }
        string text = sb.ToString();
        // Đa dạng bảng mã theo số thứ tự file: UTF-8 / UTF-8 BOM / UTF-16 LE / Windows-1258 (ANSI tiếng Việt)
        byte[] data = (large ? 0 : i % 4) switch
        {
            1 => [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(text)],
            2 => [.. new byte[] { 0xFF, 0xFE }, .. Encoding.Unicode.GetBytes(text)],
            3 => EncodeCp1258(text),
            _ => new UTF8Encoding(false).GetBytes(text)
        };
        return (data, ms);
    }

    // Windows-1258 chỉ có sẵn chữ dựng sẵn cho nguyên âm có dấu mũ/trăng/móc (ê, ă, ơ...) và 5 dấu thanh RỜI (huyền, sắc,
    // ngã, hỏi, nặng) ghép sau chữ cái. "ệ" phải mã hoá thành "ê" + dấu nặng. Bộ mã hoá có sẵn của .NET không tự làm
    // việc này nên viết tay: chữ nào không mã hoá thẳng được thì tách dấu thanh ra rồi mã hoá "chữ gốc + dấu thanh".
    private static byte[] EncodeCp1258(string text)
    {
        var enc = Encoding.GetEncoding(1258);
        const string tones = "̣̀́̃̉";
        using var ms = new MemoryStream();
        foreach (char c in text.Normalize(NormalizationForm.FormC))
        {
            string one = c.ToString();
            byte[] direct = enc.GetBytes(one);
            if (enc.GetString(direct) == one)
            {
                ms.Write(direct);
                continue;
            }
            string d = one.Normalize(NormalizationForm.FormD);
            char tone = d.FirstOrDefault(ch => tones.Contains(ch));
            string baseChar = d.Replace(tone.ToString(), "").Normalize(NormalizationForm.FormC);
            ms.Write(enc.GetBytes(baseChar));
            if (tone != ' ') ms.Write(enc.GetBytes(tone.ToString()));
        }
        byte[] bytes = ms.ToArray();
        if (enc.GetString(bytes).Normalize(NormalizationForm.FormC) == text.Normalize(NormalizationForm.FormC))
        {
            return bytes;
        }
        Console.WriteLine("  (cảnh báo: không mã hoá được cp1258 khứ hồi, dùng UTF-8 thay)");
        return new UTF8Encoding(false).GetBytes(text);
    }

    // ===================== CSV =====================
    private static (byte[], List<Marker>) MakeCsv(int i, bool large)
    {
        var ms = new List<Marker>();
        string sep = i % 2 == 0 ? "," : ";";
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(sep, "STT", "Họ tên", "CCCD", "Email", "Ghi chú"));
        int rows = large ? 60_000 : 100; // ~8,9 MB: dưới giới hạn 10 MB của CsvExtractor
        for (int r = 1; r <= rows; r++)
        {
            bool mark = r == 1 || r == rows / 2 || r == rows;
            string loc = r == 1 ? "row-first" : r == rows ? "row-last" : "row-mid";
            if (mark)
            {
                var (name, tok, id, email) = Pieces(loc, ms);
                sb.AppendLine(string.Join(sep, r, $"{name} {tok}", id, email, FillerLine().Replace(',', ' ')));
            }
            else
            {
                sb.AppendLine(string.Join(sep, r, "Người dùng mẫu", "(trống)", "n/a", FillerLine().Replace(',', ' ')));
            }
        }
        if (!large)
        {
            // Ô trong dấu ngoặc kép, chứa dấu phân cách và xuống dòng: parser CSV tự viết dễ sai chỗ này
            string tok = "QX" + Rng.Next(100000, 999999);
            string id = "079" + Rng.NextInt64(100_000_000, 999_999_999);
            ms.Add(new("vi", "quoted-cell", $"Ghi chú đặc biệt {tok}"));
            ms.Add(new("cccd", "quoted-cell", id));
            sb.AppendLine($"{rows + 1}{sep}\"Ghi chú đặc biệt {tok}{sep} CCCD {id}\r\nxuống dòng trong ô\"{sep}-{sep}-{sep}-");
        }
        string text = sb.ToString();
        byte[] data = (large ? 0 : i % 3) switch
        {
            1 => [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes(text)],
            2 => EncodeCp1258(text),
            _ => new UTF8Encoding(false).GetBytes(text)
        };
        return (data, ms);
    }

    // ===================== ZIP helper =====================
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    // ===================== DOCX =====================
    private static (byte[], List<Marker>) MakeDocx(int i, bool large)
    {
        var ms = new List<Marker>();
        var body = new StringBuilder();
        string P(string t) => $"<w:p><w:r><w:t xml:space=\"preserve\">{X(t)}</w:t></w:r></w:p>";

        body.Append(P("BIÊN BẢN LÀM VIỆC"));
        if (large)
        {
            int paras = 40_000;
            for (int k = 0; k < paras; k++)
            {
                if (k == 0) body.Append(P(Sent("body-top", ms)));
                else if (k == paras / 8) body.Append(P(Sent("body-mid", ms)));
                else if (k == paras - 1) body.Append(P(Sent("body-end", ms)));
                else body.Append(P(FillerLine()));
            }
            body.Append("<w:sectPr/>");
            return (DocxZip(body.ToString(), null, null, null, null), ms);
        }

        for (int k = 0; k < 5; k++) body.Append(P(FillerLine()));
        body.Append(P(Sent("body", ms)));
        for (int k = 0; k < 5; k++) body.Append(P(FillerLine()));

        // Bảng 2 cột; ô thứ hai chứa câu có đánh dấu
        string tbl = Sent("table", ms);
        body.Append("<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"6000\"/></w:tblGrid>");
        body.Append($"<w:tr><w:tc><w:p><w:r><w:t>Thông tin</w:t></w:r></w:p></w:tc><w:tc>{P(tbl)}</w:tc></w:tr></w:tbl>");

        // Hộp văn bản (text box): thư viện chỉ đọc "đoạn văn thân" sẽ bỏ sót
        string tb = Sent("textbox", ms);
        body.Append("<w:p><w:r><w:pict><v:shape xmlns:v=\"urn:schemas-microsoft-com:vml\" style=\"width:300pt;height:60pt\"><v:textbox>" +
                    $"<w:txbxContent>{P(tb)}</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>");

        // Chú thích cuối trang (footnote) và bình luận (comment)
        body.Append("<w:p><w:r><w:t>Đoạn có chú thích.</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p>");
        body.Append("<w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:t>Đoạn có bình luận.</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p>");
        body.Append("<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rId10\"/><w:footerReference w:type=\"default\" r:id=\"rId11\"/></w:sectPr>");

        string header = Sent("header", ms), footer = Sent("footer", ms), foot = Sent("footnote", ms), comment = Sent("comment", ms);
        return (DocxZip(body.ToString(), header, footer, foot, comment), ms);
    }

    private static byte[] DocxZip(string bodyXml, string? header, string? footer, string? footnote, string? comment)
    {
        const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                          "xmlns:r=\"" + Rel + "\" xmlns:v=\"urn:schemas-microsoft-com:vml\"";
        string P(string t) => $"<w:p><w:r><w:t xml:space=\"preserve\">{X(t)}</w:t></w:r></w:p>";
        var parts = new List<(string, string)>
        {
            ("word/document.xml", $"{Xml}<w:document {ns}><w:body>{bodyXml}</w:body></w:document>")
        };
        var overrides = new StringBuilder("<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>");
        var rels = new StringBuilder();
        if (header != null)
        {
            parts.Add(("word/header1.xml", $"{Xml}<w:hdr {ns}>{P(header)}</w:hdr>"));
            overrides.Append("<Override PartName=\"/word/header1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml\"/>");
            rels.Append($"<Relationship Id=\"rId10\" Type=\"{Rel}/header\" Target=\"header1.xml\"/>");
        }
        if (footer != null)
        {
            parts.Add(("word/footer1.xml", $"{Xml}<w:ftr {ns}>{P(footer)}</w:ftr>"));
            overrides.Append("<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>");
            rels.Append($"<Relationship Id=\"rId11\" Type=\"{Rel}/footer\" Target=\"footer1.xml\"/>");
        }
        if (footnote != null)
        {
            parts.Add(("word/footnotes.xml", $"{Xml}<w:footnotes {ns}>" +
                "<w:footnote w:type=\"separator\" w:id=\"0\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>" +
                "<w:footnote w:type=\"continuationSeparator\" w:id=\"1\"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>" +
                $"<w:footnote w:id=\"2\">{P(footnote)}</w:footnote></w:footnotes>"));
            overrides.Append("<Override PartName=\"/word/footnotes.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml\"/>");
            rels.Append($"<Relationship Id=\"rId12\" Type=\"{Rel}/footnotes\" Target=\"footnotes.xml\"/>");
        }
        if (comment != null)
        {
            parts.Add(("word/comments.xml", $"{Xml}<w:comments {ns}><w:comment w:id=\"0\" w:author=\"Tester\" w:date=\"2026-01-01T00:00:00Z\">{P(comment)}</w:comment></w:comments>"));
            overrides.Append("<Override PartName=\"/word/comments.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml\"/>");
            rels.Append($"<Relationship Id=\"rId13\" Type=\"{Rel}/comments\" Target=\"comments.xml\"/>");
        }
        parts.Add(("word/_rels/document.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\">{rels}</Relationships>"));
        parts.Add(("_rels/.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"));
        parts.Insert(0, ("[Content_Types].xml", $"{Xml}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>{overrides}</Types>"));
        return Zip([.. parts]);
    }

    // ===================== XLSX =====================
    private sealed record Sheet(string Name, bool Hidden, List<string[]> Rows);

    private static (byte[], List<Marker>) MakeXlsx(bool large)
    {
        var ms = new List<Marker>();
        var sheets = BuildSheets(large, ms);
        return (XlsxZip(sheets), ms);
    }

    // Dùng chung cho xlsx và xls: sheet 1 (header + dòng), sheet 2, sheet ẩn
    private static List<Sheet> BuildSheets(bool large, List<Marker> ms)
    {
        var s1 = new List<string[]> { new[] { "STT", "Họ tên", "CCCD", "Email", "Ghi chú" } };
        int rows = large ? 40_000 : 60;
        for (int r = 1; r <= rows; r++)
        {
            if (r == 1 || r == rows / 2 || r == rows)
            {
                var (name, tok, id, email) = Pieces(r == 1 ? "s1-first" : r == rows ? "s1-last" : "s1-mid", ms);
                s1.Add([r.ToString(), $"{name} {tok}", id, email, FillerLine()]);
            }
            else s1.Add([r.ToString(), "Người dùng mẫu", "(trống)", "n/a", FillerLine()]);
        }
        var sheets = new List<Sheet> { new("DanhSach", false, s1) };
        if (!large)
        {
            var s2 = new List<string[]> { new[] { "Mục", "Nội dung" }, new[] { "1", Sent("sheet2", ms) } };
            var s3 = new List<string[]> { new[] { "Mục", "Nội dung" }, new[] { "1", Sent("hidden-sheet", ms) } };
            sheets.Add(new("GhiChu", false, s2));
            sheets.Add(new("DuLieuAn", true, s3));
        }
        return sheets;
    }

    private static string ColName(int c) => c < 26 ? ((char)('A' + c)).ToString() : ((char)('A' + c / 26 - 1)).ToString() + (char)('A' + c % 26);

    private static byte[] XlsxZip(List<Sheet> sheets)
    {
        var shared = new Dictionary<string, int>();
        var sst = new StringBuilder();
        int Sst(string s)
        {
            if (!shared.TryGetValue(s, out int idx))
            {
                idx = shared.Count;
                shared[s] = idx;
                sst.Append($"<si><t xml:space=\"preserve\">{X(s)}</t></si>");
            }
            return idx;
        }

        var parts = new List<(string, string)>();
        var wbSheets = new StringBuilder();
        var wbRels = new StringBuilder();
        var overrides = new StringBuilder("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
        for (int s = 0; s < sheets.Count; s++)
        {
            var sh = sheets[s];
            var sb = new StringBuilder($"{Xml}<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            for (int r = 0; r < sh.Rows.Count; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                for (int c = 0; c < sh.Rows[r].Length; c++)
                {
                    sb.Append($"<c r=\"{ColName(c)}{r + 1}\" t=\"s\"><v>{Sst(sh.Rows[r][c])}</v></c>");
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            parts.Add(($"xl/worksheets/sheet{s + 1}.xml", sb.ToString()));
            wbSheets.Append($"<sheet name=\"{X(sh.Name)}\" sheetId=\"{s + 1}\"{(sh.Hidden ? " state=\"hidden\"" : "")} r:id=\"rId{s + 1}\"/>");
            wbRels.Append($"<Relationship Id=\"rId{s + 1}\" Type=\"{Rel}/worksheet\" Target=\"worksheets/sheet{s + 1}.xml\"/>");
            overrides.Append($"<Override PartName=\"/xl/worksheets/sheet{s + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }
        wbRels.Append($"<Relationship Id=\"rId99\" Type=\"{Rel}/sharedStrings\" Target=\"sharedStrings.xml\"/>");
        parts.Insert(0, ("[Content_Types].xml", $"{Xml}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>{overrides}</Types>"));
        parts.Add(("_rels/.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"));
        parts.Add(("xl/workbook.xml", $"{Xml}<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{Rel}\"><sheets>{wbSheets}</sheets></workbook>"));
        parts.Add(("xl/_rels/workbook.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\">{wbRels}</Relationships>"));
        parts.Add(("xl/sharedStrings.xml", $"{Xml}<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"{shared.Count}\" uniqueCount=\"{shared.Count}\">{sst}</sst>"));
        return Zip([.. parts]);
    }

    // ===================== XLS (qua Python + xlwt) =====================
    private static (byte[], List<Marker>) MakeXls(bool large)
    {
        var ms = new List<Marker>();
        var sheets = BuildSheets(large, ms);
        if (large)
        {
            sheets[0] = sheets[0] with { Rows = sheets[0].Rows.Take(60_000).ToList() }; // giới hạn của định dạng .xls: 65.536 dòng
        }
        string tmp = Path.Combine(Path.GetTempPath(), "dlp-gen-xls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string spec = Path.Combine(tmp, "spec.json"), outXls = Path.Combine(tmp, "out.xls");
            File.WriteAllText(spec, JsonSerializer.Serialize(new { sheets = sheets.Select(s => new { name = s.Name, hidden = s.Hidden, rows = s.Rows }) }), new UTF8Encoding(false));
            var psi = new ProcessStartInfo(_python) { RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "gen_xls.py"));
            psi.ArgumentList.Add(spec);
            psi.ArgumentList.Add(outXls);
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("gen_xls.py lỗi: " + err);
            return (File.ReadAllBytes(outXls), ms);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    // ===================== PPTX =====================
    private sealed record Slide(string Title, string Body, string[][]? Table, string? Notes);

    private static (byte[], List<Marker>) MakePptx(bool large)
    {
        var ms = new List<Marker>();
        var slides = new List<Slide>();
        if (!large)
        {
            slides.Add(new("Báo cáo khách hàng", Sent("slide1-body", ms), [["Thông tin", Sent("slide1-table", ms)]], Sent("notes1", ms)));
            slides.Add(new("Trang hai", Sent("slide2-body", ms), null, Sent("notes2", ms)));
            slides.Add(new("Trang ba", FillerLine(), null, null));
        }
        else
        {
            int n = 150;
            for (int k = 0; k < n; k++)
            {
                string body = k == 0 ? Sent("slide-top", ms) : k == n / 2 ? Sent("slide-mid", ms) : k == n - 1 ? Sent("slide-end", ms) : FillerLine();
                slides.Add(new($"Trang {k + 1}", body, null, null));
            }
        }
        return (PptxZip(slides), ms);
    }

    private static byte[] PptxZip(List<Slide> slides)
    {
        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        string ns = $"xmlns:a=\"{A}\" xmlns:r=\"{Rel}\" xmlns:p=\"{P}\"";
        const string tree0 = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";
        string Theme() =>
            $"{Xml}<a:theme xmlns:a=\"{A}\" name=\"T\"><a:themeElements><a:clrScheme name=\"c\">" +
            "<a:dk1><a:srgbClr val=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"1F1F1F\"/></a:dk2><a:lt2><a:srgbClr val=\"EEEEEE\"/></a:lt2>" +
            "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>" +
            "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
            "<a:fontScheme name=\"f\"><a:majorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
            "<a:fmtScheme name=\"x\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
            "<a:lnStyleLst><a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"12700\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln><a:ln w=\"19050\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
            "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
            "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>";
        const string clrMap = "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>";
        string Sp(int id, string name, int y, int h, string text) =>
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"500000\" y=\"{y}\"/><a:ext cx=\"8000000\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr>" +
            $"<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"vi-VN\"/><a:t>{X(text)}</a:t></a:r></a:p></p:txBody></p:sp>";

        var ct = new StringBuilder(
            "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
            "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
            "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
            "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>" +
            "<Override PartName=\"/ppt/theme/theme2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>" +
            "<Override PartName=\"/ppt/notesMasters/notesMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.notesMaster+xml\"/>");
        var presRels = new StringBuilder(
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{Rel}/notesMaster\" Target=\"notesMasters/notesMaster1.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{Rel}/theme\" Target=\"theme/theme1.xml\"/>");
        var sldIds = new StringBuilder();
        var parts = new List<(string, string)>();

        for (int s = 0; s < slides.Count; s++)
        {
            var sl = slides[s];
            int no = s + 1;
            var tree = new StringBuilder(tree0);
            tree.Append(Sp(2, "Title", 300000, 800000, sl.Title));
            tree.Append(Sp(3, "Body", 1300000, 1200000, sl.Body));
            if (sl.Table != null)
            {
                tree.Append("<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"Table\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr><p:xfrm><a:off x=\"500000\" y=\"3000000\"/><a:ext cx=\"8000000\" cy=\"800000\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr/><a:tblGrid><a:gridCol w=\"2500000\"/><a:gridCol w=\"5500000\"/></a:tblGrid>");
                foreach (var row in sl.Table)
                {
                    tree.Append("<a:tr h=\"400000\">");
                    foreach (var cell in row) tree.Append($"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"vi-VN\"/><a:t>{X(cell)}</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc>");
                    tree.Append("</a:tr>");
                }
                tree.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
            }
            parts.Add(($"ppt/slides/slide{no}.xml", $"{Xml}<p:sld {ns}><p:cSld><p:spTree>{tree}</p:spTree></p:cSld></p:sld>"));
            var slideRels = new StringBuilder($"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>");
            if (sl.Notes != null)
            {
                slideRels.Append($"<Relationship Id=\"rId2\" Type=\"{Rel}/notesSlide\" Target=\"../notesSlides/notesSlide{no}.xml\"/>");
                parts.Add(($"ppt/notesSlides/notesSlide{no}.xml", $"{Xml}<p:notes {ns}><p:cSld><p:spTree>{tree0}" +
                    "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Notes\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
                    $"<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"vi-VN\"/><a:t>{X(sl.Notes)}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:notes>"));
                parts.Add(($"ppt/notesSlides/_rels/notesSlide{no}.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/notesMaster\" Target=\"../notesMasters/notesMaster1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/slide\" Target=\"../slides/slide{no}.xml\"/></Relationships>"));
                ct.Append($"<Override PartName=\"/ppt/notesSlides/notesSlide{no}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml\"/>");
            }
            parts.Add(($"ppt/slides/_rels/slide{no}.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\">{slideRels}</Relationships>"));
            ct.Append($"<Override PartName=\"/ppt/slides/slide{no}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
            presRels.Append($"<Relationship Id=\"rId{10 + s}\" Type=\"{Rel}/slide\" Target=\"slides/slide{no}.xml\"/>");
            sldIds.Append($"<p:sldId id=\"{256 + s}\" r:id=\"rId{10 + s}\"/>");
        }

        parts.Insert(0, ("[Content_Types].xml", $"{Xml}<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>{ct}</Types>"));
        parts.Add(("_rels/.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>"));
        parts.Add(("ppt/presentation.xml", $"{Xml}<p:presentation {ns}><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:notesMasterIdLst><p:notesMasterId r:id=\"rId2\"/></p:notesMasterIdLst><p:sldIdLst>{sldIds}</p:sldIdLst><p:sldSz cx=\"9144000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>"));
        parts.Add(("ppt/_rels/presentation.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\">{presRels}</Relationships>"));
        parts.Add(("ppt/slideMasters/slideMaster1.xml", $"{Xml}<p:sldMaster {ns}><p:cSld><p:spTree>{tree0}</p:spTree></p:cSld>{clrMap}<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>"));
        parts.Add(("ppt/slideMasters/_rels/slideMaster1.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"../theme/theme1.xml\"/></Relationships>"));
        parts.Add(("ppt/slideLayouts/slideLayout1.xml", $"{Xml}<p:sldLayout {ns} type=\"blank\"><p:cSld name=\"Blank\"><p:spTree>{tree0}</p:spTree></p:cSld></p:sldLayout>"));
        parts.Add(("ppt/slideLayouts/_rels/slideLayout1.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>"));
        parts.Add(("ppt/notesMasters/notesMaster1.xml", $"{Xml}<p:notesMaster {ns}><p:cSld><p:spTree>{tree0}</p:spTree></p:cSld>{clrMap}</p:notesMaster>"));
        parts.Add(("ppt/notesMasters/_rels/notesMaster1.xml.rels", $"{Xml}<Relationships xmlns=\"{PkgRel}\"><Relationship Id=\"rId1\" Type=\"{Rel}/theme\" Target=\"../theme/theme2.xml\"/></Relationships>"));
        parts.Add(("ppt/theme/theme1.xml", Theme()));
        parts.Add(("ppt/theme/theme2.xml", Theme()));
        return Zip([.. parts]);
    }

    // ===================== PDF =====================
    private static (byte[], List<Marker>) MakePdf(bool large)
    {
        var ms = new List<Marker>();
        var builder = new PdfDocumentBuilder();
        // Font TrueType nhúng để có tiếng Việt có dấu (14 font chuẩn của PDF không có chữ Việt)
        var font = builder.AddTrueTypeFont(File.ReadAllBytes(@"C:\Windows\Fonts\arial.ttf"));
        int pages = large ? 150 : 3;
        for (int p = 0; p < pages; p++)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            double y = 800;
            void Line(string t)
            {
                page.AddText(t, 10, new PdfPoint(40, y), font);
                y -= 16;
            }
            Line($"Trang {p + 1}");
            // Mỗi câu đánh dấu tách thành 3 dòng (cắt ở dấu phẩy) để không tràn khỏi trang
            string? sent = null;
            if (!large && p == 0) sent = Sent("page1", ms);
            else if (!large && p == 1) sent = Sent("page2", ms);
            else if (!large && p == 2) sent = Sent("page-last", ms);
            else if (large && p == 0) sent = Sent("page-top", ms);
            else if (large && p == pages / 2) sent = Sent("page-mid", ms);
            else if (large && p == pages - 1) sent = Sent("page-end", ms);
            if (sent != null)
            {
                foreach (var part in sent.Split(", ")) Line(part.TrimEnd('.') + (part.EndsWith('.') ? "." : ","));
            }
            for (int k = 0; k < 30; k++) Line(FillerLine());
        }
        return (builder.Build(), ms);
    }

    // ===================== EML =====================
    private static string B64(string s) => "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";

    // Quoted-printable thô cho UTF-8; ngắt dòng mềm ở ~70 byte
    private static string Qp(string text)
    {
        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            int col = 0;
            foreach (byte b in Encoding.UTF8.GetBytes(line))
            {
                string tok = b is >= 33 and <= 126 && b != '=' || b == ' ' ? ((char)b).ToString() : $"={b:X2}";
                if (col + tok.Length > 70) { sb.Append("=\r\n"); col = 0; }
                sb.Append(tok);
                col += tok.Length;
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Wrap76(string b64)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        return sb.ToString();
    }

    private static (byte[], List<Marker>) MakeEml(bool large)
    {
        var ms = new List<Marker>();
        string subject = Sent("subject", ms);
        string fromName = Names[Rng.Next(Names.Length)] + " QX" + Rng.Next(100000, 999999);
        ms.Add(new("vi", "from-name", fromName));
        string bodyPlain = FillerLine() + "\n" + Sent("body-plain", ms) + "\n" + FillerLine();
        string bodyHtml = $"<html><body><p>{X(FillerLine())}</p><p>{X(Sent("body-html", ms))}</p></body></html>";
        string attTxt = FillerLine() + "\n" + Sent("attach-txt", ms) + "\n";
        if (large)
        {
            var sbA = new StringBuilder(attTxt);
            while (sbA.Length < 6_000_000) sbA.AppendLine(FillerLine());
            attTxt = sbA.ToString();
        }
        var docxMs = new List<Marker>();
        var (docx, dm) = large ? ([], docxMs) : MakeDocx(1, false);
        foreach (var m in dm) ms.Add(m with { Loc = "attach-docx:" + m.Loc });

        const string b1 = "=_mixed_001", b2 = "=_alt_001";
        var sb = new StringBuilder();
        sb.Append("From: ").Append(B64(fromName)).Append(" <sender@example.com>\r\n");
        sb.Append("To: receiver@example.com\r\n");
        sb.Append("Subject: ").Append(B64(subject)).Append("\r\n");
        sb.Append("Date: Mon, 12 Oct 2026 09:00:00 +0700\r\nMIME-Version: 1.0\r\n");
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{b1}\"\r\n\r\n");
        sb.Append($"--{b1}\r\nContent-Type: multipart/alternative; boundary=\"{b2}\"\r\n\r\n");
        sb.Append($"--{b2}\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n{Qp(bodyPlain)}\r\n");
        sb.Append($"--{b2}\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{Wrap76(Convert.ToBase64String(Encoding.UTF8.GetBytes(bodyHtml)))}\r\n");
        sb.Append($"--{b2}--\r\n");
        sb.Append($"--{b1}\r\nContent-Type: text/plain; charset=UTF-8; name=\"dinhkem.txt\"\r\nContent-Disposition: attachment; filename=\"dinhkem.txt\"\r\nContent-Transfer-Encoding: base64\r\n\r\n{Wrap76(Convert.ToBase64String(Encoding.UTF8.GetBytes(attTxt)))}\r\n");
        if (!large)
        {
            sb.Append($"--{b1}\r\nContent-Type: application/vnd.openxmlformats-officedocument.wordprocessingml.document; name=\"dinhkem.docx\"\r\nContent-Disposition: attachment; filename=\"dinhkem.docx\"\r\nContent-Transfer-Encoding: base64\r\n\r\n{Wrap76(Convert.ToBase64String(docx))}\r\n");
        }
        sb.Append($"--{b1}--\r\n");
        return (new UTF8Encoding(false).GetBytes(sb.ToString()), ms);
    }
}
