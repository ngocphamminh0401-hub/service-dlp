// [Thay đổi] File mới (M6 — characterization / "golden master" test): chụp lại output của EmlExtractor BẢN CŨ trên bộ EmlSamples, lưu thành
// file golden trong repo (DlpService.Tests/Reading/Golden/Eml/*.txt). Sau khi viết lại bộ phân tích MIME (parse theo vị trí, giải mã lười,
// quét attachment), phần nội dung KHÔNG thuộc attachment phải GIỐNG HỆT golden — kể cả các hành vi "lạ" của bản cũ (VD thiếu
// "--boundary--" cuối thì phần cuối bị bỏ). Khối của attachment (ContextPath bắt đầu "Đính kèm:") và khối Unreadable là PHẦN MỚI của M6,
// được lọc ra trước khi so và kiểm riêng ở EmlAttachmentTests.
// Tạo lại golden (chỉ khi CỐ Ý đổi hành vi): DLP_WRITE_GOLDEN=1 dotnet test --filter EmlCharacterizationTests
using System.Runtime.CompilerServices;
using System.Text;
using DlpService.Reading;
using DlpService.Reading.Extractors;

namespace DlpService.Tests.Reading;

public sealed class EmlCharacterizationTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "dlp-eml-golden-" + Guid.NewGuid().ToString("N"));

    public EmlCharacterizationTests()
    {
        Directory.CreateDirectory(_work);
        _ = new FileReaderService(); // đăng ký CodePagesEncodingProvider + nạp extractor như service thật
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
    }

    private static string GoldenDir([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", "Eml");

    public static TheoryData<string> Names => new(EmlSamples.All().Keys);

    // 1 khối / dòng: Kind | ContextPath nối bằng " > " | Text (\r \n hiện thành \\r \\n để thấy rõ)
    internal static string Serialize(IEnumerable<ContentBlock> blocks) => string.Join("\n", blocks
        .Where(b => b.Kind != BlockKind.Unreadable && !(b.ContextPath.Count > 0 && b.ContextPath[0].StartsWith("Đính kèm:", StringComparison.Ordinal)))
        .Select(b => $"{b.Kind} | {string.Join(" > ", b.ContextPath)} | {b.Text.Replace("\r", "\\r").Replace("\n", "\\n")}"));

    internal static List<ContentBlock> Extract(FileReaderService reader, string path) => reader.FindExtractor(path)!.Extract(path).ToList();

    [Theory]
    [MemberData(nameof(Names))]
    public void Noi_dung_ngoai_attachment_giong_het_ban_cu(string name)
    {
        string path = Path.Combine(_work, name + ".eml");
        File.WriteAllBytes(path, EmlSamples.All()[name]);
        string actual = Serialize(Extract(new FileReaderService(), path));

        string golden = Path.Combine(GoldenDir(), name + ".txt");
        if (Environment.GetEnvironmentVariable("DLP_WRITE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenDir());
            File.WriteAllText(golden, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(golden), $"thiếu golden {golden} — chạy với DLP_WRITE_GOLDEN=1 trên bản cũ");
        Assert.Equal(File.ReadAllText(golden, Encoding.UTF8), actual);
    }
}
