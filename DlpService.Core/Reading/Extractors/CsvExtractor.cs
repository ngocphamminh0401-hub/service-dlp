// [Thay đổi] File mới (mở rộng định dạng - Bước 1): trích xuất file .csv — mỗi dòng CSV giữ nguyên thành 1 dòng text
// [Thay đổi] Streaming + giữ cấu trúc (Bước 4): tách từng dòng thành ô (SplitCsvLine), dòng đầu = TableHeader (lưu
// tên cột), các dòng sau = TableRow kèm ColumnNames — để chế độ bảng của proximity/volume_escalation vẫn hoạt động
// đúng như trước (Text mỗi dòng vẫn nối ô bằng " | "), đồng thời có sẵn tên cột cho báo cáo sau này.
namespace DlpService.Reading.Extractors;

/// <summary>Đọc file .csv: giữ nguyên từng dòng (dòng tiêu đề + các dòng dữ liệu), để chế độ bảng của proximity
/// (nhãn cột ở dòng đầu, giá trị ở các dòng dưới) và volume_escalation hoạt động đúng.</summary>
public class CsvExtractor : IContentExtractor
{
    /// <inheritdoc/>
    public IReadOnlyCollection<string> Extensions { get; } = [".csv"];

    /// <inheritdoc/>
    public long MaxFileSizeBytes => 10 * 1024 * 1024;

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(string filePath)
    {
        // [Thay đổi] M6: mở file rồi dùng chung đường đọc Stream (attachment trong email đi cùng đường này)
        using var stream = ExtractorHelpers.OpenShared(filePath);
        foreach (var block in Extract(stream, Path.GetFileName(filePath)))
        {
            yield return block;
        }
    }

    /// <inheritdoc/>
    public bool SupportsStream => true; // [Thay đổi] M6

    /// <inheritdoc/>
    public IEnumerable<ContentBlock> Extract(Stream stream, string fileName)
    {
        string text = TextFileReader.ReadAll(stream);
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);

        IReadOnlyList<string>? columnNames = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var cells = ExtractorHelpers.SplitCsvLine(lines[i]);
            string rowText = ExtractorHelpers.JoinRow(cells);

            if (columnNames == null)
            {
                // Dòng đầu tiên coi là dòng tiêu đề cột — dùng làm ColumnNames cho mọi dòng dữ liệu phía sau
                columnNames = cells;
                yield return new ContentBlock(BlockKind.TableHeader, rowText, ContextPath: []);
            }
            else
            {
                yield return new ContentBlock(BlockKind.TableRow, rowText, ContextPath: [], ColumnNames: columnNames, SourceIndex: i);
            }
        }
    }
}
