// [Thay đổi] File mới (mục 4.10 - Context builder): chọn chunk gửi LLM theo ngân sách token, không gửi nguyên file.
// Pass 2 riêng (mục 4.9 "lazy, trích lần hai"): tự gọi IContentExtractor.Extract() lại, KHÔNG đi qua
// FileReaderService/Scanning đang chạy thật cho luồng quét regex — Worker LLM gọi lớp này khi tới lượt xử lý job.
using DlpService.Reading;
using DlpService.Storage;

namespace DlpService.Llm;

/// <summary>1 chunk sẽ gửi LLM. Khớp message Chunk trong hợp đồng gRPC (kind, rule, text) ở docs/kien-truc-trien-khai.md mục 5.2.</summary>
/// <param name="Kind">Loại chunk: "full" (nguyên văn, file nhỏ hơn ngân sách), "metadata", "context", "sample", "stats".</param>
/// <param name="Rule">Rule liên quan (chỉ có ở chunk "context"); null nếu không áp dụng.</param>
/// <param name="Text">Nội dung chunk — CHƯA MASK, Masker (mục 4.11) chạy sau, ngay trước khi thực sự gửi đi.</param>
public sealed record ChunkResult(string Kind, string? Rule, string Text);

/// <summary>Context builder — chọn đoạn text đại diện cho 1 file để gửi LLM thẩm định, trong ngân sách token cố định
/// (kiến trúc mục 4.10: 1.500-3.000 token/file).</summary>
public static class ChunkSelector
{
    // [Thay đổi] Giữ NHẤT QUÁN với FileReaderService.MaxExtractedChars: offset trong finding.location (Pass 1) chỉ
    // có ý nghĩa trong phạm vi ký tự đã quét ở Pass 1 — Pass 2 phải dựng lại flatText với CÙNG giới hạn ký tự, cùng
    // cách nối ("\n" giữa các block), thì offset mới khớp đúng vị trí.
    private const int MaxFlatTextChars = 2_000_000;

    // ±300 ký tự quanh vị trí khớp (kiến trúc mục 4.10 cho phép 150-300, chọn cận trên để đủ ngữ cảnh cho LLM đọc)
    private const int ContextWindowChars = 300;

    // Tối đa 5 đoạn ngữ cảnh (kiến trúc mục 4.10). Finding hiện chỉ lưu 1 offset/rule (xem FileWatcherService.cs) nên
    // thực tế là tối đa 5 rule khác nhau, không phải nhiều vị trí của cùng 1 rule.
    private const int MaxContextExcerpts = 5;

    /// <summary>Chọn chunk cho 1 file đã có kết quả quét (vùng xám, đang chờ LLM thẩm định).</summary>
    /// <param name="extractor">Extractor đúng định dạng file (lấy qua FileReaderService.FindExtractor).</param>
    /// <param name="filePath">Đường dẫn file cần trích lại.</param>
    /// <param name="fileName">Tên file (hiển thị trong metadata, không phải đường dẫn đầy đủ).</param>
    /// <param name="findings">Các finding của file (từ bảng finding — rule_id, score, location, count).</param>
    /// <param name="tokenBudget">Ngân sách token cho cả file (kiến trúc: 1.500-3.000).</param>
    public static IReadOnlyList<ChunkResult> SelectChunks(
        IContentExtractor extractor, string filePath, string fileName,
        IReadOnlyList<FindingRecord> findings, int tokenBudget)
    {
        var blocks = extractor.Extract(filePath).ToList();
        string flatText = BuildFlatText(blocks);

        // File nhỏ hơn ngân sách → gửi toàn bộ text đã trích (còn phải qua Masker ở bước sau), khỏi chọn chunk
        if (TokenEstimator.EstimateTokens(flatText) <= tokenBudget)
        {
            return [new ChunkResult("full", null, flatText)];
        }

        var chunks = new List<ChunkResult>();

        // 1) Metadata và cấu trúc (15%)
        string metaText = TrimToBudget(BuildMetadataText(fileName, blocks), (int)(tokenBudget * 0.15));
        if (metaText.Length > 0)
        {
            chunks.Add(new ChunkResult("metadata", null, metaText));
        }

        // 2) Ngữ cảnh quanh match vùng xám (50%)
        chunks.AddRange(BuildContextChunks(flatText, findings, (int)(tokenBudget * 0.50)));

        // 3) Mẫu đại diện (35%)
        string sampleText = BuildSampleText(flatText, blocks, (int)(tokenBudget * 0.35));
        if (sampleText.Length > 0)
        {
            chunks.Add(new ChunkResult("sample", null, sampleText));
        }

        // 4) Phần còn lại không gửi — thay bằng thống kê
        if (findings.Count > 0)
        {
            string stats = "Tổng chỗ khớp: " + string.Join(", ", findings.Select(f => $"{f.RuleId}={f.Count}"));
            chunks.Add(new ChunkResult("stats", null, stats));
        }

        return chunks;
    }

