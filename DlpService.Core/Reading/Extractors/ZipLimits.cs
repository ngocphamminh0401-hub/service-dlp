// [Thay đổi] File mới (H4 — chặn zip bomb theo ngân sách, thay ExtractorHelpers.EnsureNotZipBomb). Trước đây chỉ chặn 1 phần > 200 MB:
// N phần × 199 MB vẫn qua, không giới hạn số phần / tỉ lệ nén, và docx/pptx nạp XML thành cây đối tượng (RAM ≈ 5–10 × XML) → 1 file
// đủ đẩy service vượt MemoryMax=512M → systemd kill → khởi động lại gặp lại đúng file → vòng crash, DLP ngừng hoạt động.
//
// Các lớp kiểm tra (không giải nén nội dung — chỉ tối đa 4 byte đầu mỗi phần):
//  1. End of Central Directory (EOCD): bản ghi 22 byte cuối file ZIP (+ bản ZIP64) cho biết SỐ PHẦN và KÍCH THƯỚC DANH MỤC → từ chối
//     trước khi .NET dựng object cho từng phần (ZIP hàng triệu phần chỉ tốn vài chục KB nhưng tốn hàng trăm MB RAM khi duyệt Entries).
//  2. Từng phần: kích thước giải nén KHAI BÁO trong danh mục (.NET ZipArchiveEntry.Open() không giải nén quá số này — xem ZipLimitsTests)
//     so với ngân sách, và tỉ lệ nén.
//  3. "Phần nội dung" (thứ parser XML sẽ nạp) xác định theo 4 BYTE ĐẦU, KHÔNG theo tên và KHÔNG theo tỉ lệ nén:
//     - không theo tên: OpenXml tìm phần chính qua relationships + content type, đặt tên "word/x.png" cho XML chính là lách được;
//     - không theo tỉ lệ: lưu XML ở chế độ "stored" (không nén) thì tỉ lệ luôn = 1 như ảnh (test phát hiện: cả Deflate "Fastest" của .NET
//       với bảng Huffman tĩnh cũng ra ≈ 1 × cho chữ) → 45 MB XML stored lọt ngân sách rồi bị nạp DOM ≈ 360 MB RAM.
//     Dùng đúng tiêu chí của parser XML: tài liệu XML chỉ có thể bắt đầu bằng BOM, '<' hoặc khoảng trắng (UTF-8, hoặc UTF-16 "<\0"/"\0<").
//     Ảnh/nhị phân (PNG 89 50, JPEG FF D8, OLE D0 CF, ZIP 50 4B...) luôn có byte đầu không hợp lệ với XML → parser không thể nạp → không tính.
//  4. Trần tổng cho MỌI phần, bất kể là gì.
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using System.IO.Compression;

namespace DlpService.Reading.Extractors;

/// <summary>Ngân sách giải nén cho 1 loại bộ đọc (đơn vị byte).</summary>
/// <param name="MaxEntries">Số phần tối đa.</param>
/// <param name="MaxCentralDirectoryBytes">Kích thước danh mục ZIP tối đa.</param>
/// <param name="MaxContentEntryBytes">1 phần nội dung (nén được ≥ 2 ×) tối đa.</param>
/// <param name="MaxSharedStringsBytes">Riêng xl/sharedStrings.xml (bộ đọc nạp hết vào RAM).</param>
/// <param name="MaxTotalContentBytes">Tổng các phần nội dung tối đa.</param>
/// <param name="MaxTotalAllBytes">Tổng mọi phần tối đa.</param>
/// <param name="MaxRatio">Tỉ lệ nén tối đa của 1 phần.</param>
/// <param name="RatioCheckMinBytes">Chỉ xét tỉ lệ với phần lớn hơn mức này.</param>
public sealed record ZipBudget(int MaxEntries, long MaxCentralDirectoryBytes, long MaxContentEntryBytes, long MaxSharedStringsBytes,
    long MaxTotalContentBytes, long MaxTotalAllBytes, double MaxRatio, long RatioCheckMinBytes);

/// <summary>File vượt giới hạn tài nguyên khi đọc — KHÔNG phải file hỏng. FileReaderService ghi nhận là "đọc chưa hết" (limit_exceeded)
/// thay vì bỏ qua. Kế thừa Exception (InvalidDataException là sealed; KHÔNG dùng IOException vì FileReaderService hiểu IOException là
/// "file đang bị khoá") — FileReaderService bắt riêng loại này trước các catch khác.</summary>
public sealed class ExtractionLimitException(string message) : Exception(message);

/// <summary>Kiểm tra ngân sách giải nén của file ZIP trước khi giao cho thư viện đọc.</summary>
public static class ZipLimits
{
    private const long MB = 1024 * 1024;
    private const int EocdSize = 22;
    private const uint EocdSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EocdSignature = 0x06064b50;

