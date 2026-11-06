# Buổi 9 — LLM host thật: nạp model ONNX INT4, kiểm SHA-256, vòng đời, GetInfo thật, prompt/schema 4 mức nhạy cảm

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái | Mục kiến trúc |
|---|---|---|
| Tải model Phi-4-mini-instruct-onnx (CPU int4, `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4`) | ✅ Xong | 6.2 |
| Project `tools/llm-host/DlpLlmHost` — service LLM thật, thay dần `DlpFakeLlm` | ✅ Xong | 6.2, 6.3 |
| `manifest.json` + kiểm SHA-256 khi khởi động, dừng ngay nếu sai | ✅ Xong, có test end-to-end | 6.2, mục 8 "Model bị thay" |
| Nạp lười (lazy-load ở lượt Classify đầu), tự unload sau N phút rảnh, warm-up | ✅ Xong, đã test qua gRPC thật (Named Pipe) | 6.3 |
| `GetInfo` thật (ready, rss_bytes, model_ver, contract_ver, prompt_ver) | ✅ Xong | 6.3, 5.2 |
| `Classify` sinh câu trả lời thật — phân loại 4 mức nhạy cảm (PUBLIC/INTERNAL/CONFIDENTIAL/RESTRICTED), SONG SONG với `Label`/`Verdict` theo rule_id cũ | ✅ Xong, test qua gRPC thật ra JSON đúng schema | 6.4, 6.5 (mở rộng thêm field mới) |
| Constrained decoding (`SetGuidance("json_schema", ...)`) khi runtime hỗ trợ + validate/retry làm lưới an toàn | ✅ Xong | 6.4 |
| Tự áp `deadline_ms` của request trong vòng sinh (không chỉ trông chờ deadline kênh gRPC) | ✅ Xong, test đúng (trả `TIMEOUT` sớm khi đặt deadline ngắn) | 6.4 |
| `Cancel` dừng được job đang chạy | ⚠️ Có tác dụng nhưng KHÔNG "sạch"/tức thời — xem giới hạn ở mục 4.c | 6.4 |
| **Prefix cache** (Generator dùng chung + `RewindTo`) — thay vì tính lại prefill ~1300+ token mỗi job | ✅ Xong, đo thật: **~46.5s → ~3.2-3.8s/job** (nhanh hơn ~14 lần) | 6.4 |
| **Batch nhỏ tối đa 8 snippet/lần gọi**, gộp kết quả nhiều batch (lấy mức cao nhất) | ✅ Xong, test 10 snippet → tách 2 batch → gộp đúng `RESTRICTED` | 6.4 |
| **Job Object giới hạn RAM (10240MB) + CPU (50%) + priority BelowNormal** | ✅ Xong, test thật (xem mục 4.d) | 6.1, 6.3, 8 |
| **Pipe ACL theo SID** (server) + **client kiểm tra process phía server** (PID + đường dẫn exe, tuỳ chọn chữ ký) | ✅ Xong, test thật cả 2 phía (mục 4.e) | 5.5 |
| `Classify` sinh `Label`/`Verdict` theo 56 rule/26 nhãn cũ (xác nhận/bác bỏ từng `Finding.rule_id`) | ❌ Chưa làm | 6.5, 7 |
| **Benchmark thật trên dữ liệu nhãn có sẵn** (`tools/perf-llm/DlpLlmBenchmark`, dùng chung `D:\dlp-classifier\rawdata` với `DlpBaseline`) — p50/p95, tokens/s, RAM, tỷ lệ `MODEL_ERROR`, độ chính xác | ✅ Xong, chạy thật 5 mẫu (mục 4.f) — **⚠️ kết quả đáng lo, xem mục 4.f** | 13.1, 13.3 |
| **Đăng ký Windows Service thật** — virtual account, `sc sidtype`, recovery, delayed-auto-start, firewall chặn outbound | ⚠️ Code + script xong, ĐÃ publish, **CHƯA thực thi cài đặt thật** (xin xác nhận trước — xem mục 4.g) | 6.1, 6.3, 14 |

---

## 1. Tải model (mục 6.2)

- Nguồn: `microsoft/Phi-4-mini-instruct-onnx` trên Hugging Face, nhánh `cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4` (INT4, chạy CPU — chưa dùng bản DirectML).
- Lệnh tải dùng `hf` (CLI mới của `huggingface-hub` 2.0, thay `huggingface-cli` đã đổi tên) — cần gọi bằng đường dẫn đầy đủ tới `Scripts\hf.exe` vì thư mục đó chưa nằm trong PATH của máy này.
- Model đặt ở `D:\Service-DLP\models\phi-4-mini-instruct-onnx\cpu_and_mobile\cpu-int4-rtn-block-32-acc-level-4` — **nằm ngoài repo Git** (đúng nguyên tắc "model tải riêng, offline" mục 6.2/17), không commit.

## 2. `DlpLlmHost` — service LLM thật (mục 6.2, 6.3)

Project mới `tools/llm-host/DlpLlmHost` (`Microsoft.NET.Sdk.Web`, dùng lại `contracts/inference.proto` như `DlpFakeLlm`, package `Microsoft.ML.OnnxRuntimeGenAI` cho suy luận thật):

