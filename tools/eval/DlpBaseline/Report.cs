// [Thay đổi] File mới: ghi kết quả đo ra CSV (từng file) và Markdown (báo cáo tóm tắt), kèm cấu hình máy.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DlpService.Classification;

namespace DlpBaseline;

/// <summary>Thông tin chung của 1 lượt đo.</summary>
internal sealed record RunInfo(
    DateTime StartedAt,
    string RawdataFolder,
    int SamplesLoaded,
    int SkippedLines,
    int SamplesMeasured,
    List<string> Formats,
    int MissingFiles,
    int PatternCount,
    double RuleLoadMs,
    double InitMs,
    double WarmupMs,
    double WallMs,
    long AllocatedBytes,
    long PeakWorkingSetBytes,
    double MinConfidence); // [Thay đổi] Công thức Go: ngưỡng loại chỗ khớp đã dùng trong lượt đo

internal static class Report
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // "Nhạy cảm" theo nghĩa DLP (chỉ số A): Confidential hoặc Restricted (top_secret)
    private static bool IsSensitive(SensitivityLevel level) => level >= SensitivityLevel.Confidential;

    /// <summary>Ghi 1 dòng/file. Chỉ có mã rule + số đo, KHÔNG có nội dung hay giá trị nhạy cảm.</summary>
    public static void WriteCsv(string path, List<FileResult> results)
    {
        var sb = new StringBuilder();
        // [Thay đổi] Log timeout: thêm 2 cột scan_complete, timed_out_rules
        // [Thay đổi] S1 giai đoạn 2: thêm split (train/test) + 3 cột điểm S1
        sb.AppendLine("id,format,domain,label,truth,predicted,contains_pii,pii_hit,is_borderline,read_ok,read_error,size_bytes,chars,match_count,extract_ms,scan_ms,classify_ms,total_ms,rules_fired,scan_complete,timed_out_rules,split,s1_internal,s1_confidential,s1_restricted,route_confidence,route_zone,conf_branch,conf_domain,file_conf,file_conf_keep,file_conf_true_domain,tel_kw_no_match_primary,tel_proximity_reject,tel_level_gate_downgrade,tel_unstructured_numeric_hits,tel_unstructured_numeric_density,tel_identifier_volume_near_gate,tel_doc_context,tel_eff_len_bytes");
        foreach (var r in results)
        {
            string[] cells =
            [
                r.Sample.Id, r.Format, r.Sample.Domain, r.Sample.Label, r.Sample.Truth.ToString(), r.Predicted.ToString(),
                Bool(r.Sample.ContainsPii), Bool(r.PiiHit), Bool(r.Sample.IsBorderline), Bool(r.ReadOk), r.ReadError ?? "",
                r.SizeBytes.ToString(Inv), r.Chars.ToString(Inv), r.MatchCount.ToString(Inv),
                Ms(r.ExtractMs), Ms(r.ScanMs), Ms(r.ClassifyMs), Ms(r.TotalMs), r.RulesFired,
                Bool(r.ScanComplete), r.TimedOutRules,
                S1Tuning.IsTrain(r.Sample.Id) ? "train" : "test",
                Score(r.Scores.Internal), Score(r.Scores.Confidential), Score(r.Scores.Restricted),
                // [Thay đổi] Router ba vùng: độ tin cậy + vùng theo ngưỡng mặc định kiến trúc (0,90/0,30)
                Score(ScoreRouter.ConfidenceFor(r.Predicted, r.Scores)),
                ScoreRouter.Route(r.Predicted, r.Scores, r.MatchCount, new RouterOptions()).Zone.ToString(),
                // [Thay đổi] FileConfidence 2 nhánh (shadow): rỗng nếu không tính (đọc lỗi / không nạp model)
                r.Conf?.Branch.ToString() ?? "", r.Conf?.Domain ?? "",
                r.Conf is { } c ? Score(c.Confidence) : "", r.Conf is { } k ? Bool(k.KeepAtLayer1) : "",
                r.ConfTrueDomain is { } t ? Score(t.Confidence) : "",
                // [Thay đổi] Chẩn đoán nhánh B: 7 counter near-miss + độ dài byte (rỗng nếu đọc lỗi)
                .. (r.Telemetry is { } tel
                    ? new[]
                    {
                        tel.KeywordHitNoMatchPrimaryRules.ToString(Inv), tel.ProximityRejectCount.ToString(Inv),
                        tel.LevelGateDowngradeCount.ToString(Inv), tel.UnstructuredNumericHits.ToString(Inv),
                        Score(tel.UnstructuredNumericDensity), Score(tel.IdentifierVolumeNearGate),
                        Bool(tel.DocContextDiscount), tel.EffectiveLengthBytes.ToString(Inv)
                    }
                    : Enumerable.Repeat("", 8).ToArray())
            ];
            sb.AppendLine(string.Join(',', cells.Select(Csv)));
        }
        // BOM để Excel mở đúng tiếng Việt
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        static string Bool(bool b) => b ? "1" : "0";
        static string Ms(double v) => v.ToString("0.###", Inv);
        static string Score(double v) => v.ToString("0.0000", Inv);
        static string Csv(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>Dựng báo cáo Markdown: máy, độ chính xác (A/B/C), tốc độ.</summary>
    public static string BuildMarkdown(RunInfo run, List<FileResult> results, string csvPath)
    {
        var md = new StringBuilder();
        int readErrors = results.Count(r => !r.ReadOk);

        md.AppendLine($"# Baseline DlpService (regex-only) — {run.StartedAt:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine($"- Dữ liệu: `{run.RawdataFolder}` — đọc {run.SamplesLoaded:N0} mẫu ({run.SkippedLines} dòng metadata bị bỏ qua), đo {run.SamplesMeasured:N0} mẫu x [{string.Join(", ", run.Formats)}]");
        md.AppendLine($"- File đã đo: **{results.Count:N0}** — thiếu file: {run.MissingFiles:N0}, đọc lỗi: {readErrors:N0} (tính là Public)");
        // [Thay đổi] Log timeout: số file có rule bị timeout (service log "CHƯA KẾT LUẬN ĐƯỢC", không gắn mức)
        int incomplete = results.Count(r => !r.ScanComplete);
        md.AppendLine($"- Quét chưa trọn (có rule bị timeout): **{incomplete:N0}** file" + (incomplete > 0
            ? " — rule: " + string.Join(", ", results.Where(r => !r.ScanComplete).SelectMany(r => r.TimedOutRules.Split(';')).GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})"))
            : ""));
        md.AppendLine($"- Chi tiết từng file: `{csvPath}`");
        md.AppendLine();

        AppendMachine(md, run);
        AppendAccuracy(md, results);
        AppendSpeed(md, run, results);
        AppendDefinitions(md);
        return md.ToString();
    }

    // ------------------------------------------------------------------------------------------------ Máy
    private static void AppendMachine(StringBuilder md, RunInfo run)
    {
        md.AppendLine("## Máy đo");
        md.AppendLine();
        md.AppendLine("| Mục | Giá trị |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| CPU | {CpuName()} |");
        md.AppendLine($"| Số luồng logic | {Environment.ProcessorCount} |");
        md.AppendLine($"| RAM | {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024:0.0} GB |");
        md.AppendLine($"| Hệ điều hành | {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) |");
        md.AppendLine($"| .NET | {RuntimeInformation.FrameworkDescription} |");
        md.AppendLine($"| Số pattern đã nạp | {run.PatternCount} |");
        md.AppendLine($"| MinConfidence (ngưỡng loại chỗ khớp) | {run.MinConfidence.ToString("0.00", Inv)} |"); // [Thay đổi] Công thức Go
        md.AppendLine();
    }

    private static string CpuName()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
            catch (Exception)
            {
                // Không đọc được registry -> dùng tên kiến trúc bên dưới
            }
        }
        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    // ------------------------------------------------------------------------------------------------ Độ chính xác
    private static void AppendAccuracy(StringBuilder md, List<FileResult> results)
    {
        var groups = BreakdownGroups(results);

        md.AppendLine("## 1. Độ chính xác");
        md.AppendLine();

        md.AppendLine("### A. Nhị phân \"nhạy cảm\" (chỉ số chính)");
        md.AppendLine();
        md.AppendLine("Positive = nhãn confidential/top_secret. Dự đoán positive = engine trả Confidential/Restricted.");
        md.AppendLine();
        AppendBinaryTable(md, groups, r => IsSensitive(r.Sample.Truth), r => IsSensitive(r.Predicted));

        md.AppendLine("### B. Xếp đúng 4 mức");
        md.AppendLine();
        var all = ConfusionMatrix.From(results);
        md.AppendLine($"Accuracy: **{Pct(all.Accuracy)}** — Macro-F1: **{Pct(all.MacroF1)}**");
        md.AppendLine();
        md.AppendLine("Ma trận nhầm lẫn (hàng = nhãn thật, cột = engine dự đoán):");
        md.AppendLine();
        md.AppendLine("| Thật \\ Dự đoán | " + string.Join(" | ", ConfusionMatrix.Levels) + " | Tổng |");
        md.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---|", ConfusionMatrix.Levels.Length + 1)));
        for (int i = 0; i < ConfusionMatrix.Levels.Length; i++)
        {
            var cells = Enumerable.Range(0, ConfusionMatrix.Levels.Length)
                .Select(j => i == j ? $"**{all.Counts[i, j]:N0}**" : all.Counts[i, j].ToString("N0"));
            int rowSum = Enumerable.Range(0, ConfusionMatrix.Levels.Length).Sum(j => all.Counts[i, j]);
            md.AppendLine($"| {ConfusionMatrix.Levels[i]} | {string.Join(" | ", cells)} | {rowSum:N0} |");
        }
        md.AppendLine();
        md.AppendLine("| Mức | Precision | Recall | F1 | Số file thật |");
        md.AppendLine("|---|---|---|---|---|");
        for (int i = 0; i < ConfusionMatrix.Levels.Length; i++)
        {
            var s = all.ForLevel(i);
            md.AppendLine($"| {ConfusionMatrix.Levels[i]} | {Pct(s.Precision)} | {Pct(s.Recall)} | {Pct(s.F1)} | {s.Tp + s.Fn:N0} |");
        }
        md.AppendLine();
        md.AppendLine("| Nhóm | Số file | Accuracy | Macro-F1 |");
        md.AppendLine("|---|---|---|---|");
        foreach (var (name, items) in groups)
        {
            var m = ConfusionMatrix.From(items);
            md.AppendLine($"| {name} | {m.Total:N0} | {Pct(m.Accuracy)} | {Pct(m.MacroF1)} |");
        }
        md.AppendLine();

        md.AppendLine("### C. Phát hiện PII");
        md.AppendLine();
        md.AppendLine("Positive = `contains_pii = true`. Dự đoán positive = có ít nhất 1 chỗ khớp của rule `category = pii`.");
        md.AppendLine();
        AppendBinaryTable(md, groups, r => r.Sample.ContainsPii, r => r.PiiHit);
    }

    private static void AppendBinaryTable(StringBuilder md, List<(string Name, List<FileResult> Items)> groups,
        Func<FileResult, bool> truth, Func<FileResult, bool> predicted)
    {
        md.AppendLine("| Nhóm | Số file | TP | FP (báo nhầm) | FN (bỏ sót) | TN | Precision | Recall | F1 |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (name, items) in groups)
        {
            var s = BinaryScore.From(items, truth, predicted);
            md.AppendLine($"| {name} | {s.Total:N0} | {s.Tp:N0} | {s.Fp:N0} | {s.Fn:N0} | {s.Tn:N0} | {Pct(s.Precision)} | {Pct(s.Recall)} | {Pct(s.F1)} |");
        }
        md.AppendLine();
    }

    // Các nhóm dùng chung cho mọi bảng: tất cả, theo định dạng, theo domain, ca khó / không khó
    private static List<(string Name, List<FileResult> Items)> BreakdownGroups(List<FileResult> results)
    {
        var groups = new List<(string, List<FileResult>)> { ("**Tất cả**", results) };
        groups.AddRange(results.GroupBy(r => r.Format).OrderBy(g => g.Key).Select(g => ($"Định dạng {g.Key}", g.ToList())));
        groups.AddRange(results.GroupBy(r => r.Sample.Domain).OrderBy(g => g.Key).Select(g => ($"Domain {g.Key}", g.ToList())));
        groups.Add(("Ca khó (is_borderline)", results.Where(r => r.Sample.IsBorderline).ToList()));
        groups.Add(("Ca thường", results.Where(r => !r.Sample.IsBorderline).ToList()));
        return groups;
    }

    // ------------------------------------------------------------------------------------------------ Tốc độ
    private static void AppendSpeed(StringBuilder md, RunInfo run, List<FileResult> results)
    {
        md.AppendLine("## 2. Tốc độ (1 luồng, tuần tự)");
        md.AppendLine();
        md.AppendLine($"- Nạp rule: {run.RuleLoadMs:N0} ms — khởi tạo reader/scanner/engine: {run.InitMs:N0} ms — khởi động {run.WarmupMs:N0} ms (không tính vào số liệu)");
        md.AppendLine($"- Tổng thời gian vòng đo: {run.WallMs / 1000:N1} giây — bộ nhớ đã cấp phát: {run.AllocatedBytes / 1024d / 1024:N0} MB — RAM cao nhất (working set): {run.PeakWorkingSetBytes / 1024d / 1024:N0} MB");
        md.AppendLine();

        md.AppendLine("| Định dạng | Bước | TB (ms) | p50 | p95 | Max | Tỷ trọng |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var group in results.GroupBy(r => r.Format).OrderBy(g => g.Key).Append(results.GroupBy(_ => "tất cả").Single()))
        {
            var items = group.ToList();
            double totalSum = items.Sum(r => r.TotalMs);
            var steps = new (string Name, Func<FileResult, double> Get)[]
            {
                ("Trích text", r => r.ExtractMs),
                ("Quét regex", r => r.ScanMs),
                ("Phân loại", r => r.ClassifyMs),
                ("**Tổng**", r => r.TotalMs)
            };
            foreach (var (name, get) in steps)
            {
                var t = TimingSummary.From(items.Select(get));
                string share = totalSum > 0 ? (t.Sum / totalSum).ToString("P0", Inv) : "—";
                md.AppendLine($"| {group.Key} | {name} | {t.Avg:0.0} | {t.P50:0.0} | {t.P95:0.0} | {t.Max:0.0} | {share} |");
            }
        }
        md.AppendLine();

        md.AppendLine("| Định dạng | Số file | Dung lượng | File/giây | MB/giây | KB text TB/file |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var group in results.GroupBy(r => r.Format).OrderBy(g => g.Key).Append(results.GroupBy(_ => "tất cả").Single()))
        {
            var items = group.ToList();
            double seconds = items.Sum(r => r.TotalMs) / 1000;
            double mb = items.Sum(r => r.SizeBytes) / 1024d / 1024;
            double avgTextKb = items.Average(r => r.Chars) * 2 / 1024d; // 1 ký tự .NET = 2 byte
            md.AppendLine($"| {group.Key} | {items.Count:N0} | {mb:N1} MB | {(seconds > 0 ? items.Count / seconds : 0):N1} | {(seconds > 0 ? mb / seconds : 0):N2} | {avgTextKb:N1} |");
        }
        md.AppendLine();

        md.AppendLine("10 file chậm nhất:");
        md.AppendLine();
        md.AppendLine("| File | Định dạng | KB | Trích | Quét | Phân loại | Tổng (ms) |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in results.OrderByDescending(r => r.TotalMs).Take(10))
        {
            md.AppendLine($"| {r.Sample.Id} | {r.Format} | {r.SizeBytes / 1024d:N0} | {r.ExtractMs:0.0} | {r.ScanMs:0.0} | {r.ClassifyMs:0.0} | {r.TotalMs:0.0} |");
        }
        md.AppendLine();
    }

    private static void AppendDefinitions(StringBuilder md)
    {
        md.AppendLine("## Ghi chú");
        md.AppendLine();
        md.AppendLine("- Nhãn → mức engine: public→Public, internal→Internal, confidential→Confidential, top_secret→Restricted.");
        md.AppendLine("- **Precision** = trong các file bị báo positive, bao nhiêu % đúng (thấp = báo nhầm nhiều). **Recall** = trong các file positive thật, bắt được bao nhiêu % (thấp = bỏ sót nhiều).");
        md.AppendLine("- Pipeline giống `FileWatcherService.ProcessFile` nhưng **không** gồm debounce 300ms, hash SHA256 chống quét trùng và ghi log.");
        md.AppendLine("- Thời gian trích text gồm cả đọc đĩa: file đã nằm trong cache của Windows sẽ nhanh hơn lần đọc đầu tiên.");
        md.AppendLine("- Mỗi mẫu có bản docx và pdf cùng nội dung → chênh lệch giữa 2 định dạng phản ánh chất lượng trích text.");
    }

    // [Thay đổi] S1 giai đoạn 2: phần báo cáo so sánh S1 (ngưỡng đã chọn) với cách phân loại hiện tại trên tập TEST
    public static string BuildS1Section(TuningResult t, string thresholdPath)
    {
        var md = new StringBuilder();
        md.AppendLine();
        md.AppendLine("## 3. S1 (shadow) — chọn ngưỡng trên dữ liệu");
        md.AppendLine();
        md.AppendLine($"- Chia theo id mẫu: train {t.TrainFiles:N0} file ({S1Tuning.TrainPercent}%), test {t.TestFiles:N0} file.");
        md.AppendLine($"- Ràng buộc khi chọn: recall \"nhạy cảm\" ≥ recall của cách hiện tại trên train ({Pct(t.RequiredRecall)}) — "
            + (t.MetRecallConstraint ? "**đạt**." : "**KHÔNG bộ ngưỡng nào đạt**, đang dùng bộ có F1 cao nhất."));
        md.AppendLine($"- Ngưỡng đã chọn: **Internal ≥ {t.Thresholds.Internal:0.00}, Confidential ≥ {t.Thresholds.Confidential:0.00}, Restricted ≥ {t.Thresholds.Restricted:0.00}** — lưu tại `{thresholdPath}`");
        md.AppendLine();

        md.AppendLine("### So sánh trên tập TEST (dữ liệu KHÔNG dùng lúc chọn ngưỡng)");
        md.AppendLine();
        md.AppendLine("| Cách phân loại | Precision | Recall | F1 | FP (báo nhầm) | FN (bỏ sót) | Accuracy 4 mức | Macro-F1 |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        md.AppendLine($"| Hiện tại (mức cao nhất của module) | {Pct(t.CurrentTestBinary.Precision)} | {Pct(t.CurrentTestBinary.Recall)} | {Pct(t.CurrentTestBinary.F1)} | {t.CurrentTestBinary.Fp:N0} | {t.CurrentTestBinary.Fn:N0} | {Pct(t.CurrentTestMatrix.Accuracy)} | {Pct(t.CurrentTestMatrix.MacroF1)} |");
        md.AppendLine($"| S1 + ngưỡng | {Pct(t.S1TestBinary.Precision)} | {Pct(t.S1TestBinary.Recall)} | {Pct(t.S1TestBinary.F1)} | {t.S1TestBinary.Fp:N0} | {t.S1TestBinary.Fn:N0} | {Pct(t.S1TestMatrix.Accuracy)} | {Pct(t.S1TestMatrix.MacroF1)} |");
        md.AppendLine();

        md.AppendLine("Ma trận nhầm lẫn của S1 trên test (hàng = nhãn thật, cột = S1 dự đoán):");
        md.AppendLine();
        md.AppendLine("| Thật \\ Dự đoán | " + string.Join(" | ", ConfusionMatrix.Levels) + " |");
        md.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---|", ConfusionMatrix.Levels.Length)));
        for (int i = 0; i < ConfusionMatrix.Levels.Length; i++)
        {
            md.AppendLine($"| {ConfusionMatrix.Levels[i]} | " + string.Join(" | ",
                Enumerable.Range(0, ConfusionMatrix.Levels.Length).Select(j => i == j ? $"**{t.S1TestMatrix.Counts[i, j]:N0}**" : t.S1TestMatrix.Counts[i, j].ToString("N0"))) + " |");
        }
        md.AppendLine();

        md.AppendLine("Điểm S1 trung bình theo nhãn thật (toàn bộ dữ liệu) — càng tách xa nhau thì S1 càng phân biệt tốt:");
        md.AppendLine();
        md.AppendLine("| Nhãn thật | S1 Internal | S1 Confidential | S1 Restricted |");
        md.AppendLine("|---|---|---|---|");
        foreach (var (label, mi, mc, mr) in t.MeanByLabel)
        {
            md.AppendLine($"| {label} | {mi:0.000} | {mc:0.000} | {mr:0.000} |");
        }
        md.AppendLine();
        md.AppendLine($"- Vùng xám (quyết định \"nhạy cảm/không\" cách biên < {S1Tuning.GreyZoneWidth:0.00}, biên = max(S1C − τC, S1R − τR)): **{Pct(t.GreyZoneShare)}** file test — "
            + "đây là số file giai đoạn 3 sẽ đánh dấu need_review (và sau này gửi LLM).");
        return md.ToString();
    }

    private static string Pct(double? value) => value is double v ? v.ToString("P1", Inv) : "—";
}
