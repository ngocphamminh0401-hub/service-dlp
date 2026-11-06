# Buổi 8 — Pipeline LLM: chunk, mask, hợp đồng gRPC, Worker điều phối, test chịu lỗi

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái | Mục kiến trúc |
|---|---|---|
| Chunk selector (chọn đoạn gửi LLM theo ngân sách token) | ✅ Xong | 4.10 |
| Masker hai lớp + huỷ chunk còn dính rule cứng | ✅ Xong | 4.11 |
| Test canary (giá trị đánh dấu không được lọt vào gói) | ✅ Xong, PASS | mục 8 |
| Đánh dấu `needs_ocr`, không đẩy LLM | ✅ Xong | 1.3, 4.8 |
| Chốt `.proto`/schema, `contract_ver = 1`, nhãn + evidence (tập đóng, dùng enum) | ✅ Xong | 5.2, 6.5 |
| LLM giả chạy process riêng (gRPC thật qua Named Pipe) | ✅ Xong | 5 |
| Client phía Regex (kết nối pipe, GetInfo/Classify/Cancel, deadline) | ✅ Xong | 5 |
| Circuit breaker (closed/open/half-open) + backoff | ✅ Xong | 4.12 |
| Worker điều phối job (rảnh máy, 1 job đồng thời, kiểm tra file_hash trước/sau) | ✅ Xong | 4.12 |
| Kiểm tra `contract_ver` trước khi gửi (chuyển regex-only nếu lệch) | ✅ Xong (phát hiện thiếu, đã sửa) | 5.3 |
| Test chịu lỗi LLM (kill giữa chừng, treo, phản hồi rác) | ✅ Xong, 10/10 PASS | mục 8 |
| **Merger — cập nhật `s_final`/nhãn cuối theo LLM** | ❌ Chưa làm | 7 |
| Kiểm tra "không có phiên người dùng đang hoạt động" | ❌ Chưa làm | 4.12 |
| PID/chữ ký process (client) + PipeSecurity theo SID (server) | ❌ Chưa làm | 5.5 |
| Model ONNX thật (Phi-4-mini-instruct) | ❌ Chưa làm — đang dùng LLM giả | 6.2 |

---

## 1. Chunk selector — chọn đoạn gửi LLM theo ngân sách token (mục 4.10)

- `Llm/TokenEstimator.cs`: 1 token ≈ 3 ký tự tiếng Việt, làm tròn LÊN ("chừa dư" — thà ước lượng dư còn hơn thiếu).
- `Llm/ChunkSelector.cs`: tự trích lại file qua `IContentExtractor` (Pass 2 riêng, không đụng `FileReaderService`/`Scanning` đang chạy thật), dựng lại `flatText` **giống hệt cách FileReaderService ghép** (nối `\n`, dừng ở cùng `MaxExtractedChars`) để offset trong `finding.location` (Pass 1) khớp đúng vị trí ở Pass 2. Chọn theo 4 bước, dừng khi hết ngân sách:
  1. Metadata (tên file, tiêu đề, sheet, cột) — 15%
  2. Ngữ cảnh quanh match vùng xám (±300 ký tự, tối đa 5 đoạn) — 50%
  3. Mẫu đại diện (bảng: 5-10 dòng đầu + vài dòng rải rác; văn bản: đầu/giữa/cuối) — 35%
  4. Phần còn lại → thay bằng thống kê (đếm theo rule)
- File nhỏ hơn ngân sách → gửi nguyên văn (`kind = "full"`).
- **Giới hạn đã biết:** `finding.location` chỉ lưu 1 offset/rule (offset nhỏ nhất) → bước 2 thực tế là "tối đa 1 đoạn/rule", chưa phải "nhiều vị trí của cùng 1 rule" như mô tả đầy đủ ở mục 4.10.

## 2. Masker hai lớp (mục 4.11)

