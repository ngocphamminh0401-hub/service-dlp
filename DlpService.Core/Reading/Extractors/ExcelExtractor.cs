// [Thay đổi] File mới (mở rộng định dạng - Bước 3): trích xuất nội dung ô từ file Excel .xlsx/.xls bằng ExcelDataReader
// [Thay đổi] Streaming + giữ cấu trúc (Bước 6): Extract() yield return từng ContentBlock. Mỗi sheet → 1
// SectionMarker (ContextPath mang tên sheet), dòng đầu có dữ liệu → TableHeader, các dòng sau → TableRow kèm
// ColumnNames — tên cột được RESET lại mỗi khi sang sheet mới (sheet sau không dùng nhầm cột của sheet trước).
using System.Globalization;
using ExcelDataReader;

namespace DlpService.Reading.Extractors;

/// <summary>Đọc file .xlsx và .xls (Excel 97-2003): mọi sheet (kể cả sheet ẩn), mỗi hàng 1 dòng, các ô nối " | ".</summary>
public class ExcelExtractor : IContentExtractor
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = [".xlsx", ".xls"];

    /// <inheritdoc/>
    public long MaxFileSizeBytes => 50 * 1024 * 1024;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(string filePath)
    {
        // [Thay đổi] M6: mở file rồi dùng chung đường đọc Stream (attachment trong email đi CÙNG đường này) → hành vi file thường không đổi
        using var stream = ExtractorHelpers.OpenShared(filePath);
        foreach (var block in Extract(stream, Path.GetFileName(filePath)))
        {
            yield return block;
        }
    }

    // [Thay đổi] M6: đọc từ Stream (attachment email giải mã trong bộ nhớ — KHÔNG ghi file tạm ra đĩa). Stream phải seek được.
    /// <inheritdoc/>
    public bool SupportsStream => true;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(Stream stream, string fileName)
    {
        // [Thay đổi] H4: kiểm ngân sách trên chính stream sẽ đọc. Ngân sách Streaming: ExcelDataReader đọc sheet theo từng hàng (sheet lớn
        // được phép hơn docx), riêng sharedStrings.xml bị nạp hết vào RAM nên có ngưỡng riêng. .xls nhị phân cũ không phải ZIP → tự bỏ qua.
        ZipLimits.EnsureWithin(stream, ZipLimits.Streaming);
        // CreateReader tự nhận ra .xlsx (OpenXML) hay .xls (BIFF nhị phân) theo NỘI DUNG file, không theo đuôi.
        // File .xls cần bảng mã Windows — đã đăng ký CodePagesEncodingProvider ở FileReaderService.
        using var reader = ExcelReaderFactory.CreateReader(stream);

        do
        {
            // Mốc tên sheet; ghi rõ sheet ẩn — dữ liệu nhạy cảm hay bị "giấu" vào sheet ẩn thay vì xoá
            string hidden = reader.VisibleState == "visible" ? "" : " (ẩn)";
            IReadOnlyList<string> sheetPath = [reader.Name];
            yield return new ContentBlock(BlockKind.SectionMarker, $"Sheet: {reader.Name}{hidden}", sheetPath);

            IReadOnlyList<string>? columnNames = null;
            int rowIndex = 0;
            while (reader.Read())
            {
                var cells = new List<string>(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    cells.Add(FormatCell(reader.GetValue(i), reader.GetNumberFormatString(i)));
                }

                string row = ExtractorHelpers.JoinRow(cells);
                if (row.Length > 0)
                {
                    if (columnNames == null)
                    {
                        columnNames = cells; // dòng đầu tiên có dữ liệu của sheet này làm tên cột
                        yield return new ContentBlock(BlockKind.TableHeader, row, sheetPath);
                    }
                    else
                    {
                        yield return new ContentBlock(BlockKind.TableRow, row, sheetPath, columnNames, rowIndex);
                    }
                }
                rowIndex++;
            }
        } while (reader.NextResult()); // sang sheet kế tiếp
    }

    // Chuyển giá trị 1 ô thành chuỗi GIỐNG như người dùng nhìn thấy trong Excel ở những điểm quan trọng với DLP
    private static string FormatCell(object? value, string? numberFormat)
    {
        switch (value)
        {
            case null:
                return string.Empty;

            case double number:
                // CCCD/số TK lưu dạng SỐ với định dạng "000000000000" để hiện số 0 đầu. Giá trị thô 1192012345 chỉ
                // còn 10 chữ số → regex CCCD 12 số bỏ sót. Áp lại đúng định dạng toàn số 0 đó để khôi phục.
                if (numberFormat is { Length: > 0 } && numberFormat.All(c => c == '0'))
                {
                    return number.ToString(numberFormat, CultureInfo.InvariantCulture);
                }
                // Số nguyên (kể cả rất lớn như số thẻ 16 chữ số) in đủ chữ số, không ra dạng 4.53914880343646E+15
                if (number == Math.Floor(number) && Math.Abs(number) < 1e17)
                {
                    return ((long)number).ToString(CultureInfo.InvariantCulture);
                }
                return number.ToString(CultureInfo.InvariantCulture);

            case DateTime date:
                // Kiểu ngày Việt Nam, khớp rule ngày sinh (dd/MM/yyyy); có giờ thì kèm giờ
                return date.TimeOfDay == TimeSpan.Zero
                    ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                    : date.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }
}
