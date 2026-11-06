// [Thay đổi] File mới (mục 6.2/6.3/6.4 - LLM host thật): implement service Inference (contracts/inference.proto)
// trên nền ModelHost thật. GetInfo báo đúng trạng thái nạp model + RAM tiến trình thật. Classify sinh câu trả lời
// thật (PromptBuilder + ModelHost.GenerateAsync + ClassifyOutputSchema), ép constrained decoding khi runtime hỗ
// trợ, tự kiểm tra deadline_ms trong vòng sinh, Cancel dừng được job đang chạy (qua JobCancellationRegistry), và
// chia nhỏ nhiều snippet thành các batch tối đa 8 (mục 6.4) thay vì nhồi hết vào 1 lần gọi.
using System.Diagnostics;
using Dlp.Inference;
using Grpc.Core;
using Status = Dlp.Inference.Status;

namespace DlpLlmHost;

public sealed class InferenceHostService(ModelHost modelHost, JobCancellationRegistry cancellationRegistry) : Inference.InferenceBase
{
    private const uint ContractVer = 1; // khớp DlpService/Llm/ContractVersion.cs + contracts/inference.proto
    private const string PromptVer = "sensitivity-4level-1";
    private const int BatchSize = 8; // mục 6.4: "batch nhỏ 4 đến 8 snippet trong cùng một prompt khi cùng file"

    /// <inheritdoc/>
    public override Task<Info> GetInfo(Empty request, ServerCallContext context) => Task.FromResult(new Info
    {
        Ready = modelHost.State == ModelHostState.Ready,
        ContractVer = ContractVer,
        ModelVer = modelHost.ModelVer,
        PromptVer = PromptVer,
        MaxChunks = 5,
        MaxInputTokens = 3000,
        QueueDepth = 0,
        RssBytes = (ulong)Process.GetCurrentProcess().WorkingSet64
    });

    /// <inheritdoc/>
    public override async Task<ClassifyRes> Classify(ClassifyReq request, ServerCallContext context)
    {
        if (request.ContractVer != ContractVer)
        {
            return new ClassifyRes { JobId = request.JobId, Status = Status.BadContract };
        }

        // [Thay đổi] mục 6.4: "kiểm tra deadline_ms trong vòng sinh token" — TỰ áp deadline_ms của request, không
        // chỉ trông chờ deadline của kênh gRPC. Đăng ký vào registry để Cancel tìm được.
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (request.DeadlineMs > 0)
        {
            jobCts.CancelAfter(TimeSpan.FromMilliseconds(request.DeadlineMs));
        }
        cancellationRegistry.Register(request.JobId, jobCts);
        // [Thay đổi] Log chẩn đoán MODEL_ERROR/TIMEOUT: ghi đầu-cuối mỗi job (chỉ con số + mã trạng thái, không nội dung).
        var jobWatch = Stopwatch.StartNew();
        DiagLog.Write($"job={request.JobId} bat dau: chunks={request.Chunks.Count} ky_tu={request.Chunks.Sum(c => c.Text.Length)} findings={request.Findings.Count} deadline_ms={request.DeadlineMs} model_state={modelHost.State} ws={DiagLog.WorkingSetMb()}MB");
        try
        {
            var response = await ClassifyCore(request, jobCts.Token);
            string detail = "";
            if (response.Status == Status.Timeout)
            {
                // Phân biệt 3 nguyên nhân huỷ: kênh gRPC của client đóng/hết hạn, hết deadline_ms phía server, hay RPC Cancel
                detail = context.CancellationToken.IsCancellationRequested ? " nguyen_nhan=kenh_grpc_cua_client_dong_hoac_het_han"
                    : jobWatch.ElapsedMilliseconds >= (long)request.DeadlineMs - 500 ? " nguyen_nhan=het_deadline_ms_phia_server"
                    : " nguyen_nhan=bi_huy_boi_RPC_Cancel";
            }
            DiagLog.Write($"job={request.JobId} ket thuc: status={response.Status} tong={jobWatch.ElapsedMilliseconds}ms{detail} ws={DiagLog.WorkingSetMb()}MB");
            return response;
        }
        finally
        {
            cancellationRegistry.Unregister(request.JobId);
        }
    }