- `ModelManifest.cs`: đọc/ghi `manifest.json` (danh sách file model + SHA-256), băm lại file thật trên đĩa để so khớp (`Verify`). `Generate`/`Save` chỉ chạy thủ công 1 lần (`dotnet run -- genmanifest`) khi thiết lập model — không tự chạy lúc `serve`.
- `ModelHost.cs`: quản lý vòng đời model thật —
  - `EnsureLoadedAsync`: nạp lười (chỉ khi có job đầu tiên gọi tới), khoá bằng `SemaphoreSlim` để không nạp trùng nếu nhiều request tới cùng lúc; nạp xong chạy ngay 1 lượt sinh cực ngắn (warm-up, `max_length=4`) vì lượt sinh đầu luôn chậm hơn hẳn.
  - `RunIdleUnloadLoopAsync`: vòng lặp nền (`PeriodicTimer` 30s) — rảnh quá `idle-unload-minutes` (mặc định 10) thì tự `Unload()`, trả RAM.
  - Trạng thái `Unloaded/Loading/Ready/Failed` — `Failed` không tự phục hồi thành `Unloaded` (giữ nguyên để không lặp lại lỗi nạp âm thầm; sẽ cần cân nhắc thêm khi có Classify thật).
- `InferenceHostService.cs`: implement `Inference.InferenceBase` thật —
  - `GetInfo` trả `ready` theo đúng `ModelHost.State`, `rss_bytes` đo bằng `Process.GetCurrentProcess().WorkingSet64` (số RAM THẬT, không phải số giả như `DlpFakeLlm`).
  - `Classify`: kiểm `contract_ver` → gọi `EnsureLoadedAsync` (lazy-load/warm-up) → ghép prompt (`PromptBuilder`) → `ModelHost.GenerateAsync` → parse (`ClassifyOutputSchema`) → **sai thì thử lại đúng 1 lần** (mục 6.4) → vẫn sai thì `MODEL_ERROR`; đúng thì điền `SensitivityLevel/MatchedRule/NeedReview/Reason` + `Status=OK`. `Labels/Verdicts/Evidence` (theo 56 rule cũ) vẫn để trống — CHƯA làm, là việc khác.
  - `Cancel`: no-op (chưa có hàng đợi để huỷ).
- `Program.cs`: 2 chế độ — `genmanifest` (tạo `manifest.json`) và `serve` (kiểm SHA-256 → dừng ngay nếu sai → dựng Kestrel qua Named Pipe → `MapGrpcService<InferenceHostService>` → chạy song song vòng lặp tự unload).

## 3. Prompt + schema output cho phân loại 4 mức nhạy cảm (mục 6.4/6.5 — mở rộng THÊM, không thay hợp đồng cũ)

Người dùng đưa 1 system prompt hoàn chỉnh (tiếng Việt, có few-shot, output ép JSON 1 dòng) để phân loại **CẢ gói** thành 1 trong 4 mức `PUBLIC/INTERNAL/CONFIDENTIAL/RESTRICTED` kèm `matched_rule` (8 giá trị) — khác hẳn `enum Label` (26 giá trị theo `rule_id`) đã chốt ở buổi 8. Quyết định: **thêm song song**, không xoá gì:

- `contracts/inference.proto`: thêm `enum SensitivityLevel`, `enum MatchedRule`, và 4 field mới (10-13) vào `ClassifyRes` — field tuỳ chọn, **không tăng `contract_ver`** (đúng mục 5.3).
- `PromptBuilder.cs` (mới): giữ nguyên văn system prompt người dùng đưa; `BuildPrompt` ghép với "gói" (nối các `Chunk.Text` đã mask) — **quan trọng: dùng đúng khuôn chat (`chat_template`) của Phi-4-mini-instruct** lấy từ `tokenizer_config.json` của model: `<|system|>...<|end|><|user|>...<|end|><|assistant|>`. Thiếu khuôn này đã bị phát hiện qua test thật (xem mục 4.a).
- `ClassifyOutputSchema.cs` (mới): parse + validate nghiêm ngặt dòng JSON (đúng 4 giá trị `label`, đúng 8 giá trị `matched_rule`, `need_review` phải là bool, `reason` phải là string) — sai bất kỳ phần nào thì coi là lỗi, không "sửa hộ".
- `ModelHost.cs`: thêm `GenerateAsync` — decode kiểu tham lam (`do_sample=false`, KHÔNG sampling ngẫu nhiên) = tương đương temperature 0 (mục 6.4), `max_length = số_token_prompt + max_new_tokens`.
- `InferenceHostService.Classify`: nối tất cả lại, có retry 1 lần khi JSON sai (mục 6.4).
- `GeneratorParams.SetGuidance("json_schema", ...)` (constrained decoding — ONNX Runtime GenAI 0.9.x hỗ trợ `json_schema`/`regex`/`lark_grammar`, xem `ort_genai_c.h`): ép model chỉ sinh đúng khuôn JSON đã định — bọc `try/catch`, thất bại thì âm thầm bỏ qua, dựa vào validate+retry làm lưới an toàn (đúng mục 6.4: "constrained decoding nếu hỗ trợ; nếu không thì validate+retry").
- `JobCancellationRegistry.cs` (mới): service gRPC tạo instance MỚI mỗi request nên `Classify`/`Cancel` không tự chia sẻ state — cần 1 singleton map `job_id -> CancellationTokenSource` để `Cancel` tìm đúng job đang chạy. `Classify` tự áp `deadline_ms` của request (`CancelAfter`) thay vì chỉ dựa vào deadline kênh gRPC.

