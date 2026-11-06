// [Thay đổi] File mới: đọc tham số dòng lệnh của công cụ baseline.
namespace DlpBaseline;

/// <summary>Tham số dòng lệnh: thư mục rawdata, giới hạn số mẫu, định dạng cần đo, thư mục kết quả.</summary>
internal sealed class Options
{
    /// <summary>Thư mục chứa các thư mục con "domain__label", mỗi thư mục có _metadata.jsonl.</summary>
    public string RawdataFolder { get; private set; } = @"D:\dlp-classifier\rawdata";

    /// <summary>Chỉ lấy tối đa N mẫu (chia đều cho các thư mục con) — để chạy thử nhanh. Null = lấy hết.</summary>
    public int? Limit { get; private set; }

    /// <summary>Định dạng cần đo: "docx", "pdf" hoặc cả hai.</summary>
    public List<string> Formats { get; private set; } = ["docx", "pdf"];

    /// <summary>Nơi ghi file CSV + báo cáo Markdown.</summary>
    public string OutFolder { get; private set; } = Path.Combine("tools", "eval", "results");

    // [Thay đổi] Công thức Go: ngưỡng loại chỗ khớp điểm thấp truyền cho RegexScanner (0 = không loại, để đo riêng tác
    // động của công thức điểm mới; mặc định 0,60 như service)
    /// <summary>Ngưỡng MinConfidence của RegexScanner.</summary>
    public double MinConfidence { get; private set; } = DlpService.Scanning.CandidateScorer.DefaultMinConfidence;

    /// <summary>Đọc tham số; trả null nếu sai cú pháp hoặc người dùng xin trợ giúp (đã in hướng dẫn).</summary>
    public static Options? Parse(string[] args)
    {
        var options = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            bool hasValue = i + 1 < args.Length;

            if (arg == "--limit" && hasValue && int.TryParse(args[i + 1], out int limit) && limit > 0)
            {
                options.Limit = limit;
                i++;
            }
            else if (arg == "--format" && hasValue)
            {
                options.Formats = args[++i].ToLowerInvariant() switch
                {
                    "docx" => ["docx"],
                    "pdf" => ["pdf"],
                    "both" => ["docx", "pdf"],
                    _ => []
                };
                if (options.Formats.Count == 0)
                {
                    PrintUsage();
                    return null;
                }
            }
            else if (arg == "--out" && hasValue)
            {
                options.OutFolder = args[++i];
            }
            // [Thay đổi] Công thức Go: --min-confidence 0..1 (dấu chấm thập phân, VD 0.60)
            else if (arg == "--min-confidence" && hasValue
                && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double min)
                && min is >= 0 and <= 1)
            {
                options.MinConfidence = min;
                i++;
            }
            else if (arg is "-h" or "--help" || arg.StartsWith("--", StringComparison.Ordinal))
            {
                PrintUsage();
                return null;
            }
            else
            {
                options.RawdataFolder = arg;
            }
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Cách dùng: DlpBaseline [thư mục rawdata] [--limit N] [--format docx|pdf|both] [--out thư mục] [--min-confidence X]
              thư mục rawdata   mặc định D:\dlp-classifier\rawdata
              --limit N         chỉ lấy N mẫu, chia đều theo thư mục con (chạy thử nhanh)
              --format          định dạng cần đo, mặc định both
              --out             nơi ghi kết quả, mặc định tools\eval\results
              --min-confidence  ngưỡng loại chỗ khớp điểm thấp 0..1, mặc định 0.60 (0 = không loại)
            """);
    }
}
