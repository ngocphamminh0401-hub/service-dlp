// [Thay đổi] File mới (mở rộng định dạng - Bước 13): test bằng file độc hại mẫu.
// Tự tạo: zip bomb (.docx/.xlsx/.pptx), file .docx/.eml lồng cấu trúc quá sâu, file .txt quá khổ, và 1 file bình
// thường để đối chứng (không được bị chặn nhầm). Chạy qua FileReaderService THẬT (không mock) — xác nhận mỗi
// trường hợp bị từ chối đúng cách (Success=false, có lý do) và xử lý XONG NHANH (không treo).
//
// Cách chạy: dotnet run --project tools/safety/DlpExtractorSafety
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DlpService.Reading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

string work = Path.Combine(Path.GetTempPath(), "dlp-extractor-safety");
if (Directory.Exists(work))
{
    Directory.Delete(work, true);
}
Directory.CreateDirectory(work);

var reader = new FileReaderService();
var report = new SafetyReport();

// Ngưỡng "xử lý xong đủ nhanh" cho mọi kịch bản trong bộ test này — timeout thật trong FileReaderService là 30s;
// mọi trường hợp ở đây phải bị chặn SỚM HƠN NHIỀU (không cần đợi tới timeout mới phát hiện ra là độc hại).
var fastEnough = TimeSpan.FromSeconds(10);

report.Section("1. Zip bomb (.docx/.xlsx/.pptx) — 1 phần bên trong khai giải nén ra ~250MB");
foreach (var ext in new[] { ".docx", ".xlsx", ".pptx" })
{
    string path = Samples.ZipBomb(work, ext);
    report.RunRejected($"Zip bomb {ext}", reader, path, fastEnough);
}

report.Section("2. Lồng cấu trúc quá sâu (30 lớp, giới hạn cho phép là 20)");
report.RunRejected(".docx lồng 30 lớp content control", reader, Samples.DeeplyNestedDocx(work, depth: 30), fastEnough);
report.RunRejected(".eml lồng 30 lớp multipart", reader, Samples.DeeplyNestedEml(work, depth: 30), fastEnough);

report.Section("3. File quá khổ (vượt MaxFileSizeBytes)");
report.RunRejected(".txt 11MB (giới hạn .txt là 10MB)", reader, Samples.OversizedTxt(work, megabytes: 11), fastEnough);

report.Section("4. Đối chứng — không được chặn nhầm file/cấu trúc bình thường");
report.RunAccepted(".docx lồng 5 lớp (trong giới hạn 20) vẫn đọc được", reader, Samples.DeeplyNestedDocx(work, depth: 5), fastEnough);
report.RunAccepted(".csv 5MB bình thường vẫn đọc được", reader, Samples.NormalLargeCsv(work, megabytes: 5), fastEnough);

string resultFile = report.Save();
Console.WriteLine();
Console.WriteLine(report.Failures == 0 ? "== TẤT CẢ PASS ==" : $"== {report.Failures} FAIL ==");
Console.WriteLine($"Báo cáo: {resultFile}");
Directory.Delete(work, true);
return report.Failures == 0 ? 0 : 1;

