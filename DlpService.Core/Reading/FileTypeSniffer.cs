// [Thay đổi] File mới (magic bytes cho bộ đọc .NET): nhận diện định dạng THẬT của file qua các byte đầu ("magic bytes" — chữ ký cố
// định mà mỗi định dạng ghi ở đầu file), KHÔNG tin vào đuôi file. Lý do: trước đây FileReaderService chọn bộ đọc chỉ theo đuôi →
// "bangluong.docx" đổi tên thành ".txt" bị TxtExtractor đọc byte zip thành chữ rác, không khớp regex nào và bị lưu là "sạch / Public"
// (lỗ để né DLP). Lớp này chỉ NHẬN DIỆN; việc chọn bộ đọc theo kết quả nằm ở FileReaderService.TryReadFile.
using System.IO.Compression;
using System.Text;
using DlpService.Reading.Extractors;

namespace DlpService.Reading;

/// <summary>Định dạng thật của file, nhận ra từ nội dung (không phải từ đuôi).</summary>
public enum DetectedFormat
{
    /// <summary>File rỗng — không có gì để nhận diện.</summary>
    Empty,

    /// <summary>Văn bản thuần (có BOM, hoặc không có byte 0 ở 8 KB đầu): txt, csv, eml... đều thuộc nhóm này.</summary>
    Text,

    /// <summary>Nhị phân nhưng không khớp chữ ký nào đã biết (ảnh, exe, zip nén thường...).</summary>
    UnknownBinary,

    /// <summary>PDF (%PDF).</summary>
    Pdf,

    /// <summary>RTF ({\rtf).</summary>
    Rtf,

    /// <summary>Word 2007+ (zip có word/document.xml).</summary>
    Docx,

    /// <summary>Excel 2007+ (zip có xl/workbook.xml).</summary>
    Xlsx,

    /// <summary>PowerPoint 2007+ (zip có ppt/presentation.xml).</summary>
    Pptx,

    /// <summary>File zip khác (zip thường, hoặc zip hỏng không đọc được danh mục).</summary>
    OtherZip,

    /// <summary>Word 97-2003 (OLE có luồng "WordDocument").</summary>
    Doc,

    /// <summary>Excel 97-2003 (OLE có luồng "Workbook" hoặc "Book").</summary>
    Xls,

    /// <summary>PowerPoint 97-2003 (OLE có luồng "PowerPoint Document").</summary>
    Ppt,

    /// <summary>File OLE khác: msg, file Office 2007+ CÓ MẬT KHẨU (bọc trong OLE), hoặc OLE hỏng.</summary>
    OtherOle
}

/// <summary>Nhận diện định dạng thật của file bằng magic bytes.</summary>
public static class FileTypeSniffer
{
    // Số byte đọc ở đầu file: đủ cho mọi chữ ký + kiểm tra byte 0 trong 8 KB đầu để phân biệt văn bản với nhị phân (giống
    // TikaNativeExtractor.IsLegacyEncodedText). Mọi chữ ký đều so ĐÚNG Ở ĐẦU FILE: tìm "%PDF-" lùi sâu hơn (chuẩn PDF cho phép) sẽ bắt
    // nhầm file .txt có ghi chú "%PDF-1.4 là..." ở dòng đầu → đẩy sang bộ đọc PDF → đọc lỗi, mất luôn nội dung văn bản.
    private const int HeaderBytes = 8192;

    private static readonly byte[] ZipSignature = [(byte)'P', (byte)'K', 0x03, 0x04];
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();
    private static readonly byte[] RtfSignature = @"{\rtf"u8.ToArray();
    private static readonly byte[][] TextBoms = [[0xEF, 0xBB, 0xBF], [0xFF, 0xFE], [0xFE, 0xFF]]; // UTF-8, UTF-16 LE, UTF-16 BE

    /// <summary>Nhận diện định dạng thật của file.</summary>
    /// <param name="filePath">Đường dẫn file.</param>
    /// <exception cref="IOException">File đang bị khoá (nơi gọi xử lý như lỗi đọc thường).</exception>
    /// <exception cref="UnauthorizedAccessException">Không có quyền đọc.</exception>
    public static DetectedFormat Detect(string filePath)
    {
        using var stream = ExtractorHelpers.OpenShared(filePath);
        return Detect(stream);
    }

    /// <summary>Nhận diện định dạng từ 1 stream đọc được và tua được (seekable); không đóng stream.</summary>
    internal static DetectedFormat Detect(Stream stream)
    {
        var header = new byte[HeaderBytes];
        int length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        var head = header.AsSpan(0, length);

        if (head.IsEmpty)
        {
            return DetectedFormat.Empty;
        }
        if (head.StartsWith(ZipSignature))
        {
            return DetectZipKind(stream);
        }
        if (head.StartsWith(OleSignature))
        {
            return DetectOleKind(stream);
        }
        if (head.StartsWith(PdfSignature))
        {
            return DetectedFormat.Pdf;
        }
        if (head.StartsWith(RtfSignature))
        {
            return DetectedFormat.Rtf;
        }
        foreach (var bom in TextBoms)
        {
            if (head.StartsWith(bom))
            {
                return DetectedFormat.Text; // kiểm tra BOM TRƯỚC byte 0: UTF-16 có BOM chứa rất nhiều byte 0 nhưng vẫn là văn bản
            }
        }
        return head.Contains((byte)0) ? DetectedFormat.UnknownBinary : DetectedFormat.Text;
    }