    // [Thay đổi] H4: theo đúng mẫu TikaNativeRuntime.Configure — Worker gán 1 lần lúc khởi động (trước khi tạo FileReaderService), extractor
    // (tạo bằng reflection, constructor không tham số) đọc ra. volatile: luồng xử lý file luôn thấy bản đã gán.
    private static volatile ZipBudget _dom = Build(new ZipLimitOptions()).Dom;
    private static volatile ZipBudget _streaming = Build(new ZipLimitOptions()).Streaming;

    /// <summary>Ngân sách cho docx/pptx (nạp DOM).</summary>
    public static ZipBudget Dom => _dom;

    /// <summary>Ngân sách cho xlsx (đọc theo hàng).</summary>
    public static ZipBudget Streaming => _streaming;

    /// <summary>Đọc cấu hình "Reading:Zip" (Worker gọi 1 lần lúc khởi động). Giá trị sai → mặc định + cảnh báo.</summary>
    public static void Configure(ZipLimitOptions options, ILogger logger)
    {
        foreach (var item in options.Validate())
        {
            logger.LogWarning("[Zip] Reading:Zip không hợp lệ, dùng mặc định: {item}", item);
        }
        var (dom, streaming) = Build(options);
        _dom = dom;
        _streaming = streaming;
        logger.LogInformation("[Zip] Ngân sách giải nén: docx/pptx {domEntry}/{domTotal} MB, xlsx {stEntry}/{stTotal} MB (sharedStrings {ss} MB), ≤ {entries} phần, tỉ lệ ≤ {ratio}×",
            options.DomMaxEntryMB, options.DomMaxTotalMB, options.StreamingMaxEntryMB, options.StreamingMaxTotalMB,
            options.StreamingMaxSharedStringsMB, options.MaxEntries, options.MaxCompressionRatio);
    }

    /// <summary>Tính 2 bộ ngân sách từ cấu hình (hàm thuần, không đổi trạng thái — test gọi thẳng được).</summary>
    public static (ZipBudget Dom, ZipBudget Streaming) Build(ZipLimitOptions o)
    {
        long cd = o.MaxCentralDirectoryMB * MB, all = o.MaxTotalAllEntriesMB * MB, ratioMin = o.RatioCheckMinMB * MB;
        long domEntry = o.DomMaxEntryMB * MB;
        var dom = new ZipBudget(o.MaxEntries, cd, domEntry, domEntry, o.DomMaxTotalMB * MB, all, o.MaxCompressionRatio, ratioMin);
        var streaming = new ZipBudget(o.MaxEntries, cd, o.StreamingMaxEntryMB * MB, o.StreamingMaxSharedStringsMB * MB,
            o.StreamingMaxTotalMB * MB, all, o.MaxCompressionRatio, ratioMin);
        return (dom, streaming);
    }