    private async Task<ClassifyRes> ClassifyCore(ClassifyReq request, CancellationToken cancellationToken)
    {
        var loadWatch = Stopwatch.StartNew();
        try
        {
            await modelHost.EnsureLoadedAsync(cancellationToken);
            DiagLog.Write($"job={request.JobId} nap model xong ({loadWatch.ElapsedMilliseconds}ms gom cho cong hash + nap), state={modelHost.State}");
        }
        catch (OperationCanceledException)
        {
            DiagLog.Write($"job={request.JobId} bi huy luc cho/nap model sau {loadWatch.ElapsedMilliseconds}ms");
            return new ClassifyRes { JobId = request.JobId, Status = Status.Timeout };
        }
        catch (Exception ex)
        {
            // Log lỗi thật (không phải nội dung file/chunk — an toàn để log) thay vì nuốt hoàn toàn, để chẩn đoán
            // được các lỗi runtime native (hết bộ nhớ cấp phát KV-cache, model hỏng...) khi vận hành thật.
            Console.Error.WriteLine($"[llm-host] EnsureLoadedAsync lỗi (job_id={request.JobId}): {ex.Message}");
            DiagLog.Write($"job={request.JobId} NAP MODEL LOI sau {loadWatch.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message} (ws={DiagLog.WorkingSetMb()}MB)");
            return new ClassifyRes { JobId = request.JobId, Status = Status.ModelError };
        }

        var sw = Stopwatch.StartNew();

        // [Thay đổi] mục 6.4 "batch nhỏ 4-8 snippet": chia request.Chunks thành các nhóm tối đa BatchSize, mỗi
        // nhóm gọi model riêng (vẫn tái dùng prefix cache — xem ModelHost), rồi gộp kết quả (lấy mức nhạy cảm CAO
        // NHẤT trong các batch — enum SensitivityLevel đã được đánh số ĐÚNG theo thứ tự mức độ nên so sánh trực
        // tiếp bằng int là đúng ý nghĩa; mục 7.3: "nếu các chunk mâu thuẫn thì coi là không chắc" -> need_review=true
        // khi các batch KHÔNG đồng nhất).
        var chunkTexts = request.Chunks.Select(c => c.Text).ToList();
        var batches = chunkTexts.Chunk(BatchSize).ToList();
        if (batches.Count == 0)
        {
            batches.Add([]);
        }

        ClassifyOutput? best = null;
        bool anyDisagreement = false;
        uint totalGeneratedTokens = 0;
        // [Thay đổi] mục 7 (nợ buổi 9/10): gom Verdicts từ MỌI batch — KHÔNG khử trùng lặp theo rule_id ở đây, vì
        // Merger.MergeFile (DlpService) đã tự gom theo rule_id và xử lý đồng thuận/mâu thuẫn (mục 7.3).
        var allVerdicts = new List<Verdict>();
        int batchIndex = 0;
        foreach (var batch in batches)
        {
            batchIndex++;
            var (output, status, generatedTokens) = await ClassifyOneBatchAsync(batch, request.Findings, cancellationToken,
                $"job={request.JobId} batch={batchIndex}/{batches.Count}", TokenCapFor(request.Findings.Count));
            totalGeneratedTokens += (uint)generatedTokens;
            if (status != Status.Ok || output == null)
            {
                return new ClassifyRes { JobId = request.JobId, ModelVer = modelHost.ModelVer, PromptVer = PromptVer, Status = status, GeneratedTokens = totalGeneratedTokens };
            }

            allVerdicts.AddRange(output.Verdicts);

            if (best == null)
            {
                best = output;
            }
            else if ((int)output.Level != (int)best.Level)
            {
                anyDisagreement = true;
                if ((int)output.Level > (int)best.Level)
                {
                    best = output;
                }
            }
        }

        var response = new ClassifyRes
        {
            JobId = request.JobId,
            ModelVer = modelHost.ModelVer,
            PromptVer = PromptVer,
            Status = Status.Ok,
            LatencyMs = (uint)sw.ElapsedMilliseconds,
            SensitivityLevel = best!.Level,
            MatchedRule = best.Rule,
            NeedReview = best.NeedReview || anyDisagreement,
            Reason = best.Reason,
            GeneratedTokens = totalGeneratedTokens
        };
        response.Verdicts.AddRange(allVerdicts);
        return response;
    }

