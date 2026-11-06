// [Thay đổi] File mới (mục 5 - LLM giả): implement THẬT service Inference (contracts/inference.proto) để test round-trip
// hợp đồng gRPC/Named Pipe. Kết quả trả về được ĐIỀU KHIỂN qua tiền tố job_id (xem bảng dưới) — không phải AI thật,
// chỉ mô phỏng có kiểm soát các tình huống mục 5.4/7.2 cần xử lý: OK, BUSY, TIMEOUT, MODEL_ERROR, NOT_READY.
using Dlp.Inference;
using Grpc.Core;
using Status = Dlp.Inference.Status; // "Status" cũng có trong Grpc.Core (mã lỗi RPC) — chốt rõ dùng Status trong hợp đồng của mình

namespace DlpFakeLlm;

/// <summary>Cấu hình LLM giả — cho phép test giả lập LLM chạy SAI contract_ver (--contract-ver N khi "serve").</summary>
public sealed record FakeLlmOptions(uint ContractVer);

/// <summary>LLM giả: nhận job_id có tiền tố quy ước để giả lập từng tình huống, còn lại thì trả nhãn "ngẫu nhiên có
/// kiểm soát" (băm job_id → luôn ra cùng kết quả cho cùng job_id, đo lặp lại được).</summary>
public sealed class FakeInferenceService(FakeLlmOptions options) : Inference.InferenceBase
{
    /// <inheritdoc/>
    public override Task<Info> GetInfo(Empty request, ServerCallContext context) => Task.FromResult(new Info
    {
        Ready = true,
        ContractVer = options.ContractVer,
        ModelVer = "fake-llm-1.0",
        PromptVer = "fake-prompt-1",
        MaxChunks = 5,
        MaxInputTokens = 3000,
        QueueDepth = 0,
        RssBytes = 0
    });

    /// <inheritdoc/>
    public override async Task<ClassifyRes> Classify(ClassifyReq request, ServerCallContext context)
    {
        // [Thay đổi] mục 5.3: server cũng tự kiểm tra contract_ver, không chỉ trông chờ client đã gọi GetInfo trước
        if (request.ContractVer != options.ContractVer)
        {
            return new ClassifyRes { JobId = request.JobId, Status = Status.BadContract };
        }

        // Tiền tố job_id quy ước để giả lập từng tình huống (chỉ dùng cho tool test, KHÔNG phải hành vi model thật)
        if (request.JobId.StartsWith("BUSY-", StringComparison.Ordinal))
        {
            return new ClassifyRes { JobId = request.JobId, Status = Status.Busy };
        }
        if (request.JobId.StartsWith("NOTREADY-", StringComparison.Ordinal))
        {
            return new ClassifyRes { JobId = request.JobId, Status = Status.NotReady };
        }
        if (request.JobId.StartsWith("ERROR-", StringComparison.Ordinal))
        {
            return new ClassifyRes { JobId = request.JobId, Status = Status.ModelError };
        }
        if (request.JobId.StartsWith("TIMEOUT-", StringComparison.Ordinal))
        {
            // Ngủ lâu hơn deadline_ms rồi mới trả TIMEOUT — mô phỏng đúng mục 5.4 "kiểm tra trong vòng sinh token,
            // hết hạn thì dừng". Giới hạn 2s để bộ test không phải chờ quá lâu dù deadline_ms client gửi lớn.
            int sleepMs = Math.Min((int)request.DeadlineMs + 200, 2000);
            await Task.Delay(sleepMs, context.CancellationToken);
            return new ClassifyRes { JobId = request.JobId, Status = Status.Timeout };
        }
        if (request.JobId.StartsWith("HANG-", StringComparison.Ordinal))
        {
            // [Thay đổi] Test chịu lỗi: KHÔNG tự báo TIMEOUT như trên — cố tình treo vô thời hạn, chỉ dừng khi bị
            // huỷ (client hết deadline, hoặc process này bị giết). Mô phỏng "LLM treo thật", không phải LLM tự biết
            // dừng đúng hạn.
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        }
        if (request.JobId.StartsWith("GARBAGE-", StringComparison.Ordinal))
        {
            // [Thay đổi] Test chịu lỗi: phản hồi ĐÚNG khuôn dạng proto (không lỗi kỹ thuật) nhưng giá trị vô lý —
            // kiểm tra Worker không sụp đổ khi dữ liệu nhận về "trông hợp lệ nhưng vô nghĩa"
            var garbage = new ClassifyRes
            {
                JobId = request.JobId,
                ModelVer = "",
                PromptVer = "",
                Confidence = -999f,
                LatencyMs = uint.MaxValue,
                Status = Status.Ok
            };
            garbage.Labels.Add((Label)9999); // giá trị enum không nằm trong tập đóng đã chốt
            return garbage;
        }

        // Còn lại: "cố định/ngẫu nhiên có kiểm soát" — băm job_id làm hạt giống, CÙNG job_id luôn ra CÙNG kết quả
        var random = new Random(request.JobId.GetHashCode(StringComparison.Ordinal));
        var label = (Label)random.Next(0, Enum.GetValues<Label>().Length);
        float confidence = 0.5f + (float)random.NextDouble() * 0.49f;

        var response = new ClassifyRes
        {
            JobId = request.JobId,
            ModelVer = "fake-llm-1.0",
            PromptVer = "fake-prompt-1",
            Confidence = confidence,
            LatencyMs = 5,
            Status = Status.Ok
        };
        response.Labels.Add(label);

        // Mô phỏng "LLM xác nhận lại" mọi finding đã có trong request (mục 7.1 "LLM xác nhận hoặc phát hiện thêm")
        foreach (var finding in request.Findings)
        {
            response.Verdicts.Add(new Verdict { RuleId = finding.RuleId, TruePositive = true, Confidence = confidence });
        }

        return response;
    }

    /// <inheritdoc/>
    public override Task<Empty> Cancel(CancelReq request, ServerCallContext context) => Task.FromResult(new Empty());
}