    /// <summary>Ném <see cref="ExtractionLimitException"/> nếu file ZIP vượt ngân sách. Không phải ZIP (VD .xls nhị phân cũ) → bỏ qua.
    /// Dùng ĐÚNG stream sẽ đưa cho thư viện đọc (tránh kiểm 1 file, đọc file khác nếu bị thay giữa 2 lần mở); trả lại vị trí 0.</summary>
    public static void EnsureWithin(Stream stream, ZipBudget budget)
    {
        stream.Position = 0;
        Span<byte> magic = stackalloc byte[2];
        bool isZip = stream.Read(magic) == 2 && magic[0] == (byte)'P' && magic[1] == (byte)'K';
        if (!isZip)
        {
            stream.Position = 0;
            return;
        }

        // Lớp 1: EOCD — chưa dựng object nào. Không đọc được EOCD (ZIP hỏng) → để ZipArchive tự báo lỗi "hỏng" như cũ.
        if (ReadDirectoryInfo(stream) is { } dir)
        {
            if (dir.Entries > budget.MaxEntries)
            {
                throw new ExtractionLimitException($"Vượt giới hạn: {dir.Entries:N0} phần trong file ZIP (> {budget.MaxEntries:N0})");
            }
            if (dir.DirectoryBytes > budget.MaxCentralDirectoryBytes)
            {
                throw new ExtractionLimitException($"Vượt giới hạn: danh mục ZIP {dir.DirectoryBytes / MB} MB (> {budget.MaxCentralDirectoryBytes / MB} MB)");
            }
        }

        // Lớp 2–4: từng phần (chỉ đọc danh mục, không giải nén)
        stream.Position = 0;
        long totalContent = 0, totalAll = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            Span<byte> head = stackalloc byte[4];
            foreach (var entry in archive.Entries)
            {
                long size = entry.Length, packed = Math.Max(1, entry.CompressedLength);
                double ratio = (double)size / packed;
                totalAll += size;

                if (size > budget.RatioCheckMinBytes && ratio > budget.MaxRatio)
                {
                    throw new ExtractionLimitException($"Nghi ngờ zip bomb: 1 phần nén {ratio:0}× (> {budget.MaxRatio:0}×)");
                }

                if (size > 0 && LooksLikeXml(entry, head)) // phần parser XML sẽ nạp — xét theo byte đầu (xem lớp 3 ở đầu file)
                {
                    bool sharedStrings = entry.FullName.EndsWith("sharedStrings.xml", StringComparison.OrdinalIgnoreCase);
                    long maxEntry = sharedStrings ? budget.MaxSharedStringsBytes : budget.MaxContentEntryBytes;
                    if (size > maxEntry)
                    {
                        throw new ExtractionLimitException($"Vượt giới hạn: 1 phần nội dung giải nén {size / MB} MB (> {maxEntry / MB} MB)");
                    }
                    totalContent += size;
                    if (totalContent > budget.MaxTotalContentBytes)
                    {
                        throw new ExtractionLimitException($"Vượt giới hạn: tổng nội dung giải nén > {budget.MaxTotalContentBytes / MB} MB");
                    }
                }

                if (totalAll > budget.MaxTotalAllBytes)
                {
                    throw new ExtractionLimitException($"Vượt giới hạn: tổng giải nén > {budget.MaxTotalAllBytes / MB} MB");
                }
            }
        }
        stream.Position = 0;
    }

    // [Thay đổi] H4: giải nén tối đa 4 byte đầu của 1 phần. Chưa đủ 4 byte (phần rất nhỏ) → coi như XML (an toàn: tính vào ngân sách).
    private static bool LooksLikeXml(ZipArchiveEntry entry, Span<byte> head)
    {
        int n = 0;
        using (var s = entry.Open())
        {
            int r;
            while (n < head.Length && (r = s.Read(head[n..])) > 0) n += r;
        }
        if (n < head.Length)
        {
            return true;
        }

        byte b0 = head[0], b1 = head[1];
        if (b0 == 0xEF && b1 == 0xBB) return true;                      // BOM UTF-8
        if ((b0 == 0xFF && b1 == 0xFE) || (b0 == 0xFE && b1 == 0xFF)) return true; // BOM UTF-16
        if (b0 == '<' || IsXmlSpace(b0)) return true;                  // UTF-8 / ASCII, và UTF-16 LE không BOM ("<\0")
        if (b0 == 0 && (b1 == '<' || IsXmlSpace(b1))) return true;     // UTF-16 BE không BOM ("\0<")
        return false;
    }

    private static bool IsXmlSpace(byte b) => b is 0x20 or 0x09 or 0x0A or 0x0D;

    /// <summary>Số phần + kích thước danh mục đọc từ EOCD (và bản ZIP64 nếu có).</summary>
    public readonly record struct DirectoryInfo(long Entries, long DirectoryBytes);

    /// <summary>Đọc EOCD ở cuối file ZIP. null nếu không tìm thấy (không phải ZIP / ZIP hỏng).</summary>
    public static DirectoryInfo? ReadDirectoryInfo(Stream stream)
    {
        long length = stream.Length;
        if (length < EocdSize)
        {
            return null;
        }

        // EOCD nằm trong 22 + 65.535 byte cuối (65.535 = độ dài tối đa của phần "ghi chú" ở đuôi file ZIP) → tìm chữ ký từ cuối lên
        int tail = (int)Math.Min(length, EocdSize + ushort.MaxValue);
        var buffer = new byte[tail];
        stream.Position = length - tail;
        stream.ReadExactly(buffer);

        for (int i = tail - EocdSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) != EocdSignature)
            {
                continue;
            }

            // EOCD: +10 tổng số phần (2 byte), +12 kích thước danh mục (4 byte)
            long entries = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 10));
            long dirBytes = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 12));

            // Giá trị "tràn" (0xFFFF / 0xFFFFFFFF) = số thật nằm ở bản ghi ZIP64: locator 20 byte ngay trước EOCD, +8 = vị trí bản ghi ZIP64
            if (entries == ushort.MaxValue || dirBytes == uint.MaxValue)
            {
                long eocdPos = length - tail + i;
                if (eocdPos < 20)
                {
                    return null;
                }
                Span<byte> locator = stackalloc byte[20];
                stream.Position = eocdPos - 20;
                stream.ReadExactly(locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
                {
                    return null;
                }
                long zip64Pos = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
                if (zip64Pos < 0 || zip64Pos > length - 56)
                {
                    return null;
                }
                Span<byte> rec = stackalloc byte[56];
                stream.Position = zip64Pos;
                stream.ReadExactly(rec);
                if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != Zip64EocdSignature)
                {
                    return null;
                }
                // ZIP64 EOCD: +32 tổng số phần (8 byte), +40 kích thước danh mục (8 byte). Ép về long: số khổng lồ → long.MaxValue (vẫn vượt ngưỡng)
                entries = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(rec[32..]), long.MaxValue);
                dirBytes = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(rec[40..]), long.MaxValue);
            }
            return new DirectoryInfo(entries, dirBytes);
        }
        return null;
    }
}
