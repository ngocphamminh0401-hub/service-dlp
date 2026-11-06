// [Thay đổi] File mới (lệnh chẩn đoán "explain <file_id>"): chạy tay từ dòng lệnh (KHÔNG qua Windows Service/SCM),
// mở DB read-only về mặt ý nghĩa (không ghi gì), in ra console mọi thứ ResultStore.GetExplain trả về + so sánh
// rules_ver hiện tại (đọc rule trên đĩa) với rules_ver đã lưu lúc quét, để biết rule có đổi từ đó tới giờ hay không.
// Không in giá trị thật của file — mọi bảng nguồn (finding/llm_result) vốn đã không lưu giá trị thật (mục 5 CLAUDE.md).
using DlpService.Classification;
using DlpService.Scanning;
using DlpService.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DlpService.Diagnostics;

public static class ExplainCommand
{
    /// <summary>Chạy "explain &lt;file_id&gt;". Trả mã thoát tiến trình (0 = tìm thấy và in được, 1 = lỗi/không tìm thấy).</summary>
    public static int Run(string[] args)
    {
        if (args.Length < 2 || !long.TryParse(args[1], out long fileId))
        {
            Console.WriteLine("Cách dùng: DlpService.exe explain <file_id>");
            return 1;
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        // [Thay đổi] Cùng cách đọc DbPath như Worker.cs, để chẩn đoán đúng DB service thật đang dùng
        string dbPath = config["Storage:DbPath"] ?? Path.Combine("data", "dlp.db");
        if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
        }

        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"Không tìm thấy DB: {dbPath}");
            return 1;
        }

        using var store = new ResultStore(dbPath);
        var explain = store.GetExplain(fileId);
        if (explain == null)
        {
            Console.WriteLine($"Không tìm thấy file_id={fileId}");
            return 1;
        }

        // [Thay đổi] Đọc lại rule trên đĩa để so sánh — chỉ để CẢNH BÁO drift, không dùng để tính lại gì cả
        var ruleFolder = Path.Combine(AppContext.BaseDirectory, "rules");
        var currentRulesVer = RuleLoader.ComputeRulesVersion(ruleFolder);
        var patterns = new RuleLoader(NullLogger.Instance).LoadFromFolder(ruleFolder);
        var scanner = new RegexScanner(patterns, currentRulesVer);

        Print(explain, scanner, currentRulesVer);
        return 0;
    }

    private static void Print(ExplainRecord e, RegexScanner scanner, string currentRulesVer)
    {
        Console.WriteLine($"file_id       : {e.FileId}");
        Console.WriteLine($"path          : {e.Path}");
        Console.WriteLine($"size          : {e.Size} byte, sửa lúc {e.MtimeUtc:O}");
        Console.WriteLine($"file_hash     : {e.FileHash}");
        Console.WriteLine();
        Console.WriteLine($"status        : {e.Status}");
        Console.WriteLine($"source        : {e.Source}");
        Console.WriteLine($"needs_review  : {e.NeedsReview}");
        Console.WriteLine($"needs_ocr     : {e.NeedsOcr}");
        Console.WriteLine($"S1            : {e.S1:0.0000}");
        Console.WriteLine($"S_final       : {e.SFinal:0.0000}");
        Console.WriteLine($"rules_ver     : {e.RulesVer}{(e.RulesVer != currentRulesVer ? "  ⚠ KHÁC bộ rule hiện tại trên đĩa (" + currentRulesVer + ") — kết quả có thể đã lỗi thời" : "")}");
        Console.WriteLine($"merge_policy_ver : {e.MergePolicyVer}{(e.MergePolicyVer != MergePolicyVersion.Current ? "  ⚠ KHÁC công thức merge hiện tại (" + MergePolicyVersion.Current + ")" : "")}");

        Console.WriteLine();
        Console.WriteLine($"Findings ({e.Findings.Count}):");
        foreach (var f in e.Findings)
        {
            var policy = scanner.PolicyOf(f.RuleId);
            Console.WriteLine($"  - rule_id={f.RuleId,-24} score={f.Score:0.0000} count={f.Count} hard_rule={f.HardRule} " +
                $"validator_passed={f.ValidatorPassed?.ToString() ?? "n/a"} llm_policy={policy}");
        }

        Console.WriteLine();
        if (e.JobId == null)
        {
            Console.WriteLine("LLM job     : không có (chưa cần LLM, hoặc chưa tới lượt)");
            return;
        }

        Console.WriteLine($"LLM job       : job_id={e.JobId} status={e.JobStatus} attempts={e.JobAttempts} priority={e.JobPriority}");
        if (e.ModelVer == null)
        {
            Console.WriteLine("LLM result  : chưa có (job chưa xong)");
            return;
        }

        Console.WriteLine($"LLM result    : model_ver={e.ModelVer} prompt_ver={e.PromptVer} s2={e.S2:0.0000} " +
            $"confidence={e.Confidence:0.0000} latency_ms={e.LatencyMs}");
        Console.WriteLine($"json_out      : {e.JsonOut}");
    }
}