## 4. Test qua gRPC thật (Named Pipe)

### a. Vòng 1 — thiếu khuôn chat (phát hiện lỗi)

Gửi thẳng `SystemPrompt + document` KHÔNG có token `<|system|>/<|user|>/<|assistant|>` → model không tuân theo vai trò assistant, sinh lan man, **365 giây** vẫn không ra JSON hợp lệ (2/2 lần thử đều fail) → `MODEL_ERROR`. Kiểm tra `tokenizer_config.json` thấy `chat_template` yêu cầu đúng khuôn trên → sửa `PromptBuilder.BuildPrompt`.

### b. Vòng 2 — sau khi sửa khuôn chat

| Bước | Kết quả đo được |
|---|---|
| `GetInfo` trước khi có job | `ready=false`, RAM ~60-68MB |
| `Classify("Danh sách khách hàng VIP quý 3: Nguyễn Thị Hoa, SĐT 0938123456, số dư tiết kiệm 1.200.000.000 VNĐ.")` (câu KHÔNG có trong few-shot) | `status=OK`, `level=CONFIDENTIAL`, `matched_rule=PII_BASIC`, `reason="PII tài chính của cá nhân cụ thể"` — **đúng schema, tổng quát hoá tốt** (không phải học vẹt ví dụ) |
| Thời gian | Tải+warm-up ~15s, sinh JSON ~46.5s (tổng ~61.4s cho 1 job) |
| RAM sau khi nạp | ~3.5-4.9GB (khớp ước lượng 2-3GB của mục 6.2, hơi nhỉnh hơn) |

(Cơ chế idle-unload đã test kỹ ở lần chạy trước — xem lịch sử; không lặp lại ở vòng test này.)

**⚠️ Đáng lo về hiệu năng:** ~46.5 giây để sinh xong 1 JSON ngắn (dưới 100 token) trên CPU máy này — CHẬM HƠN NHIỀU so với ước lượng lạc quan ở mục 13 ("10-20 token/giây"). Cần đo lại với nhiều mẫu hơn và cân nhắc: prompt hệ thống hiện khá dài (nhiều few-shot) làm chậm prefill; có thể cần rút gọn prompt, dùng bản DirectML (GPU), hoặc chấp nhận hàng chục giây/file là bình thường cho máy đích thật.

### c. `deadline_ms` và `Cancel` — test thật, phát hiện giới hạn quan trọng

| Kịch bản | Kết quả |
|---|---|
| `deadline_ms=5000` (ngắn hơn nhiều so với ~46.5s sinh bình thường) | Trả `TIMEOUT` sau ~8.5s — ĐÚNG, dừng sớm hơn hẳn chạy hết. Sau đó gọi lại `Classify` bình thường vẫn thành công (không bị kẹt trạng thái lỗi vĩnh viễn dù bị huỷ giữa lúc đang nạp model) |
| Gọi `Classify` (deadline 300s) rồi gọi `Cancel(job_id)` ở giây thứ 3 | Job vẫn chạy tới **~44.1 giây** mới trả về (`TIMEOUT`) — **CÓ dừng nhưng KHÔNG "sạch"/tức thời** như kỳ vọng |

**Nguyên nhân (suy luận từ số đo, chưa cô lập bằng thực nghiệm riêng):** vòng lặp sinh token có kiểm tra `cancellationToken.ThrowIfCancellationRequested()` trước MỖI token — nhưng đây là 1 lệnh gọi C API đồng bộ (chặn luồng), không thể ngắt giữa chừng MỘT lệnh gọi. Với prompt hệ thống dài (~1300+ token, nhiều few-shot), bước "prefill" (xử lý toàn bộ prompt lần đầu) nhiều khả năng nằm gọn trong lệnh `GenerateNextToken()` ĐẦU TIÊN và chiếm gần hết thời gian (~40+ giây trong tổng ~46.5s) — `Cancel` gọi ở giây thứ 3 rơi đúng vào giữa lệnh gọi này, chỉ được phát hiện SAU KHI lệnh đó trả về (gần cuối). Đây khớp với ghi chú đã có sẵn ở mục 6.4 của tài liệu kiến trúc: "Prefix cache cho system prompt và few-shot cố định (phần lặp lại nhiều nhất)" — **CHƯA làm**, và chính là thứ sẽ giảm hẳn chi phí prefill lặp lại mỗi lần gọi.

**Kết luận trung thực (lúc viết mục 4.c):** `deadline_ms`/`Cancel` đã nối đúng dây (code-level đúng, có test xác nhận `TryCancel`/`CancelAfter` hoạt động), nhưng độ phản hồi bị giới hạn bởi việc suy luận ONNX Runtime GenAI là API đồng bộ — không "dừng sạch" giữa chừng một lệnh gọi native đang chạy. Đã giải quyết phần lớn ở mục 4.d bên dưới (prefix cache) — job thứ 2 trở đi không còn tốn ~40s prefill, nên cửa sổ "không ngắt được" cũng ngắn theo.