    /// <summary>Sinh + validate cho ĐÚNG 1 batch (tối đa <see cref="BatchSize"/> snippet). Trả (kết quả, OK, số
    /// token đã sinh) khi thành công, hoặc (null, mã lỗi, số token đã sinh ở LẦN THỬ CUỐI) khi hết lượt retry vẫn
    /// không ra JSON hợp lệ / bị huỷ / lỗi model — vẫn trả số token để benchmark tính tokens/s kể cả job lỗi.</summary>
    // [Thay đổi] Trần token MỚI theo số findings của job. Đầu ra gồm phần cố định (~45 token) + reason (≤120 ký tự ≈ 40 token)
    // + mỗi verdict ≈ 30 token (số verdict ≈ số findings, +1 nếu model phát hiện thêm). 150 + 40×findings luôn dư so với
    // ước lượng đó (6 findings: cần ~295, trần 390) nhưng đủ chặt để lượt "chạy lạc" dừng sau vài chục giây thay vì
    // hàng trăm giây. Kẹp [200, 800]: sàn cho file không có finding, trần cứng cho file nhiều finding (verdicts tối đa 16).
    private static int TokenCapFor(int findingCount) => Math.Clamp(150 + 40 * findingCount, 200, 800);

    private async Task<(ClassifyOutput? Output, Status Status, int GeneratedTokens)> ClassifyOneBatchAsync(IEnumerable<string> maskedChunkTexts, IEnumerable<Finding> findings, CancellationToken cancellationToken, string logTag, int tokenCap)
    {
        string suffix = PromptBuilder.BuildSuffix(maskedChunkTexts, findings);
        int lastGeneratedTokens = 0;
        var batchWatch = Stopwatch.StartNew();
        // [Thay đổi] Lỗi "chắc chắn lặp lại": sinh tham lam (do_sample=false) nên cùng đầu vào cho cùng đầu ra. Nếu lượt
        // sinh vừa rồi hỏng VÀ bị cắt do chạm trần (hoặc giống hệt lượt trước), sinh lại chỉ tốn thêm ~100 giây vô ích.
        bool deterministicFailure = false;
        string? previousText = null;

        // [Thay đổi] mục 6.4: "dùng constrained decoding/grammar nếu runtime hỗ trợ; nếu không thì validate JSON
        // sau khi sinh và retry tối đa 1 lần; sau đó trả MODEL_ERROR" — ModelHost đã ép JsonSchema ở tầng nạp model
        // (dùng chung cho mọi job); ở đây VẪN validate+retry như lưới an toàn thứ 2.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            GenerateResult result;
            try
            {
                result = await modelHost.GenerateAsync(suffix, cancellationToken, tokenCap);
            }
            catch (OperationCanceledException)
            {
                DiagLog.Write($"{logTag} lan {attempt + 1}/2: BI HUY sau {batchWatch.ElapsedMilliseconds}ms ke tu dau batch (chi tiet token da sinh: xem dong 'gen bi huy' ngay truoc)");
                return (null, Status.Timeout, lastGeneratedTokens);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[llm-host] GenerateAsync lỗi: {ex.Message}");
                // [Thay đổi] "bad allocation" ở lượt sinh ĐẦU TIÊN sau khi nạp model (log chẩn đoán: 4/4 lần nạp, 0 token đã sinh,
                // lượt sau tự khỏi): thử lại đúng 1 lần thay vì bỏ cả job. An toàn vì ModelHost.GenerateSync luôn RewindTo về
                // điểm cuối prefix trong khối finally, và chưa có token nào được sinh nên không có gì bị lặp.
                if (attempt == 0 && ex.Message.Contains("bad allocation", StringComparison.OrdinalIgnoreCase))
                {
                    DiagLog.Write($"{logTag} lan 1/2: bad allocation khi bat dau sinh -> thu lai 1 lan (ws={DiagLog.WorkingSetMb()}MB)");
                    continue;
                }
                DiagLog.Write($"{logTag} lan {attempt + 1}/2: GenerateAsync LOI {ex.GetType().Name}");
                return (null, Status.ModelError, lastGeneratedTokens);
            }
            lastGeneratedTokens += result.TokenCount;

            bool parsedOk = ClassifyOutputSchema.TryParse(result.Text, out var parsed, out string? parseError);
            // Chỉ ghi LOẠI lỗi parse (thông báo cố định của ClassifyOutputSchema), KHÔNG ghi văn bản model sinh ra.
            // Lỗi JSON thô (JsonException) có thể kèm ký tự của văn bản đó nên gom về 1 nhãn chung.
            string parseNote = parsedOk ? "parse=ok"
                : "parse=HONG(" + (parseError != null && parseError.StartsWith("Không parse được JSON", StringComparison.Ordinal)
                    ? "json_khong_hop_le" : parseError) + ")";
            string tail = result.Text.TrimEnd().EndsWith('}') ? "ket_thuc_bang_}" : "KHONG_ket_thuc_bang_}(co_the_bi_cat_do_dung_tran_token)";
            double tps = result.GenMs > 0 ? result.TokenCount * 1000.0 / result.GenMs : 0;
            bool hitCap = result.HitTokenCap || (result.AllowedNewTokens > 0 && result.TokenCount >= result.AllowedNewTokens - 1);
            // [Thay đổi] Khi parse hỏng, ghi thêm HÌNH DẠNG đầu ra (chỉ các con số đếm, không có chữ nào của model) để biết
            // đoạn nào chạy lạc: reason dài vô tận, quá nhiều verdict, hay chuỗi chữ số quá dài.
            string shape = parsedOk ? "" : " " + DescribeShape(result.Text);
            DiagLog.Write($"{logTag} lan {attempt + 1}/2: dau_vao={result.SuffixTokens}tok sinh={result.TokenCount}tok/tran={result.AllowedNewTokens} trong {result.GenMs}ms ({tps:0.0}tok/s) cham_tran={hitCap} do_dai_text={result.Text.Length} {tail} {parseNote}{shape}");

            if (parsedOk)
            {
                return (parsed, Status.Ok, lastGeneratedTokens);
            }

            // Hỏng + bị cắt do chạm trần, hoặc giống hệt lượt trước -> sinh lại vô ích (tham lam: cùng vào, cùng ra).
            if (hitCap || result.Text == previousText)
            {
                deterministicFailure = true;
                DiagLog.Write($"{logTag}: loi chac chan lap lai (cham_tran={hitCap}, giong_luot_truoc={result.Text == previousText}) -> khong sinh lai");
                break;
            }
            previousText = result.Text;
        }