/// <summary>Tạo các file mẫu độc hại/đối chứng.</summary>
internal static class Samples
{
    /// <summary>File ZIP mang đúng đuôi định dạng OOXML nhưng có 1 entry toàn số 0 (nén cực mạnh), giải nén ra
    /// ~250MB — vượt ngưỡng 200MB trong ExtractorHelpers.EnsureNotZipBomb. Không cần là .docx/.xlsx/.pptx hợp lệ
    /// thật sự: EnsureNotZipBomb chạy TRƯỚC khi OpenXml/ExcelDataReader mở file, nên chỉ cần đúng là file ZIP.</summary>
    public static string ZipBomb(string dir, string extension)
    {
        string path = Path.Combine(dir, "zipbomb" + extension);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("bomb.xml", CompressionLevel.Optimal);
            using var stream = entry.Open();
            byte[] buffer = new byte[1024 * 1024]; // 1MB toàn số 0 — nén gần như về 0 byte, ghi rất nhanh
            for (int i = 0; i < 250; i++) // 250 x 1MB = ~250MB sau giải nén
            {
                stream.Write(buffer);
            }
        }
        return path;
    }

    /// <summary>File .docx hợp lệ, thân tài liệu lồng "depth" lớp content control (SdtBlock), trong cùng đến
    /// đoạn văn chứa chữ. Dùng để test giới hạn độ sâu của DocxExtractor.AppendBlocks.</summary>
    public static string DeeplyNestedDocx(string dir, int depth)
    {
        string path = Path.Combine(dir, $"nested-{depth}.docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(BuildNested(depth)));
            main.Document.Save();
        }
        return path;

        static OpenXmlElement BuildNested(int remaining) => remaining <= 0
            ? new W.Paragraph(new W.Run(new W.Text("Số CCCD 012345678901")))
            : new W.SdtBlock(new W.SdtContentBlock(BuildNested(remaining - 1)));
    }

    /// <summary>File .eml hợp lệ, thân email lồng "depth" lớp multipart/mixed, trong cùng là 1 phần text/plain.
    /// Dùng để test giới hạn độ sâu của EmlMimeParser.</summary>
    public static string DeeplyNestedEml(string dir, int depth)
    {
        string path = Path.Combine(dir, $"nested-{depth}.eml");
        string body = BuildNested(depth);
        string raw = "Subject: test\r\n" + body;
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(raw));
        return path;

        static string BuildNested(int remaining)
        {
            if (remaining <= 0)
            {
                return "Content-Type: text/plain; charset=utf-8\r\n\r\nSo CCCD 012345678901\r\n";
            }
            string boundary = "B" + remaining;
            return $"Content-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n--{boundary}\r\n{BuildNested(remaining - 1)}\r\n--{boundary}--\r\n";
        }
    }

    /// <summary>File .txt thuần, toàn chữ "A", đúng "megabytes" MB — vượt MaxFileSizeBytes của TxtExtractor (10MB).</summary>
    public static string OversizedTxt(string dir, int megabytes)
    {
        string path = Path.Combine(dir, "oversized.txt");
        using var f = File.Create(path);
        byte[] buffer = new byte[1024 * 1024];
        Array.Fill(buffer, (byte)'A');
        for (int i = 0; i < megabytes; i++)
        {
            f.Write(buffer);
        }
        return path;
    }

    /// <summary>File .csv bình thường (không độc hại) nhưng khá lớn — đối chứng để chắc chắn các giới hạn mới
    /// không chặn nhầm file hợp lệ.</summary>
    public static string NormalLargeCsv(string dir, int megabytes)
    {
        string path = Path.Combine(dir, "normal.csv");
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        writer.WriteLine("Ho ten,So CCCD");
        string row = "Nguyen Van A,012345678901";
        long targetBytes = (long)megabytes * 1024 * 1024;
        long written = 0;
        while (written < targetBytes)
        {
            writer.WriteLine(row);
            written += row.Length + 2;
        }
        return path;
    }
}

/// <summary>In PASS/FAIL ra console và lưu báo cáo Markdown vào tools/safety/results/.</summary>
internal sealed class SafetyReport
{
    private readonly StringBuilder _md = new();

    public int Failures { get; private set; }

    public void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
        _md.AppendLine().AppendLine("## " + title).AppendLine();
    }

    /// <summary>Kỳ vọng file BỊ TỪ CHỐI (Success=false) và xong trong vòng "maxDuration".</summary>
    public void RunRejected(string name, FileReaderService reader, string filePath, TimeSpan maxDuration)
    {
        var sw = Stopwatch.StartNew();
        var result = reader.TryReadFile(filePath);
        sw.Stop();

        bool ok = !result.Success && sw.Elapsed <= maxDuration;
        Check(name, ok, $"Success={result.Success}, lý do=\"{result.ErrorMessage}\", mất {sw.Elapsed.TotalSeconds:0.00}s (giới hạn {maxDuration.TotalSeconds:0}s)");
    }

    /// <summary>Kỳ vọng file ĐỌC ĐƯỢC (Success=true) và xong trong vòng "maxDuration" — dùng cho ca đối chứng.</summary>
    public void RunAccepted(string name, FileReaderService reader, string filePath, TimeSpan maxDuration)
    {
        var sw = Stopwatch.StartNew();
        var result = reader.TryReadFile(filePath);
        sw.Stop();

        bool ok = result.Success && sw.Elapsed <= maxDuration;
        Check(name, ok, $"Success={result.Success}, lý do=\"{result.ErrorMessage}\", mất {sw.Elapsed.TotalSeconds:0.00}s (giới hạn {maxDuration.TotalSeconds:0}s)");
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
        while (dir != null && !(dir.Name == "safety" && dir.Parent?.Name == "tools"))
        {
            dir = dir.Parent;
        }
        string folder = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"safety-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(file, $"# Test file độc hại mẫu — {DateTime.Now:yyyy-MM-dd HH:mm}\n\nKết quả: " +
            (Failures == 0 ? "**TẤT CẢ PASS**" : $"**{Failures} FAIL**") + "\n" + _md);
        return file;
    }
}