## 4.d Prefix cache + batch nhỏ + Job Object — làm mục 6.4/6.3 còn thiếu

**1. Prefix cache (mục 6.4 "Prefix cache cho system prompt và few-shot cố định — phần lặp lại nhiều nhất"):** ONNX Runtime GenAI 0.9.x không có API "prefix cache" tường minh, nhưng có `Generator.RewindTo(newLength)` ("rewind về 1 độ dài đã cho, tiếp tục sinh từ đó") — dùng đúng API này: giữ **1 Generator dùng chung** suốt vòng đời model (thay vì tạo mới mỗi job), nạp prompt CỐ ĐỊNH (`PromptBuilder.Prefix` — system prompt + `<|system|>...<|end|><|user|>`) 1 LẦN lúc load (đây cũng chính là warm-up), rồi mỗi job: `AppendTokens` phần "đuôi" riêng của job (tài liệu + `<|end|><|assistant|>`), sinh xong thì LUÔN `RewindTo` lại đúng điểm "vừa xong prefix" (kể cả khi job lỗi/bị huỷ — dùng `finally`) để job sau tái dùng đúng.

**2. Batch nhỏ tối đa 8 snippet (mục 6.4):** `InferenceHostService.Classify` chia `request.Chunks` thành các nhóm tối đa 8 bằng `Chunk()` (LINQ), gọi model riêng cho từng nhóm (vẫn qua Generator dùng chung ở trên), rồi gộp: lấy `SensitivityLevel` **cao nhất** trong các batch (enum đã đánh số đúng theo mức độ nên so `int` trực tiếp là đúng ý nghĩa — PUBLIC=0 < INTERNAL=1 < CONFIDENTIAL=2 < RESTRICTED=3), `need_review=true` nếu các batch không đồng nhất (mục 7.3: "nếu các chunk mâu thuẫn thì coi là không chắc").

**3. Job Object (mục 6.1/6.3/8 "Job Object giới hạn RAM và CPU... priority thấp"):** `ResourceLimiter.cs` (P/Invoke `CreateJobObject`/`SetInformationJobObject`/`AssignProcessToJobObject`) áp giới hạn RAM (`JOBOBJECT_EXTENDED_LIMIT_INFORMATION.JobMemoryLimit`) + CPU (`JOBOBJECT_CPU_RATE_CONTROL_INFORMATION`, hard cap) cho CHÍNH tiến trình `DlpLlmHost`, cộng `Process.PriorityClass = BelowNormal`. Best-effort — lỗi chỉ log cảnh báo, không chặn service chạy (mục 6.1 coi đây là lớp phòng thủ thêm).

**Test thật + phát hiện quan trọng về RAM:**

| Bước | Kết quả |
|---|---|
| Job 1 (lần đầu — nạp model + xây prefix cache + sinh) | Ban đầu THẤT BẠI (`OnnxRuntimeGenAIException: bad allocation` / "Could not allocate the key-value cache buffer... max_length (5362)") — xem phân tích dưới |
| Job 2 (model đã Ready, prefix đã cache) — sau khi sửa | `status=OK`, latency **~3.2-3.8 giây** (so với ~46.5s trước khi có prefix cache — **nhanh hơn ~14 lần**) |
| Batch 10 snippet (>8, tự tách 2 batch: 8 thường + 2 gồm 1 private key giả) | `status=OK`, `level=RESTRICTED`, `matched_rule=AUTH_SECRET`, `need_review=true` — **đúng**: gộp đúng mức cao nhất |

**Nguyên nhân lỗi cấp phát ban đầu, đã sửa:** Generator dùng chung phải cấp phát SẴN bộ nhớ KV-cache cho `max_length` TỐI ĐA (prefix ~1300 + suffix tối đa + token mới), khác hẳn thiết kế cũ (mỗi lần tạo Generator mới chỉ cần đúng độ dài thực tế đang dùng). Với `maxSuffixTokens=3000` (cận trên ngân sách mục 4.10) → `max_length≈5362` → cấp phát thất bại. Máy test chỉ có **13.7GB RAM tổng, ~6GB rảnh** lúc đo. Đã thử tăng dần giới hạn Job Object: 4096MB và 6144MB đều KHÔNG đủ (vẫn lỗi cấp phát), 10240MB chạy ổn định (đã test cả prefix-cache lẫn batch). Đã giảm `maxSuffixTokens` xuống 1500 (cận dưới ngân sách mục 4.10) và chốt mặc định `--memory-limit-mb 10240` (thay vì ước lượng RAM cũ 2-3GB ở mục 6.2 — **con số đó không còn đúng sau khi có prefix cache**, cần ghi nhớ khi triển khai thật trên máy đích).

**Điểm cần lưu ý:** thời gian nạp model + xây prefix cache lần đầu (đo lúc RAM đủ) là **~137-141 giây** — CHẬM HƠN hẳn so với trước (~15-20s nạp, chưa tính prefill riêng ~46.5s trong lần gọi đầu = tổng cũ ~61-65s). Có thể do bước prefill 1 lần cho TOÀN BỘ prefix dài hơn tổng số bước nhỏ trước đây cộng lại, hoặc do máy đang thiếu RAM vật lý phải dùng page file. Đánh đổi hợp lý cho service chạy lâu dài (trả giá 1 lần, mọi job sau nhanh hẳn), nhưng cần đo lại trên máy đích thật và cân nhắc nếu service bị `idle-unload` quá thường xuyên (phải trả giá ~140s lại mỗi lần).