        // [Thay đổi] OUTPUT_TOO_LONG = lỗi vĩnh viễn của file này (DlpService không thử lại); MODEL_ERROR = lỗi tạm như cũ.
        var finalStatus = deterministicFailure ? Status.OutputTooLong : Status.ModelError;
        DiagLog.Write($"{logTag}: khong ra JSON hop le -> {finalStatus} (tong {batchWatch.ElapsedMilliseconds}ms)");
        return (null, finalStatus, lastGeneratedTokens);
    }

    // Chỉ các con số đếm về hình dạng văn bản model sinh ra — tuyệt đối KHÔNG trả về/ghi nội dung chữ (quy tắc không log
    // dữ liệu nhạy cảm nguyên văn).
    private static string DescribeShape(string text)
    {
        int verdictObjects = 0;
        for (int i = text.IndexOf("\"rule_id\"", StringComparison.Ordinal); i >= 0; i = text.IndexOf("\"rule_id\"", i + 1, StringComparison.Ordinal))
        {
            verdictObjects++;
        }

        string reasonInfo = "reason=khong_thay";
        int reasonStart = text.IndexOf("\"reason\":\"", StringComparison.Ordinal);
        if (reasonStart >= 0)
        {
            int from = reasonStart + "\"reason\":\"".Length;
            int end = text.IndexOf("\",\"verdicts\"", from, StringComparison.Ordinal);
            reasonInfo = end >= 0 ? $"reason_len={end - from}(da_dong)" : $"reason_len>={text.Length - from}(CHUA_dong)";
        }

        int run = 0, longestDigitRun = 0, whitespace = 0, newlines = 0, longestWhitespaceRun = 0, wsRun = 0;
        foreach (char c in text)
        {
            run = char.IsDigit(c) ? run + 1 : 0;
            longestDigitRun = Math.Max(longestDigitRun, run);
            // [Thay đổi] Đếm khoảng trắng để kiểm chứng nghi vấn "model sinh khoảng trắng liên tục" (JSON schema cho phép
            // khoảng trắng tùy ý giữa các thành phần). Chỉ là số đếm.
            if (char.IsWhiteSpace(c))
            {
                whitespace++;
                wsRun++;
                longestWhitespaceRun = Math.Max(longestWhitespaceRun, wsRun);
                if (c == '\n')
                {
                    newlines++;
                }
            }
            else
            {
                wsRun = 0;
            }
        }
        return $"[hinh_dang: verdict_objs={verdictObjects} {reasonInfo} chuoi_so_dai_nhat={longestDigitRun} khoang_trang={whitespace}/{text.Length} xuong_dong={newlines} chuoi_khoang_trang_dai_nhat={longestWhitespaceRun}]";
    }

    /// <inheritdoc/>
    public override Task<Empty> Cancel(CancelReq request, ServerCallContext context)
    {
        cancellationRegistry.TryCancel(request.JobId);
        return Task.FromResult(new Empty());
    }
}
