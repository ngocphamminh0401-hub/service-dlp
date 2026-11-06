// [Thay đổi] File mới (mục 8 - "canary" giá trị đánh dấu): tự tạo file mẫu chèn giá trị đánh dấu (CCCD/SĐT hợp lệ,
// email, secret entropy cao, số "lạ" không khớp rule cụ thể), chạy đúng luồng thật extractor -> finding (RegexScanner)
// -> ChunkSelector (4.10) -> Masker (4.11), rồi xác nhận KHÔNG giá trị đánh dấu nào còn nguyên văn trong "gói" cuối
// cùng (các chunk còn lại sau khi mask, đã bỏ chunk bị huỷ).
//
// Cách chạy: dotnet run --project tools/canary/DlpCanaryTest
using System.Text;
using DlpService.Llm;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging.Abstractions;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

// ---- Giá trị đánh dấu (canary): distinctive, hợp lệ theo đúng validator thật để bài test có ý nghĩa ----
const string CccdCanary = "001199000001";              // qua vn_cccd_prefix: tỉnh 001, nữ 1900s, sinh 1999
const string PhoneCanary = "0324681357";                // qua vn_phone: đầu số 032 (Viettel), 7 số cuối không giả
const string EmailCanary = "canary.leak.marker@vidu-canary.test"; // qua email_personal (không phải hộp thư chung)
const string SecretCanary = "CanaryKey9xQ7zR2vLmP4wJ8"; // qua secret_entropy: dài, nhiều loại ký tự, entropy cao
const string LonelyNumberCanary = "778899001";          // 9 số "lạ", không khớp rule cụ thể -> chỉ lớp 2 (regex rộng) bắt được

string[] canaries = [CccdCanary, PhoneCanary, EmailCanary, SecretCanary, LonelyNumberCanary];

string work = Path.Combine(Path.GetTempPath(), "dlp-canary-test");
if (Directory.Exists(work))
{
    Directory.Delete(work, true);
}
Directory.CreateDirectory(work);

var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(Path.Combine(AppContext.BaseDirectory, "rules"));
var scanner = new RegexScanner(patterns);
var reader = new FileReaderService();
var report = new CanaryReport();

report.Section("Nội dung nhúng giá trị đánh dấu vào mọi file mẫu");
string body = $"""
    Họ tên: Nguyễn Văn Canary
    Số CCCD: {CccdCanary}
    Số điện thoại: {PhoneCanary}
    Email: {EmailCanary}
    api_key={SecretCanary}
    Mã hồ sơ nội bộ: {LonelyNumberCanary}
    """;

report.Queue("Sample.Txt", Samples.WriteTxt(work, body), canaries);
report.Queue("Sample.Csv", Samples.WriteCsv(work, body), canaries);
report.Queue("Sample.Docx", Samples.WriteDocx(work, body), canaries);
report.Queue("Sample.Xlsx", Samples.WriteXlsx(work, body), canaries);
report.Queue("Sample.Eml", Samples.WriteEml(work, body), canaries);

report.RunAll(reader, scanner);

string resultFile = report.Save();
Console.WriteLine();
Console.WriteLine(report.Failures == 0 ? "== TẤT CẢ PASS — không canary nào lọt vào gói ==" : $"== {report.Failures} FAIL ==");
Console.WriteLine($"Báo cáo: {resultFile}");
Directory.Delete(work, true);
return report.Failures == 0 ? 0 : 1;

/// <summary>Tạo file mẫu chứa nguyên "body" theo từng định dạng. .pdf/.pptx bỏ qua (chưa có thư viện ghi trong
/// project — PdfPig chỉ đọc; dựng .pptx hợp lệ từ đầu bằng OpenXml tốn công hơn giá trị bài test này mang lại).</summary>
internal static class Samples
{
    public static string WriteTxt(string dir, string body)
    {
        string path = Path.Combine(dir, "canary.txt");
        File.WriteAllText(path, body, Encoding.UTF8);
        return path;
    }

    public static string WriteCsv(string dir, string body)
    {
        // Ép về dạng bảng: mỗi dòng "Nhãn: Giá trị" -> 1 hàng CSV 2 cột
        string path = Path.Combine(dir, "canary.csv");
        var lines = new List<string> { "Nhan,Gia tri" };
        foreach (var line in body.Split('\n'))
        {
            int sep = line.IndexOf(':');
            if (sep < 0)
            {
                continue;
            }
            lines.Add($"{line[..sep].Trim()},{line[(sep + 1)..].Trim()}");
        }
        File.WriteAllText(path, string.Join("\n", lines), Encoding.UTF8);
        return path;
    }

