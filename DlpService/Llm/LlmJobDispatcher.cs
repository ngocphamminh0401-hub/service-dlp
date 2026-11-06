// [Thay đổi] File mới (mục 4.12 - Worker điều phối job): 1 lượt xử lý = lấy đúng 1 job vùng xám, kiểm tra file chưa
// đổi, dựng gói gửi LLM (ChunkSelector + Masker đã có), gọi LLM (GuardedInferenceClient đã có circuit breaker), rồi
// ghi kết quả. KHÔNG áp dụng công thức merge S_final (mục 7 - Merger, làm sau) — chỉ hoàn tất vòng đời của llm_job/
// llm_result cho đúng (không để job kẹt lại "running" mãi).
using System.Text.Json;
using Dlp.Inference;
using DlpService.Classification;
using DlpService.Reading;
using DlpService.Scanning;
using DlpService.Storage;
using DlpService.Text;
using Status = Dlp.Inference.Status;

namespace DlpService.Llm;

/// <summary>Điều phối 1 lượt xử lý job LLM. Gọi TryDispatchOnceAsync theo vòng lặp (Worker.cs) — hàm này LUÔN chạy
/// xong (await) trước khi vòng lặp gọi lại lần sau, nên tự nhiên chỉ có 1 job chạy đồng thời (mục 4.12).</summary>
public sealed class LlmJobDispatcher(
    ResultStore store,
    FileReaderService fileReader,
    RegexScanner scanner,
    GuardedInferenceClient llmClient,
    MachineIdleChecker idleChecker,
    LlmJobDispatcherOptions options,
    ILogger logger)
{
    // [Thay đổi] mục 5.3: nhớ lại contract_ver của LLM đã xác nhận khớp lần gần nhất — không phải gọi GetInfo mỗi
    // lượt. null = chưa xác nhận (mới khởi động, hoặc vừa mất kết nối) → lượt tới phải hỏi lại.
    private uint? _lastVerifiedServerContractVer;

    // Chỉ log cảnh báo 1 LẦN cho mỗi giá trị contract_ver sai khác nhau — tránh log lặp lại mỗi 10 giây nếu tình
    // trạng lệch phiên bản kéo dài (vẫn tiếp tục KIỂM TRA mỗi lượt, chỉ bớt spam log).
    private uint? _lastWarnedMismatchVer;

    /// <summary>Thử xử lý 1 job. Trả về mô tả ngắn để log (không chứa nội dung nhạy cảm) — "no_job", "not_idle:...",
    /// "circuit_open", "contract_mismatch:...", "stale_before", "stale_after", hoặc "done:&lt;status&gt;".</summary>
    public async Task<string> TryDispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        // Kiểm tra RẺ TIỀN trước (không đụng DB): mạch đang ngắt thì khỏi tốn công lấy job
        if (llmClient.State == CircuitState.Open)
        {
            _lastVerifiedServerContractVer = null; // mất kết nối -> lần kết nối lại phải xác nhận contract_ver từ đầu
            return "circuit_open";
        }

        if (!idleChecker.IsIdle(out string busyReason))
        {
            return "not_idle:" + busyReason;
        }

        // [Thay đổi] mục 5.3: "Mỗi lần kết nối, Regex service gọi GetInfo. Lệch contract_ver thì chuyển sang
        // regex-only... không gửi request sai schema." — kiểm tra TRƯỚC KHI lấy job, để không lỡ đưa job vào trạng
        // thái "running" rồi mới phát hiện không gửi được (job sẽ kẹt tới khi có cơ chế dọn riêng).
        if (_lastVerifiedServerContractVer != ContractVersion.Current)
        {
            var info = await llmClient.TryGetInfoAsync(TimeSpan.FromSeconds(5), cancellationToken);
            if (info == null)
            {
                return "circuit_open"; // không kết nối được — coi như chưa sẵn sàng, thử lại lượt sau
            }

            _lastVerifiedServerContractVer = info.ContractVer;
            if (info.ContractVer != ContractVersion.Current)
            {
                if (_lastWarnedMismatchVer != info.ContractVer)
                {
                    logger.LogWarning("[LLM] contract_ver lệch (Regex service={ours}, LLM={theirs}) — chuyển sang regex-only, KHÔNG gửi request nào cho tới khi khớp lại",
                        ContractVersion.Current, info.ContractVer);
                    _lastWarnedMismatchVer = info.ContractVer;
                }
                return $"contract_mismatch:server={info.ContractVer}";
            }
            _lastWarnedMismatchVer = null;
        }

        var job = store.TryPickNextJob();
        if (job == null)
        {
            return "no_job";
        }

        var extractor = fileReader.FindExtractor(job.Path);
        if (extractor == null)
        {
            store.MarkJobStale(job.JobId);
            return "stale_before:khong_con_ho_tro_dinh_dang";
        }

        List<ContentBlock> blocks;
        string flatTextBefore;
        try
        {
            blocks = extractor.Extract(job.Path).ToList();
            // [Thay đổi] Vá lỗi (buổi 11): FileWatcherService (Pass 1) chuẩn hoá NFC trước khi băm (Normalizer.ToNfc) —
            // thiếu bước này ở đây làm hash LUÔN lệch với mọi file tiếng Việt (dấu tổ hợp rời khác NFC), khiến job bị
            // đánh dấu "stale" sai dù nội dung không đổi (đã tái hiện thật: 100% job thật báo stale_before).
            flatTextBefore = Normalizer.ToNfc(string.Join("\n", blocks.Select(b => b.Text)));
        }
        catch (Exception)
        {
            // Không trích lại được (file bị xoá/khoá/hỏng) -> không có gì để so sánh, phải coi như đã đổi (an toàn hơn)
            store.MarkJobStale(job.JobId);
            return "stale_before:khong_trich_lai_duoc";
        }

        if (ContentHash.Compute(flatTextBefore) != job.FileHash)
        {
            store.MarkJobStale(job.JobId);
            return "stale_before:noi_dung_da_doi";
        }

        var findings = store.GetFindings(job.FileId);
        var chunks = ChunkSelector.SelectChunks(extractor, job.Path, Path.GetFileName(job.Path), findings, options.TokenBudget);
        var maskedChunks = chunks.Select(c => Masker.Mask(c, scanner)).Where(c => c != null).Cast<ChunkResult>().ToList();

        var request = new ClassifyReq
        {
            JobId = job.JobId,
            ContractVer = ContractVersion.Current,
            Meta = BuildMeta(job.Path, blocks),
            DeadlineMs = (uint)options.JobDeadline.TotalMilliseconds
        };
        request.Chunks.AddRange(maskedChunks.Select(c => new Chunk { Kind = c.Kind, Rule = c.Rule ?? "", Text = c.Text }));
        request.Findings.AddRange(findings.Select(f => new Finding
        {
            RuleId = f.RuleId,
            Score = (float)f.Score,
            ValidatorPassed = f.ValidatorPassed ?? false,
            Count = (uint)f.Count,
            Location = f.Location,
            HardRule = f.HardRule
        }));

        var response = await llmClient.TryClassifyAsync(request, cancellationToken);

        if (response == null)
        {
            store.FailJob(job.JobId, "khong_ket_noi_duoc_llm");
            return "done:no_response";
        }
        if (response.Status == Status.Timeout)
        {
            store.TimeoutJob(job.JobId);
            return "done:timeout";
        }
        if (response.Status == Status.OutputTooLong)
        {
            // [Thay đổi] Lỗi VĨNH VIỄN của file này: LLM sinh tham lam nên gửi lại cùng nội dung sẽ ra cùng kết quả bị cắt
            // — không thử lại (tránh tốn thêm nhiều phút mỗi lần), chốt file giữ điểm regex + needs_review.
            store.FailJobPermanently(job.JobId, "OUTPUT_TOO_LONG");
            return "done:output_too_long";
        }
        if (response.Status != Status.Ok)
        {
            // BUSY / NOT_READY / BAD_CONTRACT: LLM có trả lời nhưng chưa làm được việc — thử lại sau như 1 lỗi tạm
            store.FailJob(job.JobId, $"llm_status_{response.Status}");
            return "done:" + response.Status;
        }

        // [Thay đổi] Kiểm tra file_hash LẦN 2 (mục 4.12: "sau khi nhận: kiểm tra lại lần nữa") — file có thể đã đổi
        // TRONG LÚC chờ LLM trả lời (có thể mất tới hàng chục giây)
        string flatTextAfter;
        try
        {
            flatTextAfter = Normalizer.ToNfc(string.Join("\n", extractor.Extract(job.Path).Select(b => b.Text)));
        }
        catch (Exception)
        {
            store.MarkJobStale(job.JobId);
            return "stale_after:khong_trich_lai_duoc";
        }

        if (ContentHash.Compute(flatTextAfter) != job.FileHash)
        {
            store.MarkJobStale(job.JobId);
            return "stale_after:noi_dung_da_doi_trong_luc_cho";
        }

        // [Thay đổi] Merger mục 7: đổi Verdict (kiểu gRPC) sang RuleVerdict (kiểu thuần của Classification) rồi merge
        // với S1 + findings đã có — ra S_final/needs_review/source thật, ghi đè scan_result cùng transaction với llm_result.
        string jsonOut = JsonSerializer.Serialize(new
        {
            labels = response.Labels.Select(l => l.ToString()).ToArray(),
            verdicts = response.Verdicts.Select(v => new { rule_id = v.RuleId, true_positive = v.TruePositive, confidence = v.Confidence }).ToArray()
        });

        var ruleVerdicts = response.Verdicts
            .Select(v => new RuleVerdict(v.RuleId, v.TruePositive, v.Confidence))
            .ToList();
        var merge = Merger.MergeFile(job.S1, findings, ruleVerdicts, scanner.PolicyOf);

        store.CompleteJob(job.JobId, new LlmResultRecord(jsonOut, response.Confidence, response.Confidence,
            response.ModelVer, response.PromptVer, (int)response.LatencyMs),
            merge: new ScanMergeUpdate(merge.SFinal, merge.NeedsReview, merge.Source, MergePolicyVersion.Current),
            fileId: job.FileId, fileHash: job.FileHash);

        logger.LogInformation("[LLM] Job {jobId} xong: {count} nhãn, {verdicts} verdict, tin cậy {confidence:0.00}",
            job.JobId, response.Labels.Count, response.Verdicts.Count, response.Confidence);
        return "done:ok";
    }

    // Meta.title = Heading đầu tiên; Meta.columns = tên cột (TableHeader) — cùng nguồn dữ liệu ChunkSelector dùng,
    // nhưng đây là trường CÓ CẤU TRÚC (Meta message) khác với chunk "metadata" dạng text tự do.
    private static Meta BuildMeta(string path, List<ContentBlock> blocks)
    {
        var meta = new Meta
        {
            FileType = Path.GetExtension(path),
            FileName = Path.GetFileName(path),
            ParentDir = Path.GetFileName(Path.GetDirectoryName(path)) ?? "",
            Title = blocks.FirstOrDefault(b => b.Kind == BlockKind.Heading)?.Text ?? "",
            SizeBucket = SizeBucket(path)
        };
        meta.Columns.AddRange(blocks.Where(b => b.Kind == BlockKind.TableHeader).Select(b => b.Text).Distinct());
        return meta;
    }

    private static string SizeBucket(string path)
    {
        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return "unknown";
        }
        return bytes switch
        {
            < 10 * 1024 => "<10KB",
            < 100 * 1024 => "10-100KB",
            < 1024 * 1024 => "100KB-1MB",
            _ => ">1MB"
        };
    }
}

/// <summary>Cấu hình LlmJobDispatcher, đọc từ appsettings.json mục "Llm".</summary>
public sealed class LlmJobDispatcherOptions
{
    /// <summary>Ngân sách token gửi LLM mỗi file (mục 4.10: 1.500-3.000).</summary>
    public int TokenBudget { get; set; } = 2000;

    /// <summary>Deadline mỗi job (mục 4.12: mặc định 60 giây).</summary>
    public TimeSpan JobDeadline { get; set; } = TimeSpan.FromSeconds(60);
}