## 4.e Pipe ACL theo SID + client kiểm tra process phía server (mục 5.5)

**Server (`DlpLlmHost`):** `PipeAcl.cs` dựng `PipeSecurity` chỉ cấp `ReadWrite | CreateNewInstance` cho ĐÚNG 1 SID (mặc định: user Windows hiện tại — chưa có tài khoản dịch vụ ảo thật kiểu `NT SERVICE\RegexSvc`, đó là việc cài đặt/mục 6.1, chưa làm; có thể đổi qua `--allowed-account`). Không thêm ACE nào khác nên mọi SID không được liệt kê bị hệ điều hành từ chối thẳng, không tới được tầng gRPC.

- **Phát hiện qua test:** Kestrel Named Pipes transport mặc định bật `PipeOptions.CurrentUserOnly`, và .NET CẤM kết hợp cờ này với `PipeSecurity` tuỳ chỉnh (`ArgumentException`) — phải tự viết `CreateNamedPipeServerStream` (qua `NamedPipeTransportOptions`, cấu hình bằng `builder.WebHost.UseNamedPipes(...)`) để bỏ cờ đó và tự áp ACL.
- **Phát hiện thứ 2:** chỉ cấp `PipeAccessRights.ReadWrite` thì bị "Access denied" ngay cả với tên pipe HOÀN TOÀN MỚI (chưa ai từng dùng) — vì Kestrel tạo NHIỀU instance của cùng pipe để phục vụ nhiều kết nối đồng thời (pool), và tạo instance thứ 2 trở đi cần thêm quyền `PipeAccessRights.CreateNewInstance`.
- **`FirstPipeInstance` (chống pipe squatting):** hoá ra là hành vi MẶC ĐỊNH sẵn có của Kestrel Named Pipes transport, không cần code riêng — tự kiểm chứng được qua chính các lỗi `UnauthorizedAccessException` gặp phải suốt buổi khi lỡ chạy 2 server trùng tên pipe (đặc trưng của `FILE_FLAG_FIRST_PIPE_INSTANCE` bị từ chối, không phải lỗi "hết instance" thông thường).

**Client (`DlpService/Llm`):** `PipeServerVerifier.cs` — sau khi `NamedPipeClientStream.ConnectAsync` thành công, gọi P/Invoke `GetNamedPipeServerProcessId` lấy PID phía server, tra `Process.GetProcessById(pid).MainModule.FileName`, so với đường dẫn exe mong đợi (cấu hình `Llm:ServerExePath`); không khớp thì ném exception ngay trong `ConnectCallback` (gRPC coi như lỗi kết nối, `TryGetInfoAsync`/`TryClassifyAsync` trả `null` như bình thường — vẫn đúng nguyên tắc P3, không làm giảm độ sẵn sàng). Có thêm kiểm tra chữ ký Authenticode (`X509CertificateLoader.LoadCertificateFromFile`) nhưng TẮT mặc định (`RequireServerSignature=false`) vì dự án chưa có hạ tầng ký code — bật khi triển khai thật đã ký binary. **An toàn là từ chối**: mọi lỗi xác minh (không lấy được PID, không mở được process...) đều coi là thất bại, không phải "bỏ qua coi như hợp lệ".

**Test thật (2 kịch bản, viết client ngoài repo dùng đúng `InferenceClient.ConnectNamedPipe` thật):**

| Kịch bản | Kết quả |
|---|---|
| Đường dẫn exe mong đợi ĐÚNG (`DlpLlmHost.exe` thật) | Kết nối được, `GetInfo` trả lời bình thường — PASS |
| Đường dẫn exe mong đợi SAI (`notepad.exe` giả) | `TryGetInfoAsync` trả `null` (bị từ chối, thoái hoá an toàn) — PASS |

**Chưa test được (do hạn chế môi trường):** ACL từ chối 1 SID/tài khoản KHÁC thật sự (cần máy nhiều user hoặc `runas`) — chỉ kiểm chứng được logic "chỉ 1 SID được liệt kê" qua code + tài liệu Windows, chưa có bằng chứng thực nghiệm cho trường hợp 2 tài khoản khác nhau trên cùng máy.

## 4.f Benchmark thật trên dữ liệu nhãn có sẵn (mục 13.1/13.3)

**Phát hiện quan trọng:** dự án đã có sẵn 1 bộ dữ liệu nhãn THẬT ở `D:\dlp-classifier\rawdata` (12.743 mẫu, 16 thư mục domain×nhãn: finance/healthcare/hr/insurance × public/internal/confidential/top_secret) — CÙNG bộ mà `tools/eval/DlpBaseline` đang dùng để đo baseline regex. Quan trọng hơn: `DlpService.Classification.SensitivityLevel` (Public/Internal/Confidential/Restricted) đã tồn tại SẴN và CHÍNH LÀ 4 mức mà prompt LLM (mục 4.b buổi này) đang phân loại — không phải khái niệm mới. Vì vậy benchmark KHÔNG tạo bộ dữ liệu mới (đúng nguyên tắc DRY) mà dùng lại bộ này, cho phép so khớp trực tiếp với nhãn thật.