- `Llm/Masker.cs`: **Lớp 1** quét lại chunk bằng đúng bộ rule chính (`RegexScanner`), thay match bằng token giữ hình dạng (`<EMAIL>`, `<PHONE>`, `<CARD>`, `<DATE>`, `<MONEY>`, `<NUM{n}>`, fallback `<PII>`); secret thì `<SECRET len=N entropy=...>` — **giữ nguyên nhãn** ("password=") đứng trước, chỉ che giá trị thật (sửa 1 lỗi phát hiện lúc test: ban đầu tính nhầm độ dài/entropy trên cả nhãn).
- **Lớp 2**: 2 regex "rộng" không validator (email tổng quát, chuỗi ≥8 số liền) bắt phần lớp 1 bỏ lọt.
- **Kiểm tra sau mask**: quét lại lần nữa bằng rule chính; còn chỗ khớp đã qua validator checksum (`Validated = true`) → **huỷ cả chunk**, không gửi.
- Test canary (`tools/canary/DlpCanaryTest`): chèn CCCD/SĐT hợp lệ, email, secret entropy cao, số "lạ" vào 5 định dạng file (txt/csv/docx/xlsx/eml) → **không giá trị nào lọt** vào gói cuối cùng.

## 3. `needs_ocr` — phân biệt "sạch thật" với "không đọc được" (mục 1.3, 4.8)

- `FileReadResult.NeedsOcr`: bật khi file có dung lượng nhưng không trích được chữ nào (PDF scan).
- `scan_result.needs_ocr` (cột mới, có `ALTER TABLE` cho DB cũ — `CREATE TABLE IF NOT EXISTS` không tự thêm cột cho bảng đã tồn tại).
- `EscalationPolicy.Decide`: `needsOcr = true` → luôn từ chối gửi LLM, lý do tường minh "cần OCR" thay vì trùng hợp bị chặn bởi bộ lọc "quá ít text".

## 4. Chốt hợp đồng gRPC (mục 5.2, 6.5)

- `contracts/inference.proto`: bản chốt chính thức, **`contract_ver = 1`**. Phát hiện: doc kiến trúc tham chiếu "phụ lục 17.1/17.2" cho danh sách nhãn/evidence nhưng **chưa từng được viết** — đã điền trực tiếp vào mục 6.5.
- `labels`/`evidence` chuyển từ `repeated string` (chỉ đóng bằng quy ước) sang **`enum Label`** (26 giá trị, gộp 56 rule trong `rules/`) và **`enum EvidenceCode`** (6 giá trị, khớp đúng loại chunk của ChunkSelector) — "tập đóng" được ép buộc ở tầng schema.
- Enum C# tương ứng 1-1: `Llm/ContractVersion.cs`, `Llm/Label.cs`, `Llm/EvidenceCode.cs` (viết tay, dùng được trước khi có codegen).
- **Nợ kỹ thuật:** sau khi có codegen thật từ `.proto` (mục 5 bên dưới), enum viết tay này giờ **trùng lặp** với `Dlp.Inference.Label`/`EvidenceCode` sinh ra từ proto — cần hợp nhất khi làm Worker/Merger thật.

## 5. LLM giả + Client phía Regex (mục 5)

