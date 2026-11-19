// [Thay đổi] File mới (Benchmark T02): các khối dữ liệu giả lập — câu đệm trung tính, số CCCD/SĐT giả nhưng QUA validator, tên người giả,
// PNG nhiễu (cho PDF scan), ZIP tất định. Mọi giá trị sinh từ Random có seed cố định → chạy lại ra cùng byte. KHÔNG có dữ liệu thật.
using System.IO.Compression;
using System.Text;
using DlpService.Scanning;

namespace DlpBenchData;

internal static class Synth
{
    // Câu đệm trung tính — chép từ tools/eval/DlpSizeBench/Program.cs (FillerCandidates) để 2 bộ dữ liệu dùng chung "nền". Câu nào khớp
    // rule hoặc chứa từ khoá rule bị loại lúc chạy (FilterFiller), giống DlpSizeBench.
    private static readonly string[] FillerCandidates =
    [
        "Buổi sáng trời trong xanh, những đám mây trắng trôi chậm qua dãy núi phía xa.",
        "Hàng cây ven đường đã bắt đầu thay lá, tạo nên một khung cảnh vàng rực rất đẹp.",
        "Con sông nhỏ chảy hiền hòa qua cánh đồng lúa đang vào mùa chín.",
        "Người dân trong làng thường dậy sớm để tập thể dục và đi dạo quanh hồ.",
        "Món phở bò nóng hổi là lựa chọn quen thuộc cho bữa sáng của nhiều gia đình.",
        "Cuối tuần, công viên thành phố đông vui với các hoạt động thả diều và đạp xe.",
        "Mùa hè năm nay nắng nhiều, các bãi biển miền Trung đón rất đông du khách.",
        "Những con đường lát đá cổ kính mang lại cảm giác bình yên cho người đi bộ.",
        "Vườn hoa trước sân nở rộ, thu hút ong bướm bay lượn suốt cả buổi chiều.",
        "Tiếng chim hót líu lo hòa cùng làn gió nhẹ thổi qua rặng tre đầu ngõ.",
        "Chợ quê họp từ tờ mờ sáng với đủ loại rau xanh, trái cây tươi và bánh trái.",
        "Bầu trời đêm đầy sao khiến ai ngắm nhìn cũng cảm thấy thư thái.",
        "Những cơn mưa rào mùa hạ đến nhanh rồi tạnh, để lại không khí mát mẻ dễ chịu.",
        "Đồi chè xanh mướt trải dài tít tắp, nối tiếp nhau đến tận chân trời.",
        "Các em nhỏ vui đùa bên bờ suối, tiếng cười trong trẻo vang khắp xóm.",
        "Bát canh chua cá lóc thơm mùi rau ngổ là món ăn gợi nhớ vùng sông nước.",
        "Ngọn hải đăng đứng sừng sững trên mỏm đá, soi sáng cho những con thuyền về bến.",
        "Mùa thu Hà Nội có hương hoa sữa nồng nàn lan tỏa trên từng con phố nhỏ.",
        "Thảm cỏ xanh trên đồi được tưới mát mỗi sớm, trông như một tấm thảm khổng lồ.",
        "Những chiếc thuyền nan nhỏ lặng lẽ trôi trên mặt hồ phẳng lặng như gương.",
        "Bánh xèo giòn rụm ăn kèm rau sống và nước chấm chua ngọt rất hấp dẫn.",
        "Đàn cò trắng bay về tổ khi hoàng hôn buông xuống trên cánh đồng rộng.",
        "Khu rừng nguyên sinh có nhiều loài cây cổ thụ cao lớn và tán lá rậm rạp.",
        "Mỗi dịp Tết đến, phố phường lại rực rỡ sắc đỏ của đèn lồng và câu đối.",
        "Những ngôi nhà mái ngói rêu phong nằm nép mình dưới bóng cây đa cổ thụ.",
        "Gió biển thổi lồng lộng, mang theo vị mặn mòi và tiếng sóng vỗ rì rào.",
        "Ly cà phê sữa đá buổi sáng giúp nhiều người khởi đầu ngày mới đầy năng lượng.",
        "Hoa sen nở rộ trong đầm, tỏa hương thơm dịu nhẹ suốt mùa hè.",
        "Con đường đèo quanh co uốn lượn giữa những vách núi đá vôi hùng vĩ.",
        "Ruộng bậc thang mùa nước đổ lấp lánh dưới ánh nắng như những tấm gương.",
        "Bên hiên nhà, chú mèo lười biếng nằm sưởi nắng và lim dim đôi mắt.",
        "Những quả xoài chín vàng treo lủng lẳng trên cành, tỏa mùi thơm ngọt ngào.",
        "Sương mù giăng kín thung lũng vào sáng sớm, tạo nên vẻ huyền ảo khó tả.",
        "Dòng thác trắng xóa đổ xuống từ độ cao lớn, bọt nước bắn tung tóe.",
        "Buổi chiều, người dân tụ tập bên bờ sông để câu cá và trò chuyện rôm rả.",
        "Chiếc cầu tre lắt lẻo bắc qua con kênh nhỏ là kỷ niệm tuổi thơ của nhiều người.",
        "Những hạt mưa phùn mùa xuân lất phất bay, làm cây cối đâm chồi nảy lộc.",
        "Bánh chưng xanh gói bằng lá dong là món ăn truyền thống không thể thiếu ngày Tết.",
        "Tiếng đàn bầu ngân nga trong đêm trăng làm lòng người thêm sâu lắng.",
        "Vịnh biển với hàng nghìn hòn đảo đá vôi là danh thắng nổi tiếng thế giới."
    ];