`tools/perf-llm/DlpLlmBenchmark` (mới) — quy trình: `FileReaderService` trích văn bản thật (docx) → `ChunkSelector.SelectChunks` (không có `Finding` thật, nên coi như "không có vùng nghi vấn cụ thể" — vẫn đúng logic ngân sách token) → `Masker.Mask` (mask hai lớp như luồng thật, mục 4.11) → gửi qua `InferenceClient.ConnectNamedPipe` THẬT tới `DlpLlmHost` đang chạy → so `SensitivityLevel` trả về với nhãn thật. Ghi CSV (1 dòng/mẫu) + Markdown tóm tắt (p50/p95, tokens/s, RAM, tỷ lệ `MODEL_ERROR`, ma trận nhầm lẫn) vào `tools/perf-llm/DlpLlmBenchmark/results/`, đúng quy ước đã có ở `tools/eval/DlpBaseline`.

Thêm 1 field mới vào hợp đồng để đo tokens/s: `ClassifyRes.generated_tokens` (field 14, tuỳ chọn, không tăng `contract_ver`) — `ModelHost.GenerateAsync` trả kèm số token THẬT đã sinh (`GenerateResult` record), cộng dồn qua các batch.

**Test thật (5 mẫu tài liệu tài chính/y tế thật, sau khi sửa 2 lỗi dùng benchmark — xem dưới):**

| Chỉ số | Kết quả |
|---|---|
| p50 độ trễ | **66.0 giây** |
| p95 độ trễ | **72.4 giây** |
| tokens/s trung bình | **0.5** |
| RAM tiến trình | 4-5.3 GB |
| Tỷ lệ `MODEL_ERROR` (ước lượng lỗi parse) | 0/5 (0%) |
| Độ chính xác so nhãn thật | 1/5 (20%) — mẫu quá nhỏ, không kết luận được, nhưng đáng chú ý: 1 mẫu `top_secret` bị dự đoán THẤP hơn (`Confidential`) — hướng sai nguy hiểm hơn cho DLP so với dự đoán cao hơn |

**⚠️ Phát hiện đáng lo nhất buổi này:** với TÀI LIỆU THẬT (không phải câu test 1 dòng), độ trễ quay lại mức **~53-72 giây/job** — gần bằng mức TRƯỚC KHI có prefix cache (~46.5s), dù model đã "ấm" (prefix đã cache). Nguyên nhân: `ChunkSelector` chọn nhiều đoạn (metadata + tối đa 5 ngữ cảnh + mẫu đại diện) tạo ra "đuôi" (suffix) dài, và suffix này KHÔNG nằm trong prefix cache — vẫn phải prefill mỗi lần, giống hệt vấn đề mà prefix cache đã giải quyết cho phần system prompt. Prefix cache chỉ giải quyết được phần LẶP LẠI CỐ ĐỊNH (system prompt), không giúp được phần THAY ĐỔI THEO FILE (nội dung tài liệu) — đây là giới hạn cố hữu của kỹ thuật này, không phải lỗi triển khai.

**2 lỗi dùng benchmark đã gặp và sửa trong lúc chạy (không phải lỗi hệ thống):**
1. `--deadline-ms` mặc định/tự chọn ban đầu (60s) ngắn hơn thời gian nạp model lần đầu (~137-239s, dao động) → thêm bước "job mồi" (warm-up, deadline 5 phút, KHÔNG tính vào số liệu) trước khi đo.
2. Ngay cả sau warm-up, 60s vẫn không đủ cho tài liệu thật (do phát hiện ở trên) → tăng lên 180s để đo được số liệu thật thay vì toàn `TIMEOUT`.

## 4.g Đăng ký Windows Service thật (mục 6.1/6.3/14) — CHƯA THỰC THI, xin xác nhận

Theo đúng nguyên tắc "hành động khó đảo ngược/ảnh hưởng hệ thống chung thì phải hỏi trước": tạo Windows Service, đổi ACL thư mục, thêm firewall rule là thay đổi TỒN TẠI LÂU DÀI trên máy (khác hẳn các lần chạy `dotnet run`/kill process tạm thời suốt buổi nay). Đã chuẩn bị đầy đủ, CHƯA bấm nút cài thật:

- `DlpLlmHost.csproj` + `Program.cs`: thêm `Microsoft.Extensions.Hosting.WindowsServices`, gọi `UseWindowsService()` (chỉ có tác dụng khi THẬT SỰ chạy dưới SCM — khi chạy tay qua `dotnet run` như suốt buổi nay thì tự nhận ra và không làm gì khác, đã test lại để chắc chắn không phá luồng dev hiện tại).
- `dotnet publish -c Release -o tools/llm-host/DlpLlmHost/publish --self-contained false` — ĐÃ chạy, có `DlpLlmHost.exe` sẵn sàng (publish chỉ sinh file cục bộ, không đổi state hệ thống, nên đã tự làm).
- `tools/llm-host/DlpLlmHost/deploy/install-service.ps1` (mới) — `sc create` (tài khoản ảo `NT SERVICE\LlmSvc`, `start= delayed-auto`), `sc sidtype unrestricted` (mục 5.5 cần SID riêng để Pipe ACL dùng), `sc failure` (recovery: tự khởi động lại khi lỗi, mục 6.3), `icacls` cấp quyền ĐỌC thư mục model cho tài khoản ảo (mục 14), `New-NetFirewallRule` chặn outbound theo đường dẫn exe (mục 6.1/8). Đã kiểm tra cú pháp PowerShell (`Parser]::ParseFile`, không chạy thật) — sửa 1 lỗi encoding (file thiếu BOM UTF-8 làm PowerShell 5.1 đọc sai ký tự tiếng Việt, gây lỗi parse).
- `tools/llm-host/DlpLlmHost/deploy/uninstall-service.ps1` (mới) — đường lùi: `sc delete` + xoá firewall rule, để việc cài đặt có thể hoàn tác sạch.