- `tools/fake-llm/DlpFakeLlm`: dựng THẬT gRPC qua Named Pipe (không phải gọi hàm C# trực tiếp) — `Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes` đã có sẵn trong shared framework net10.0, **không cần** cài package riêng. Điều khiển kịch bản qua tiền tố `job_id`: `BUSY-`, `TIMEOUT-`, `ERROR-`, `NOTREADY-`, `HANG-` (treo vô hạn), `GARBAGE-` (trả lời đúng khuôn dạng nhưng giá trị vô lý); còn lại → nhãn "ngẫu nhiên có kiểm soát" (băm `job_id`). Thêm `--contract-ver N` để giả lập LLM chạy sai phiên bản.
- `DlpService/Llm/InferenceClient.cs`: "đầu dây" phía Regex service — `ConnectNamedPipe`, `TryGetInfoAsync`, `TryClassifyAsync`, `CancelAsync`. **Mọi hàm đều nuốt lỗi kết nối, trả `null`** thay vì ném exception (nguyên tắc P3: LLM lỗi → thoái hoá về regex-only, không dừng). 2 lớp deadline: `deadline_ms` trong request (LLM tự biết dừng) + deadline riêng ở client (`deadline_ms` + 2s dư, phòng LLM treo không tự báo).

## 6. Circuit breaker + backoff (mục 4.12)

- `Llm/CircuitBreaker.cs`: máy trạng thái Closed/Open/HalfOpen kiểu cầu dao tự ngắt — lỗi liên tiếp quá ngưỡng (mặc định 3) thì ngắt (mặc định 2 phút); hết giờ thì dò thử ĐÚNG 1 lần; dò thất bại thì ngắt lại **lâu hơn** (backoff x2, tối đa 30 phút); dò thành công thì đóng lại, hết backoff.
- `Llm/GuardedInferenceClient.cs`: bọc `InferenceClient`, tự động ghi nhận thành công/thất bại. Chỉ tính là lỗi: mất kết nối, `TIMEOUT`, `MODEL_ERROR`. `BUSY`/`NOT_READY`/`BAD_CONTRACT` **không** tính lỗi (LLM vẫn sống, chỉ chưa làm được việc).

## 7. Worker điều phối job (mục 4.12)

- `Llm/MachineIdleChecker.cs`: kiểm CPU (`PerformanceCounter`) + pin (`GetSystemPowerStatus`, P/Invoke). Đo lỗi → coi như đang rảnh (fail-open, đây chỉ là lớp "lịch sự" giảm tải, không phải an toàn bắt buộc). **Chưa làm:** kiểm tra "có phiên người dùng đang hoạt động" — Windows không có cách đáng tin cậy để 1 Windows Service biết màn hình có bị khoá hay không.
- `Llm/LlmJobDispatcher.cs`: 1 lượt xử lý = xác nhận `contract_ver` khớp (gọi `GetInfo`, nhớ kết quả, chỉ hỏi lại sau khi mất kết nối) → lấy 1 job → **kiểm tra `file_hash` trước khi gửi** (trích lại file, băm, so với hash lúc tạo job; lệch → `MarkJobStale`) → dựng gói (`ChunkSelector` + `Masker`) → gọi LLM → **kiểm tra `file_hash` lại sau khi nhận** (file có thể đổi trong lúc chờ) → ghi kết quả (`CompleteJob`/`TimeoutJob`/`FailJob`/`MarkJobStale`, tái dùng máy trạng thái `llm_job` đã có từ buổi 6).
- `Worker.cs`: vòng lặp riêng 10 giây (tách khỏi vòng dọn dẹp 1 phút có sẵn) — luôn `await` xong lượt trước mới lặp lại → tự nhiên chỉ 1 job chạy đồng thời, không cần khoá riêng.
- `Storage/ContentHash.cs` (mới): tách hàm băm dùng chung — Pass 1 (`FileWatcherService`) và Pass 2 (Worker LLM) PHẢI băm giống hệt nhau, nếu không sẽ luôn báo "file đã đổi" dù thật ra không đổi gì.
- **Chưa làm:** cập nhật `scan_result.s_final`/nhãn cuối theo công thức merge (mục 7 — Merger). Hiện tại kết quả LLM chỉ dừng ở `llm_job`/`llm_result` (đúng vòng đời, không bị kẹt "running"), chưa ảnh hưởng tới kết luận cuối cùng gửi cho người dùng.

## 8. Test chịu lỗi + sửa lỗi phát hiện qua test (mục 5.3, mục 8)

Khi làm "contract test" phát hiện `LlmJobDispatcher` **chưa thực sự tuân thủ mục 5.3** — gửi `Classify` thẳng, không xác nhận `contract_ver` trước. Đã sửa: kiểm tra qua `GetInfo` trước khi lấy job, lệch thì dừng ngay (không đụng job nào), có nhớ kết quả để không phải hỏi lại mỗi lượt.

`tools/resilience-llm/DlpLlmResilience` — 4 kịch bản, **10/10 PASS**:

| # | Kịch bản | Kết quả |
|---|---|---|
| 1 | LLM chạy sai `contract_ver` | Không gửi request nào, job vẫn `pending` nguyên vẹn |
| 2 | Giết LLM (TerminateProcess) giữa chừng | Không crash, job quay về `pending` để thử lại |
| 3 | LLM treo không trả lời (không tự báo timeout) | Client tự cắt sau ~3s (deadline riêng), không đợi mãi |
| 4 | LLM trả lời "rác" (đúng khuôn dạng, giá trị vô lý — confidence âm, nhãn ngoài tập đóng) | Không crash, vẫn hoàn tất job bình thường |

---

## 9. Bảng file đã thay đổi/thêm mới trong buổi 8

| File | Thay đổi | Lý do |
|---|---|---|
| `Llm/TokenEstimator.cs`, `Llm/ChunkSelector.cs` (mới) | Chọn chunk theo ngân sách token | Mục 4.10 |
| `Reading/FileReaderService.cs` (`FindExtractor`) | Tra extractor theo đuôi file, không qua `TryReadFile` | Cho ChunkSelector tự trích lại (Pass 2) |
| `Llm/Masker.cs` (mới) | Mask 2 lớp + huỷ chunk dính rule cứng | Mục 4.11 |
| `tools/canary/DlpCanaryTest/*` (mới) | Test giá trị đánh dấu không lọt vào gói | Mục 8 |
| `Reading/FileReaderService.cs` (`NeedsOcr`), `Storage/ResultStore.cs` (cột `needs_ocr` + `ALTER TABLE`), `Classification/EscalationPolicy.cs`, `Watching/FileWatcherService.cs` | Phân biệt "sạch" và "cần OCR" | Mục 1.3, 4.8 |
| `contracts/inference.proto` (mới), `Llm/ContractVersion.cs`, `Llm/Label.cs`, `Llm/EvidenceCode.cs` (mới) | Chốt hợp đồng gRPC, `contract_ver=1`, enum Label/EvidenceCode | Mục 5.2, 6.5 |
| `tools/fake-llm/DlpFakeLlm/*` (mới) | LLM giả chạy gRPC thật qua Named Pipe | Mục 5 |
| `DlpService.csproj` (+Grpc.Net.Client, Grpc.Tools, Google.Protobuf, sinh code Client từ .proto), `Llm/InferenceClient.cs` (mới) | Client phía Regex service | Mục 5 |
| `Llm/CircuitBreaker.cs`, `Llm/GuardedInferenceClient.cs` (mới) | Circuit breaker + backoff | Mục 4.12 |
| `Storage/ContentHash.cs` (mới), `Storage/ResultStore.cs` (`GetFindings`), `Llm/MachineIdleChecker.cs`, `Llm/LlmJobDispatcher.cs` (mới), `Worker.cs` (vòng lặp 10s) | Worker điều phối job | Mục 4.12 |
| `Llm/LlmJobDispatcher.cs` (kiểm tra `contract_ver`), `tools/fake-llm/DlpFakeLlm/FakeInferenceService.cs` (`--contract-ver`, `HANG-`, `GARBAGE-`), `tools/resilience-llm/DlpLlmResilience/*` (mới) | Tuân thủ mục 5.3 + test chịu lỗi | Mục 5.3, mục 8 |
| `Text/Normalizer.cs` (mới), `Reading/FileReaderService.cs`, `Classification/EscalationPolicy.cs` | Gom NFC + bỏ dấu + tách từ về 1 chỗ (tách refactor, không đổi hành vi) | Dọn trùng lặp |

---

## 10. Việc có thể làm tiếp (theo đúng thứ tự phụ thuộc)

| Việc | Mục kiến trúc | Ghi chú |
|---|---|---|
| **Merger** — công thức `S_final`, cập nhật `scan_result` theo bảng quyết định (mục 7.2) | 7, 4.13 | Việc quan trọng nhất còn thiếu — hiện LLM đã trả lời nhưng chưa ảnh hưởng kết luận cuối |
| Hợp nhất enum `Label`/`EvidenceCode` viết tay với bản sinh từ `.proto` (`Dlp.Inference.*`) | — | Tránh 2 nguồn sự thật lệch nhau khi sửa 1 bên quên bên kia |
| Kiểm tra "không có phiên người dùng đang hoạt động" | 4.12 | Cần nghiên cứu thêm API Windows đáng tin cậy hơn |
| PID/chữ ký process (client) + PipeSecurity theo SID (server) | 5.5 | Bảo mật kênh IPC |
| Model ONNX thật (Phi-4-mini-instruct) thay LLM giả | 6.2 | Cần benchmark tiếng Việt trước khi bật thật (mục 6.6) |
| OCR cho PDF scan | 1.3 | Đang ngoài phạm vi theo thiết kế |
| `finding` lưu nhiều vị trí/rule (hiện chỉ 1 offset nhỏ nhất/rule) | 4.10 | Cải thiện chất lượng ngữ cảnh gửi LLM |