    public static string WriteDocx(string dir, string body)
    {
        string path = Path.Combine(dir, "canary.docx");
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var docBody = new W.Body(new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
            new W.Run(new W.Text("Hồ sơ đánh dấu (canary)"))));
        foreach (var line in body.Split('\n'))
        {
            docBody.Append(new W.Paragraph(new W.Run(new W.Text(line))));
        }
        main.Document = new W.Document(docBody);
        main.Document.Save();
        return path;
    }

    public static string WriteXlsx(string dir, string body)
    {
        string path = Path.Combine(dir, "canary.xlsx");
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = doc.AddWorkbookPart();
        workbookPart.Workbook = new S.Workbook();
        var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new S.SheetData();
        sheetPart.Worksheet = new S.Worksheet(sheetData);

        sheetData.Append(TextRow("Nhan", "Gia tri")); // dòng tiêu đề cột
        foreach (var line in body.Split('\n'))
        {
            int sep = line.IndexOf(':');
            if (sep < 0)
            {
                continue;
            }
            sheetData.Append(TextRow(line[..sep].Trim(), line[(sep + 1)..].Trim()));
        }

        var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());
        sheets.Append(new S.Sheet { Id = workbookPart.GetIdOfPart(sheetPart), SheetId = 1, Name = "CanarySheet" });
        workbookPart.Workbook.Save();
        return path;

        static S.Row TextRow(params string[] cells)
        {
            var row = new S.Row();
            foreach (var text in cells)
            {
                row.Append(new S.Cell { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)) });
            }
            return row;
        }
    }

    public static string WriteEml(string dir, string body)
    {
        string path = Path.Combine(dir, "canary.eml");
        // [Thay đổi] Toàn bộ raw (kể cả header ASCII) encode bằng UTF-8, KHỚP với charset khai trong Content-Type —
        // dùng Latin-1 ở đây sẽ làm hỏng byte của phần thân tiếng Việt (khác với EmlMimeParser, nơi Latin-1 chỉ là
        // mẹo giữ nguyên byte để định vị header/boundary ASCII, không dùng để mã hoá nội dung có dấu).
        string raw = "Subject: Canary test\r\nFrom: a@x.com\r\nTo: b@y.com\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n" + body.Replace("\n", "\r\n") + "\r\n";
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(raw));
        return path;
    }
}

/// <summary>In PASS/FAIL và lưu báo cáo Markdown vào tools/canary/results/.</summary>
internal sealed class CanaryReport
{
    private readonly StringBuilder _md = new();
    private readonly List<(string Name, string FilePath, string[] Canaries)> _pending = [];

    public int Failures { get; private set; }

    public void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
        _md.AppendLine().AppendLine("## " + title).AppendLine();
    }

    // Chỉ ghi lại — chạy thật ở RunAll() để tách rõ "tạo file mẫu" (ở Main) và "chấm điểm" (ở đây)
    public void Queue(string name, string filePath, string[] canaries) =>
        _pending.Add((name, filePath, canaries));

    public void RunAll(FileReaderService reader, RegexScanner scanner)
    {
        Section("Kết quả từng file mẫu — không canary nào được lọt vào gói gửi LLM");
        foreach (var (name, filePath, canaries) in _pending)
        {
            RunOne(name, filePath, canaries, reader, scanner);
        }
    }

    private void RunOne(string name, string filePath, string[] canaries, FileReaderService reader, RegexScanner scanner)
    {
        var extractor = reader.FindExtractor(filePath);
        if (extractor == null)
        {
            Check(name, false, "không tìm thấy extractor cho định dạng này");
            return;
        }

        var blocks = extractor.Extract(filePath).ToList();
        string flatText = string.Join("\n", blocks.Select(b => b.Text));

        // Dựng finding giả lập giống ĐÚNG cách FileWatcherService làm ở Pass 1: 1 dòng/rule, offset = vị trí nhỏ nhất
        var findings = scanner.Scan(flatText).Matches
            .GroupBy(m => m.RuleId ?? m.PatternName)
            .Select(g => new FindingRecord(g.Key, g.Max(m => m.Score), g.Any(m => m.Validated) ? true : null,
                g.Any(m => m.Validated), g.Count(), $"offset:{g.Min(m => m.Index)}"))
            .ToList();

        var chunks = ChunkSelector.SelectChunks(extractor, filePath, Path.GetFileName(filePath), findings, tokenBudget: 2000);
        var masked = chunks.Select(c => Masker.Mask(c, scanner)).Where(c => c != null).Cast<ChunkResult>().ToList();
        string package = string.Join("\n---\n", masked.Select(c => $"[{c.Kind}/{c.Rule}] {c.Text}"));

        int huyChunk = chunks.Count - masked.Count;
        var leaked = canaries.Where(c => package.Contains(c, StringComparison.Ordinal)).ToList();

        bool ok = leaked.Count == 0 && package.Length > 0;
        string detail = ok
            ? $"{chunks.Count} chunk ({huyChunk} bị huỷ), {package.Length:N0} ký tự gói cuối — không canary nào lọt"
            : leaked.Count > 0
                ? $"LỘ {leaked.Count} canary: {string.Join(", ", leaked)}"
                : "gói rỗng (nghi ngờ mọi chunk đều bị huỷ, không có gì để gửi LLM)";
        Check(name, ok, detail);

        if (!ok)
        {
            _md.AppendLine("  Gói (đã mask, để soi lỗi):\n  ```\n  " + package.Replace("\n", "\n  ") + "\n  ```");
        }
    }

    private void Check(string name, bool ok, string detail)
    {
        if (!ok)
        {
            Failures++;
        }
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} — {detail}");
        _md.AppendLine($"- **{(ok ? "PASS" : "FAIL")}** {name} — {detail}");
    }

    public string Save()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !(dir.Name == "canary" && dir.Parent?.Name == "tools"))
        {
            dir = dir.Parent;
        }
        string folder = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"canary-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(file, $"# Test canary — {DateTime.Now:yyyy-MM-dd HH:mm}\n\nKết quả: " +
            (Failures == 0 ? "**TẤT CẢ PASS**" : $"**{Failures} FAIL**") + "\n" + _md);
        return file;
    }
}