    /// <summary>Đuôi chuẩn của 1 định dạng (".docx", ".pdf"...); null với Empty/Text/UnknownBinary/OtherZip/OtherOle — các nhóm
    /// này không ứng với đúng 1 bộ đọc nào.</summary>
    public static string? CanonicalExtension(DetectedFormat format) => format switch
    {
        DetectedFormat.Pdf => ".pdf",
        DetectedFormat.Rtf => ".rtf",
        DetectedFormat.Docx => ".docx",
        DetectedFormat.Xlsx => ".xlsx",
        DetectedFormat.Pptx => ".pptx",
        DetectedFormat.Doc => ".doc",
        DetectedFormat.Xls => ".xls",
        DetectedFormat.Ppt => ".ppt",
        _ => null
    };

    // Zip: docx/xlsx/pptx đều là zip — phân biệt bằng phần chính bên trong. Chỉ đọc DANH MỤC cuối file zip (central directory),
    // không giải nén gì. Tên phần trong gói Office không phân biệt hoa thường (chuẩn OPC).
    private static DetectedFormat DetectZipKind(Stream stream)
    {
        try
        {
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase)) return DetectedFormat.Docx;
                if (entry.FullName.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase)) return DetectedFormat.Xlsx;
                if (entry.FullName.Equals("ppt/presentation.xml", StringComparison.OrdinalIgnoreCase)) return DetectedFormat.Pptx;
            }
            return DetectedFormat.OtherZip;
        }
        catch (InvalidDataException)
        {
            return DetectedFormat.OtherZip; // zip hỏng/cụt: không đoán được loại con, để bộ đọc theo đuôi tự báo lỗi
        }
    }

    // OLE (Compound File Binary — "hệ thống file thu nhỏ" bên trong .doc/.xls/.ppt): phân biệt bằng TÊN LUỒNG trong thư mục OLE.
    // Cấu trúc header (chuẩn MS-CFB): offset 30 = số mũ cỡ sector (9 → 512 byte, 12 → 4096 byte); offset 48 = sector đầu của thư mục;
    // offset 76 = 109 số hiệu sector đầu tiên của bảng FAT (bảng "sector này nối tiếp sector nào"). Mỗi mục thư mục dài 128 byte:
    // 64 byte tên UTF-16 + 2 byte độ dài tên. Chỉ đọc 109 sector FAT đầu (đủ cho file tới ~7 MB với sector 512 byte; file lớn hơn mà
    // thư mục nằm ngoài vùng đó thì trả OtherOle — bộ đọc theo đuôi vẫn được thử như cũ).
    private static DetectedFormat DetectOleKind(Stream stream)
    {
        const uint EndOfChain = 0xFFFFFFFE;
        const int MaxDirectorySectors = 256; // chặn vòng lặp vô hạn khi chuỗi sector bị hỏng/cố tình vòng tròn

        try
        {
            var reader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: true);
            stream.Position = 30;
            int sectorShift = reader.ReadUInt16();
            if (sectorShift is not (9 or 12))
            {
                return DetectedFormat.OtherOle;
            }
            int sectorSize = 1 << sectorShift;
            stream.Position = 44;
            uint fatSectorCount = reader.ReadUInt32();
            uint firstDirSector = reader.ReadUInt32();

            // Bảng FAT: ghép từ tối đa 109 sector FAT liệt kê ngay trong header
            stream.Position = 76;
            var fatSectors = new List<uint>();
            for (int i = 0; i < 109 && i < fatSectorCount; i++)
            {
                fatSectors.Add(reader.ReadUInt32());
            }
            var fat = new List<uint>();
            foreach (uint s in fatSectors)
            {
                long offset = (s + 1L) * sectorSize;
                if (offset + sectorSize > stream.Length) break;
                stream.Position = offset;
                for (int i = 0; i < sectorSize / 4; i++) fat.Add(reader.ReadUInt32());
            }

            // Duyệt chuỗi sector của thư mục, đọc tên từng mục
            var names = new HashSet<string>(StringComparer.Ordinal);
            uint sector = firstDirSector;
            for (int n = 0; n < MaxDirectorySectors && sector != EndOfChain; n++)
            {
                long offset = (sector + 1L) * sectorSize;
                if (offset + sectorSize > stream.Length) break;
                for (int e = 0; e < sectorSize / 128; e++)
                {
                    stream.Position = offset + e * 128;
                    byte[] rawName = reader.ReadBytes(64);
                    int nameBytes = reader.ReadUInt16();
                    if (nameBytes is >= 2 and <= 64)
                    {
                        names.Add(Encoding.Unicode.GetString(rawName, 0, nameBytes - 2)); // bỏ ký tự kết thúc chuỗi
                    }
                }
                if (sector >= fat.Count) break;
                sector = fat[(int)sector];
            }

            if (names.Contains("WordDocument")) return DetectedFormat.Doc;
            if (names.Contains("Workbook") || names.Contains("Book")) return DetectedFormat.Xls;
            if (names.Contains("PowerPoint Document")) return DetectedFormat.Ppt;
            return DetectedFormat.OtherOle;
        }
        catch (EndOfStreamException)
        {
            return DetectedFormat.OtherOle; // file OLE bị cụt
        }
    }
}