**Cần bạn xác nhận trước khi tôi (hoặc bạn) chạy `install-service.ps1` (yêu cầu quyền Administrator):**
```powershell
.\tools\llm-host\DlpLlmHost\deploy\install-service.ps1 `
  -ExePath "D:\Service-DLP\tools\llm-host\DlpLlmHost\publish\DlpLlmHost.exe" `
  -ModelDir "D:\Service-DLP\models\phi-4-mini-instruct-onnx\cpu_and_mobile\cpu-int4-rtn-block-32-acc-level-4"
```
Sau khi chạy: service `LlmSvc` được TẠO nhưng KHÔNG tự start (script chỉ đăng ký, để bạn kiểm tra trước khi chạy thật) — dùng `Start-Service LlmSvc` khi sẵn sàng, hoặc `.\uninstall-service.ps1` để gỡ hoàn toàn nếu muốn hoãn lại.

## 5. Việc có thể làm tiếp (theo đúng thứ tự phụ thuộc)

| Việc | Mục kiến trúc | Ghi chú |
|---|---|---|
| **Prefix cache cho phần "đuôi" thay đổi theo file, hoặc rút ngắn suffix** (mục 4.f phát hiện: suffix dài — nhiều ngữ cảnh/mẫu — kéo lại độ trễ về gần mức trước khi có prefix cache) | 6.4, 13.3 | Việc QUAN TRỌNG NHẤT còn lại về hiệu năng — 66-72s/job thật hiện tại nhiều khả năng không chấp nhận được |
| **Chạy benchmark với cỡ mẫu lớn hơn** (`--limit` cao hơn 5, tiến dần tới 500-2000 theo mục 13.1) trên máy đủ RAM (máy test hiện chỉ ~6GB rảnh/13.7GB tổng) | 6.2, 13.1 | Đã có công cụ (`tools/perf-llm/DlpLlmBenchmark`) — chỉ cần thời gian chạy dài hơn (mỗi job hiện ~1 phút) |
| `Classify` sinh thêm `Label`/`Verdict` theo 56 rule/26 nhãn cũ (xác nhận/bác bỏ từng `Finding.rule_id`) — cần prompt/schema RIÊNG, khác hẳn prompt 4 mức này | 6.5, 7 | Việc lớn còn thiếu để nối vào công thức merge `S_final` (mục 7) |
| Đổi `DlpService/Llm/InferenceClient.cs` (qua cấu hình) trỏ sang `DlpLlmHost` thay vì `DlpFakeLlm` khi test end-to-end thật | — | Nên làm sau khi đã có cả 2 loại output (4 mức + Label/Verdict), hoặc quyết định Worker dùng loại nào trước |
| Test ACL từ chối tài khoản THẬT khác (cần máy nhiều user hoặc tài khoản dịch vụ `NT SERVICE\RegexSvc` + `sc sidtype`) | 5.5 | Hiện mới test "SID đúng thì vào, exe sai thì bị chặn" — chưa test "SID sai thì bị chặn" bằng thực nghiệm |
| Đặt tên pipe không đoán trước được (kèm GUID cài đặt) thay vì tên cố định `dlp-llm-host` | 5.5 | Phần còn lại của mục 5.5, chưa làm |
| Tài khoản dịch vụ quyền thấp + chặn mạng ra (Windows Firewall theo exe) cho `DlpLlmHost` | 6.1, 8 | Chưa làm — Job Object (RAM/CPU) + priority + Pipe ACL đã xong, đây là phần còn lại |
| Bản DirectML (model + package `Microsoft.ML.OnnxRuntimeGenAI.DirectML` khác CPU) | 6.2 | Có thể cần thiết nếu hiệu năng CPU không đạt |
| Merger — `S_final` (mục 7, vẫn còn nợ từ buổi 8) | 7, 4.13 | Chờ có `Label`/`Verdict` thật trước |

---

## 6. Bảng file đã thay đổi/thêm mới trong buổi 9