    // Nối text các block bằng "\n" — PHẢI giống hệt cách FileReaderService ghép để offset của finding khớp đúng.
    // Dừng ở MaxFlatTextChars vì Pass 1 cũng dừng ở đó (RegexScanner chưa từng thấy phần sau điểm này).
    private static string BuildFlatText(IEnumerable<ContentBlock> blocks)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var block in blocks)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            sb.Append(block.Text);
            if (sb.Length >= MaxFlatTextChars)
            {
                break;
            }
        }
        return sb.ToString();
    }

    // Tên file + tiêu đề tài liệu (Heading đầu tiên) + tên sheet/trang (SectionMarker) + tên cột (TableHeader)
    private static string BuildMetadataText(string fileName, List<ContentBlock> blocks)
    {
        var parts = new List<string> { $"Tên file: {fileName}" };

        var title = blocks.FirstOrDefault(b => b.Kind == BlockKind.Heading);
        if (title != null)
        {
            parts.Add($"Tiêu đề: {title.Text}");
        }

        var sections = blocks.Where(b => b.Kind == BlockKind.SectionMarker).Select(b => b.Text).Distinct().ToList();
        if (sections.Count > 0)
        {
            parts.Add("Sheet/Trang: " + string.Join(", ", sections));
        }

        var columns = blocks.Where(b => b.Kind == BlockKind.TableHeader).Select(b => b.Text).Distinct().ToList();
        if (columns.Count > 0)
        {
            parts.Add("Cột: " + string.Join(" | ", columns));
        }

        return string.Join("\n", parts);
    }

    // Mỗi finding lấy 1 cửa sổ ±300 ký tự quanh offset đã lưu (tối đa 5 finding điểm cao nhất, dừng khi hết ngân sách)
    private static List<ChunkResult> BuildContextChunks(string flatText, IReadOnlyList<FindingRecord> findings, int budgetTokens)
    {
        var result = new List<ChunkResult>();
        int used = 0;

        foreach (var finding in findings.OrderByDescending(f => f.Score).Take(MaxContextExcerpts))
        {
            if (ParseOffset(finding.Location) is not int offset)
            {
                continue;
            }

            int start = Math.Max(0, offset - ContextWindowChars);
            int end = Math.Min(flatText.Length, offset + ContextWindowChars);
            if (start >= end)
            {
                continue;
            }

            string window = flatText[start..end];
            int tokens = TokenEstimator.EstimateTokens(window);
            if (used + tokens > budgetTokens)
            {
                break;
            }

            result.Add(new ChunkResult("context", finding.RuleId, window));
            used += tokens;
        }

        return result;
    }

    // "offset:<số>" — xem FileWatcherService.cs chỗ tạo FindingRecord
    private static int? ParseOffset(string location) =>
        location.StartsWith("offset:", StringComparison.Ordinal) && int.TryParse(location.AsSpan(7), out int value)
            ? value
            : null;

    // Có bảng: 5-10 dòng đầu mỗi bảng/sheet + vài dòng rải rác (ổn định theo vị trí, không dùng số ngẫu nhiên thật
    // để đo lại được nhiều lần ra cùng kết quả). Không có bảng: đoạn đầu + giữa + cuối văn bản.
    private static string BuildSampleText(string flatText, List<ContentBlock> blocks, int budgetTokens)
    {
        var tableRows = blocks.Where(b => b.Kind == BlockKind.TableRow).ToList();
        if (tableRows.Count > 0)
        {
            var lines = new List<string>();
            foreach (var group in tableRows.GroupBy(b => string.Join(">", b.ContextPath)))
            {
                var rows = group.ToList();
                lines.AddRange(rows.Take(8).Select(b => b.Text)); // 5-10 dòng đầu mỗi bảng/sheet

                // Vài dòng rải rác ở phần còn lại (cách đều, không phải toàn bộ) — đại diện cho phần chưa lấy
                lines.AddRange(rows.Skip(8).Where((_, i) => i % 7 == 0).Take(3).Select(b => b.Text));
            }
            return TrimToBudget(string.Join("\n", lines), budgetTokens);
        }

        if (flatText.Length == 0)
        {
            return string.Empty;
        }

        int sliceChars = Math.Max(1, TokenEstimator.MaxCharsForBudget(budgetTokens) / 3);
        string head = flatText[..Math.Min(flatText.Length, sliceChars)];
        string mid = flatText.Length > sliceChars * 2
            ? flatText.Substring((flatText.Length - sliceChars) / 2, Math.Min(sliceChars, flatText.Length))
            : string.Empty;
        string tail = flatText.Length > sliceChars ? flatText[^Math.Min(flatText.Length, sliceChars)..] : string.Empty;

        return string.Join("\n...\n", new[] { head, mid, tail }.Where(s => s.Length > 0));
    }

    private static string TrimToBudget(string text, int tokenBudget)
    {
        int maxChars = TokenEstimator.MaxCharsForBudget(tokenBudget);
        return text.Length <= maxChars ? text : text[..maxChars];
    }
}