    /// <summary>Câu đệm hợp lệ: không chỗ khớp (kể cả điểm thấp), không chứa từ khoá rule, không có chữ số.</summary>
    public static List<string> FilterFiller(RegexScanner strictScanner, IReadOnlyList<string> keywords)
    {
        return FillerCandidates
            .Where(s => !s.Any(char.IsDigit)
                        && !keywords.Any(k => ContainsWord(s, k))
                        && strictScanner.Scan(s).Matches.Count == 0)
            .ToList();
    }

    // Từ khoá có xuất hiện như 1 "từ" (không dính chữ cái/chữ số 2 bên) trong câu không — giống DlpSizeBench.ContainsWord
    public static bool ContainsWord(string text, string word)
    {
        int idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool left = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            int end = idx + word.Length;
            bool right = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (left && right) return true;
            idx++;
        }
        return false;
    }

    /// <summary>Các dòng văn bản đệm đến khi đạt ~targetChars ký tự (tính cả '\n').</summary>
    public static List<string> FillerLines(List<string> filler, int targetChars, Random rand, int sentencesPerLine = 3)
    {
        var lines = new List<string>();
        int total = 0;
        var sb = new StringBuilder();
        while (total < targetChars)
        {
            sb.Clear();
            for (int i = 0; i < sentencesPerLine; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(filler[rand.Next(filler.Count)]);
            }
            lines.Add(sb.ToString());
            total += sb.Length + 1;
        }
        return lines;
    }

    // ---------------- Số giả qua validator (dùng chung luật với CccdValidator / PhoneValidator) ----------------

    private static readonly string[] Provinces = ["001", "031", "079", "048", "092", "036", "038", "040", "056", "075"];
    private static readonly string[] MobilePrefixes = ["032", "035", "038", "086", "090", "093", "097", "098", "070", "081", "084", "088"];

    /// <summary>CCCD 12 số: mã tỉnh hợp lệ + chữ số giới tính/thế kỷ 0–3 + 2 số năm sinh (≤ năm nay) + 6 số ngẫu nhiên.</summary>
    public static string Cccd(Random r)
    {
        int genderCentury = r.Next(0, 4);
        int yy = genderCentury <= 1 ? r.Next(50, 100) : r.Next(0, 6); // 1950–1999 hoặc 2000–2005
        return $"{Provinces[r.Next(Provinces.Length)]}{genderCentury}{yy:00}{r.Next(0, 1_000_000):000000}";
    }

    /// <summary>SĐT di động 10 số với đầu số hợp lệ; 7 số thuê bao tránh dãy đều/tăng/giảm (PhoneValidator.LooksFake).</summary>
    public static string Phone(Random r)
    {
        while (true)
        {
            string sub = r.Next(1_000_000, 10_000_000).ToString("0000000");
            if (sub.Distinct().Count() >= 3) return MobilePrefixes[r.Next(MobilePrefixes.Length)] + sub;
        }
    }

    /// <summary>Dãy n chữ số (chữ số đầu khác 0) — "số lạ" cho CountUnstructuredNumericHits.</summary>
    public static string Digits(Random r, int n)
    {
        var sb = new StringBuilder(n);
        sb.Append((char)('1' + r.Next(9)));
        for (int i = 1; i < n; i++) sb.Append((char)('0' + r.Next(10)));
        return sb.ToString();
    }

    private static readonly string[] Ho = ["Nguyễn", "Trần", "Lê", "Phạm", "Hoàng", "Vũ", "Đặng", "Bùi"];
    private static readonly string[] Dem = ["Văn", "Thị", "Minh", "Thu", "Đức", "Ngọc", "Quang", "Thanh"];
    private static readonly string[] Ten = ["An", "Bình", "Châu", "Dũng", "Hà", "Khoa", "Lan", "Nam", "Phúc", "Trang"];

    public static string Name(Random r) => $"{Ho[r.Next(Ho.Length)]} {Dem[r.Next(Dem.Length)]} {Ten[r.Next(Ten.Length)]}";

    // ---------------- ZIP tất định ----------------

    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Ghi lại 1 file ZIP (docx/pptx...) với thời gian mọi entry cố định — ZipArchive mặc định ghi DateTime.Now nên mỗi lần sinh
    /// ra 1 sha256 khác. Nội dung và thứ tự entry giữ nguyên.</summary>
    public static byte[] Deterministic(byte[] zipBytes)
    {
        using var input = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                var e = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                e.LastWriteTime = FixedTime;
                using var src = entry.Open();
                using var dst = e.Open();
                src.CopyTo(dst);
            }
        }
        return output.ToArray();
    }

    // ---------------- PNG nhiễu xám (PDF scan) ----------------

    /// <summary>PNG 8-bit xám width×height điểm nhiễu ngẫu nhiên — tự ghi các chunk IHDR/IDAT/IEND (IDAT nén zlib, CRC32 tự tính).</summary>
    public static byte[] NoisePng(int width, int height, Random r)
    {
        var raw = new byte[(width + 1) * height]; // mỗi hàng: 1 byte filter (0 = None) + width byte điểm ảnh
        for (int y = 0; y < height; y++)
        {
            int row = y * (width + 1);
            for (int x = 0; x < width; x++) raw[row + 1 + x] = (byte)(r.Next(12) == 0 ? r.Next(256) : 230); // [Thay đổi] nền sáng, chấm ngẫu nhiên THƯA (1/12, trước 1/4) → nén ~3 lần tốt hơn
        }
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            idat = ms.ToArray();
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, width);
        WriteBe(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 0;  // màu xám
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", idat);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = new byte[4];
        WriteBe(crc, 0, (int)Crc32(typeBytes, data));
        s.Write(crc);
    }

    private static void WriteBe(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static uint[]? _crcTable;

    // CRC32 chuẩn PNG (đa thức 0xEDB88320) trên type + data
    private static uint Crc32(byte[] a, byte[] b)
    {
        _crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        uint crc = 0xFFFFFFFFu;
        foreach (byte x in a.Concat(b)) crc = _crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