| File | Thay đổi | Lý do |
|---|---|---|
| `tools/llm-host/DlpLlmHost/DlpLlmHost.csproj` (mới, sửa lại giữa buổi) | Project mới, `Sdk.Web` + `Microsoft.ML.OnnxRuntimeGenAI` + `Grpc.AspNetCore` + `Microsoft.Extensions.Hosting.WindowsServices`, dùng chung `contracts/inference.proto` | Mục 6.2, 14 |
| `tools/llm-host/DlpLlmHost/deploy/install-service.ps1`, `uninstall-service.ps1` (mới) | `sc create`/`sidtype`/`failure`, `icacls`, `New-NetFirewallRule` — CHƯA thực thi, chỉ kiểm tra cú pháp (mục 4.g) | Mục 6.1, 6.3, 14 |
| `tools/llm-host/DlpLlmHost/ModelManifest.cs` (mới) | Đọc/ghi/kiểm SHA-256 `manifest.json` | Mục 6.2, mục 8 "Model bị thay" |
| `tools/llm-host/DlpLlmHost/ModelHost.cs` (mới, sửa lại giữa buổi) | Vòng đời model thật: lazy-load, idle-unload; **prefix cache** — 1 Generator dùng chung, nạp prefix 1 lần lúc load (= warm-up), `RewindTo` sau mỗi job; `SetGuidance` constrained decoding, decode tham lam = temperature 0 | Mục 6.3, 6.4 |
| `tools/llm-host/DlpLlmHost/PromptBuilder.cs` (mới, tách lại giữa buổi) | Tách `Prefix` (cố định, dùng cho prefix cache) và `BuildSuffix` (phần đổi theo job/batch) — trước đó gộp chung `BuildPrompt` | Mục 6.4, 6.5 |
| `tools/llm-host/DlpLlmHost/ClassifyOutputSchema.cs` (mới) | Parse/validate nghiêm ngặt JSON output 4 mức + JSON Schema dùng cho constrained decoding | Mục 6.4 |
| `tools/llm-host/DlpLlmHost/JobCancellationRegistry.cs` (mới) | Singleton map `job_id -> CancellationTokenSource` để `Cancel` tìm đúng job (service gRPC transient, không tự chia sẻ state) | Mục 6.4 |
| `tools/llm-host/DlpLlmHost/ResourceLimiter.cs` (mới) | P/Invoke Job Object: giới hạn RAM (`JobMemoryLimit`) + CPU (`JOBOBJECT_CPU_RATE_CONTROL_INFORMATION` hard cap) + `Process.PriorityClass=BelowNormal`, best-effort | Mục 6.1, 6.3, 8 |
| `tools/llm-host/DlpLlmHost/InferenceHostService.cs` (mới, sửa lại giữa buổi) | Implement `GetInfo`/`Classify`/`Cancel` thật — chia `request.Chunks` thành batch tối đa 8, gộp kết quả (mức cao nhất), retry 1 lần/batch, tự áp `deadline_ms`, log lỗi thật thay vì nuốt hoàn toàn | Mục 6.2, 6.3, 6.4 |
| `tools/llm-host/DlpLlmHost/PipeAcl.cs` (mới) | `PipeSecurity` chỉ cho 1 SID (`ReadWrite \| CreateNewInstance`) + `CreateSecuredServerStream` (bỏ `PipeOptions.CurrentUserOnly` mặc định của Kestrel để dùng ACL riêng) | Mục 5.5 |
| `tools/llm-host/DlpLlmHost/Program.cs` (sửa lại giữa buổi) | 2 chế độ: `genmanifest`, `serve` (+ `--memory-limit-mb`, `--cpu-rate-percent`, `--allowed-account`); gọi `ResourceLimiter` sớm nhất có thể; `UseNamedPipes` áp `PipeAcl`; giảm `maxSuffixTokens` 3000→1500 sau khi phát hiện lỗi cấp phát | Mục 5.5, 6.2, 6.3 |
| `contracts/inference.proto` (`ClassifyRes` + 2 enum mới, + `generated_tokens`) | Thêm `SensitivityLevel`/`MatchedRule`/`NeedReview`/`Reason`/`GeneratedTokens` — field tuỳ chọn, không tăng `contract_ver` | Mục 5.3, 6.5, 13.3 |
| `DlpService/Llm/PipeServerVerifier.cs` (mới) | `GetNamedPipeServerProcessId` (P/Invoke) + so PID → đường dẫn exe (+ chữ ký tuỳ chọn) với giá trị mong đợi | Mục 5.5 |
| `DlpService/Llm/InferenceClient.cs` (sửa) | `ConnectNamedPipe` nhận thêm `PipeServerTrustPolicy?` tuỳ chọn, gọi `PipeServerVerifier.Verify` ngay sau khi kết nối | Mục 5.5 |
| `DlpService/Worker.cs` (sửa) | Đọc config `Llm:ServerExePath`/`Llm:RequireServerSignature`, dựng `PipeServerTrustPolicy` (null nếu chưa cấu hình — chưa ép buộc) | Mục 5.5 |
| `tools/perf-llm/DlpLlmBenchmark/*` (mới: `.csproj`, `SampleLoader.cs`, `LevelMapper.cs`, `Program.cs`, `BenchReport.cs`) | Benchmark LLM thật trên `D:\dlp-classifier\rawdata` (bộ dữ liệu nhãn có sẵn, dùng chung với `DlpBaseline`) — p50/p95, tokens/s, RAM, tỷ lệ `MODEL_ERROR`, độ chính xác | Mục 13.1, 13.3 |
| `models/phi-4-mini-instruct-onnx/...` (tải về, ngoài repo) | Model INT4 CPU thật + `manifest.json` (tự sinh) | Mục 6.2 — không commit |
