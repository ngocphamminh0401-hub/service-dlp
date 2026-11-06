// [Thay đổi] File mới (mục 13.3): ghi CSV (1 dòng/mẫu, không chứa nội dung/nhãn nhạy cảm thật — chỉ mã rule/số đo,
// theo đúng quy tắc chung "không log dữ liệu nhạy cảm nguyên văn") + báo cáo Markdown tóm tắt (p50/p95, tokens/s,
// RAM, tỷ lệ lỗi parse, độ chính xác so với nhãn thật).
using System.Globalization;
using System.Text;
using Dlp.Inference;

namespace DlpLlmBenchmark;

internal static class BenchReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void WriteCsv(string path, List<BenchResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,domain,truth,predicted,status,client_ms,server_latency_ms,generated_tokens,need_review,matched_rule");
        foreach (var r in results)
        {
            string predicted = r.Response != null ? LevelMapper.ToClsLevel(r.Response.SensitivityLevel).ToString() : "";
            string status = r.Response?.Status.ToString() ?? "NO_CONNECT";
            string[] cells =
            [
                r.Sample.Id, r.Sample.Domain, r.Sample.Truth.ToString(), predicted, status,
                r.ClientMs.ToString("0", Inv),
                (r.Response?.LatencyMs ?? 0).ToString(Inv),
                (r.Response?.GeneratedTokens ?? 0).ToString(Inv),
                r.Response?.NeedReview.ToString() ?? "",
                r.Response?.MatchedRule.ToString() ?? ""
            ];
            sb.AppendLine(string.Join(',', cells.Select(Csv)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static void WriteMarkdown(string path, List<BenchResult> results, Info? infoBefore, Info? infoAfter,
        int skippedNoExtractor, int skippedMaskedAway, string pipeName, string rawdataFolder)
    {
        var ok = results.Where(r => r.Response?.Status == Status.Ok).ToList();
        var serverLatencies = ok.Select(r => (double)r.Response!.LatencyMs).OrderBy(x => x).ToList();
        var clientLatencies = results.Where(r => r.Response != null).Select(r => r.ClientMs).OrderBy(x => x).ToList();
        var tokensPerSec = ok.Where(r => r.Response!.LatencyMs > 0)
            .Select(r => r.Response!.GeneratedTokens / (r.Response.LatencyMs / 1000.0)).ToList();

        int total = results.Count;
        int noConnect = results.Count(r => r.Response == null);
        int modelError = results.Count(r => r.Response?.Status == Status.ModelError);
        int timeout = results.Count(r => r.Response?.Status == Status.Timeout);
        int correct = ok.Count(r => LevelMapper.ToClsLevel(r.Response!.SensitivityLevel) == r.Sample.Truth);

        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark LLM thật (DlpLlmHost) — mục 13.3");
        sb.AppendLine();
        sb.AppendLine($"- Ngày chạy: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Pipe: `{pipeName}`, model_ver: `{infoBefore?.ModelVer}`, dữ liệu: `{rawdataFolder}`");
        sb.AppendLine($"- Số mẫu: {total} (bỏ qua {skippedNoExtractor} không có extractor, {skippedMaskedAway} bị huỷ hết sau mask)");
        sb.AppendLine($"- RAM tiến trình LLM: trước={FormatBytes(infoBefore?.RssBytes)}, sau={FormatBytes(infoAfter?.RssBytes)}");
        sb.AppendLine();
        sb.AppendLine("## Độ trễ (chỉ tính các job status=OK)");
        sb.AppendLine();
        sb.AppendLine("| Chỉ số | Server (`latency_ms` LLM tự đo) | Client (đo cả round-trip + hàng đợi) |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| p50 | {Percentile(serverLatencies, 0.50):0} ms | {Percentile(clientLatencies, 0.50):0} ms |");
        sb.AppendLine($"| p95 | {Percentile(serverLatencies, 0.95):0} ms | {Percentile(clientLatencies, 0.95):0} ms |");
        sb.AppendLine($"| min/max | {(serverLatencies.Count > 0 ? $"{serverLatencies.Min():0}/{serverLatencies.Max():0}" : "-")} ms | {(clientLatencies.Count > 0 ? $"{clientLatencies.Min():0}/{clientLatencies.Max():0}" : "-")} ms |");
        sb.AppendLine();
        sb.AppendLine("## Thông lượng + độ ổn định");
        sb.AppendLine();
        sb.AppendLine($"- tokens/s trung bình: {(tokensPerSec.Count > 0 ? tokensPerSec.Average().ToString("0.0", Inv) : "-")} (trên {tokensPerSec.Count} job OK)");
        sb.AppendLine($"- Không kết nối được LLM: {noConnect}/{total} ({Pct(noConnect, total)})");
        sb.AppendLine($"- `TIMEOUT`: {timeout}/{total} ({Pct(timeout, total)})");
        sb.AppendLine($"- **`MODEL_ERROR` (ước lượng tỷ lệ lỗi parse JSON sau retry — mục 13.3, ngưỡng đề xuất <1%)**: {modelError}/{total} ({Pct(modelError, total)})");
        sb.AppendLine();
        sb.AppendLine("  > Lưu ý: `MODEL_ERROR` gộp cả 2 nguyên nhân — JSON sai sau khi retry (đúng ý \"lỗi parse\") VÀ lỗi runtime thật (hết bộ nhớ, model hỏng...). Hợp đồng hiện tại không có mã lỗi RIÊNG cho \"lỗi parse\" — đây là số XẤP XỈ, không phải số chính xác tuyệt đối. Xem log server (`[llm-host] GenerateAsync lỗi: ...`) để phân biệt nếu tỷ lệ này cao bất thường.");
        sb.AppendLine();
        sb.AppendLine($"## Độ chính xác so với nhãn thật (bonus — không phải yêu cầu gốc của mục 13.3, nhưng tận dụng luôn bộ dữ liệu nhãn)");
        sb.AppendLine();
        sb.AppendLine($"- Đúng nhãn: {correct}/{ok.Count} job OK ({Pct(correct, ok.Count)})");
        sb.AppendLine();
        sb.AppendLine("| Nhãn thật \\ Dự đoán | Public | Internal | Confidential | Restricted |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (DlpService.Classification.SensitivityLevel truth in Enum.GetValues<DlpService.Classification.SensitivityLevel>())
        {
            var row = ok.Where(r => r.Sample.Truth == truth).ToList();
            string Cell(DlpService.Classification.SensitivityLevel pred) => row.Count(r => LevelMapper.ToClsLevel(r.Response!.SensitivityLevel) == pred).ToString(Inv);
            sb.AppendLine($"| {truth} | {Cell(DlpService.Classification.SensitivityLevel.Public)} | {Cell(DlpService.Classification.SensitivityLevel.Internal)} | {Cell(DlpService.Classification.SensitivityLevel.Confidential)} | {Cell(DlpService.Classification.SensitivityLevel.Restricted)} |");
        }
        sb.AppendLine();
        sb.AppendLine("**⚠️ Cỡ mẫu nhỏ (theo `--limit`) — đây KHÔNG phải bộ 500-2.000 mẫu đầy đủ theo mục 13.1. Dùng để kiểm tra luồng benchmark hoạt động đúng và có con số ban đầu, không phải kết luận cuối cùng về chất lượng model.**");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }
        int index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static string Pct(int n, int total) => total == 0 ? "0%" : (n * 100.0 / total).ToString("0.0", Inv) + "%";

    private static string FormatBytes(ulong? bytes) => bytes == null ? "?" : $"{bytes.Value / (1024.0 * 1024.0):0} MB";

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n')
        ? "\"" + s.Replace("\"", "\"\"") + "\""
        : s;
}
