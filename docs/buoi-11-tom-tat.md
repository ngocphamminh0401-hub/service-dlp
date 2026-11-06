# Buổi 11 — Điều tra & sửa LLM host (LlmSvc): SCM giết service lúc boot, log chẩn đoán, MODEL_ERROR/TIMEOUT, chạy thử end-to-end

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái |
|---|---|
| Nguyên nhân `LlmSvc` bị SCM hủy sau 30 giây lúc boot (Event 7009) | ✅ Xác định: băm SHA-256 4,6 GB nằm trước `RunAsync` |
| Kiểm hash model chạy nền sau khi báo RUNNING + "cổng toàn vẹn" + xử lý hash sai / lỗi đọc | ✅ Xong, đã kiểm trên boot nguội thật (RUNNING sau ~2,5 giây) |
| Log chẩn đoán `llm-diag.log` (MODEL_ERROR / TIMEOUT / SetGuidance) | ✅ Xong |
| Harness end-to-end `tools/e2e-llm/DlpE2eRun` (20 file, 5 vùng xám, đo độ chính xác / thời gian / CPU-RAM) | ✅ Xong, chạy 3 lần |
| Sửa lỗi 1 (đầu ra có biên + trần token theo job) và lỗi 2 (`OUTPUT_TOO_LONG`, không thử lại) | ✅ Xong, kiểm bằng model thật |
| Bản publish cho service thật | ⏳ Chưa — cần dừng 2 service rồi publish lại (xem mục 6) |
| `bad allocation` ở job đầu sau khi nạp model | ✅ Nới trần RAM 10240 → 12288 MB + thử lại 1 lần; 2 lần chạy thật: 0 lỗi |
| File `hr_top_00155` không ra kết quả LLM | ✅ Cấm khoảng trắng tùy ý (`x-guidance`); chạy thật: `Ok` (140 token, ~43 giây) |
| Chất lượng phân loại của LLM (4 mức) | ⚠️ Còn kém trên mẫu nhỏ (2/5 file vùng xám đúng) — xem mục 4 |

## 1. Nguyên nhân SCM kill LlmSvc (đã sửa)

- `LlmSvc` là service riêng (delayed-auto), `DlpLlmHost.exe` là chương trình nó chạy. DlpService chỉ kết nối pipe, không bật LLM.
- Log mốc thời gian (`C:\ProgramData\DlpLlmHost\startup-timing.log`): boot nguội 10:26:10 → process bắt đầu 10:28:30 → SCM hủy 10:28:59, **không có mốc "verify xong"**. Máy nóng băm chỉ ~5 giây, boot nguội > 29 giây.
- Sửa: `Program.cs` (băm nền trong `VerifyInBackgroundAsync`), `ModelHost.cs` (`integrityGate`), `ModelManifest.cs` (`Verify` băm theo khối 1 MB, có token hủy). Sau sửa: RUNNING sau ~2,5 giây, hash xong sau ~13 giây.
- Hash sai → không nạp model, Event Log, dừng service với mã 1 (chưa kiểm dưới SCM thật việc recovery có kích hoạt). Lỗi đọc tạm thời → thử lại 3 lần (`--verify-retry-seconds`, mặc định 30).

## 2. Phát hiện về bảo mật

- File model và exe cho nhóm `Authenticated Users` quyền **Modify** → ai đăng nhập cũng ghi đè được; hash là lớp bảo vệ duy nhất. Nên siết ACL (chưa làm).
- Hash chỉ kiểm một lần lúc khởi động, model nạp lười muộn hơn → vẫn có khoảng hở (chưa xử lý).
- `Llm:ServerExePath` chưa cấu hình → client chưa kiểm exe của server pipe. `Cancel` RPC chưa được DlpService dùng. Không đối chiếu `job_id` của phản hồi.

## 3. MODEL_ERROR / TIMEOUT — kết quả chẩn đoán

Log chẩn đoán cho thấy hai nguyên nhân tách biệt (không phải do mạng hay crash):

| Nguyên nhân | Dấu hiệu | Trạng thái |
|---|---|---|
| A. `bad allocation` ở lượt sinh đầu tiên sau khi nạp model | `gen LOI sau 0 token … bad allocation`, ws chỉ 1–3 GB | Lặp lại 100% lần nạp với trần Job Object 10240 MB (4/4). **Thí nghiệm nâng trần lên 14000 MB: job đầu chạy `Ok`** → nghi can là trần RAM (private đỉnh đo được ~9,8 GB). Lưu ý: cũng trong lần thí nghiệm đó 3 job liên tiếp chậm bất thường (0,8–0,9 token/giây, ~120 giây/job), chưa rõ do nâng trần hay do tải máy (2 lần chạy sau với trần 12288 MB lại nhanh 19–43 giây/job nên nhiều khả năng do tải máy). **Đã sửa** (mục 4) |
| B. Một file luôn sinh JSON bị cắt | `hr_top_00155`: chạm trần token ở cả 2 lượt sinh, `verdict_objs=0 reason=khong_thay` | Đã giảm tác hại, rồi **tìm ra gốc rễ**: khoảng trắng tùy ý trong JSON; đã sửa (mục 4) |

Các số đo khác: 3168 token prefix (không phải ~1300), nạp model 80–143 giây, 2–7 token/giây, job tốt 16–24 giây, guidance JSON **đã bật** (không phải nguyên nhân).

## 4. Sửa lỗi 1 và 2 (đã làm) và việc còn mở

**Đã làm:** `ClassifyOutputSchema.JsonSchemaStrict` (reason ≤ 120 ký tự, verdicts ≤ 16, confidence rời rạc, lùi về schema cũ nếu runtime từ chối); trần token job `clamp(150 + 40 × findings, 200, 800)`; trạng thái mới `OUTPUT_TOO_LONG = 6` trong `contracts/inference.proto`; `ResultStore.FailJobPermanently`; `GuardedInferenceClient` không tính trạng thái này là lỗi breaker; +3 unit test (25/25 pass).

**Kết quả chạy thật (cùng 20 file):** pha LLM từ 1803 giây xuống 310 giây; `hr_top_00155` từ ~4 × 200 giây (kẹt `pending`) xuống 1 lượt 64 giây rồi `failed`; hàng đợi không còn kẹt.

**Đã làm tiếp (a)+(b):**
- `ModelHost` thử lần lượt 3 schema: chặt + `x-guidance {"whitespace_flexible": false}` → chặt → cũ; ghi log schema nào đang chạy và schema nào bị từ chối. Runtime 0.9.2 **chấp nhận** `x-guidance` ở kiểu `json_schema`. Đầu ra ngắn hơn rõ rệt (cùng file: 83 → 56 token, 251 → 193 ký tự), file `hr_top_00155` từ chạm trần 390 token mà chưa tới `reason` thành xong 140 token, 220 ký tự → nghi vấn khoảng trắng tùy ý được xác nhận.
- Trần RAM Job Object mặc định 10240 → 12288 MB (`Program.cs`, `install-service.ps1`); thêm thử lại 1 lần khi `bad allocation` ở lượt sinh đầu (chưa cần dùng nên **đường thử lại chưa được chạy thật**). Thí nghiệm: trần 10240 → `bad allocation` 4/4 lần; 14000 và 12288 → 0 lỗi.
- Log chẩn đoán thêm số khoảng trắng / xuống dòng khi parse hỏng.

**Kết quả 2 lần chạy thật liên tiếp (cùng 20 file, trần 12288 MB):** 10/10 job `Ok`, không `ModelError`/`OutputTooLong`/`bad allocation`; job đầu (gồm nạp model) 120–131 giây, các job sau 19–43 giây; private đỉnh ~9,8 GB, working set đỉnh 7,5 GB; hàng đợi cuối `done=5`.

**Chất lượng LLM (mẫu 5 file vùng xám, replay không có `Meta`):** regex đúng 4/5, LLM đúng 2/5 (4 mức) và 2/5 nhị phân. Ví dụ LLM xếp Public → Restricted (`fin_pub_00697`), Internal → Restricted (`fin_int_00060`), Restricted → Public (`hr_top_00155`). Mức LLM hiện **không** quyết định mức cuối (Merger chỉ đổi điểm `S_final`) nên chưa gây sai kết quả, nhưng `S_final` vẫn bị đẩy lên ở 3/5 file (VD `fin_pub_00697`: 0,37 → 0,64). Cỡ mẫu quá nhỏ để kết luận về model; cần đánh giá trên bộ mẫu lớn trước khi tin LLM.

## 5. Harness end-to-end `tools/e2e-llm/DlpE2eRun`

Dùng đúng class thật (`FileWatcherService`, `ResultStore`, `LlmJobDispatcher`, `InferenceClient`) với cấu hình thật, DB và thư mục tạm riêng, LLM host thật. Chọn 20 file (5 vùng xám) từ `rawdata`, báo cáo độ chính xác / thời gian / CPU-RAM ra `tools/e2e-llm/DlpE2eRun/results`. Tùy chọn: `--host-exe`, `--host-args`, `--no-replay`, `--max-minutes`, `--keep`. Lưu ý: dispatcher không lưu mức 4 bậc của LLM nên harness gọi lại LLM riêng (pha `replay`) để lấy mức này.

Độ chính xác (mẫu nhỏ, chỉ để tham khảo): kết quả cuối = mức regex, 17/20 đúng 4 mức. Trên 4 file vùng xám có mức LLM: regex đúng 3/4, LLM đúng 2/4.

## 6. Lệnh publish (chạy bằng quyền Administrator)

```powershell
cd D:\Service-DLP
Stop-Service LlmSvc, DlpService
dotnet publish tools\llm-host\DlpLlmHost -c Release -o tools\llm-host\DlpLlmHost\publish --self-contained false
dotnet publish DlpService -c Release -o publish
Start-Service LlmSvc, DlpService
```

## 7. Bảng file đã thay đổi/thêm mới

| File | Thay đổi | Lý do |
|---|---|---|
| `tools/llm-host/DlpLlmHost/Program.cs` | Mốc thời gian khởi động; băm nền; `VerifyInBackgroundAsync`; mã thoát | Sửa SCM kill 30s |
| `tools/llm-host/DlpLlmHost/ModelHost.cs` | `integrityGate`; schema chặt + dự phòng; trần token job; `GenerateResult` thêm trường; log | Cổng hash, lỗi 1 |
| `tools/llm-host/DlpLlmHost/ModelManifest.cs` | `Verify` băm khối, có token hủy | Dừng service không treo |
| `tools/llm-host/DlpLlmHost/InferenceHostService.cs` | Log chẩn đoán; `TokenCapFor`; không sinh lại khi lỗi chắc chắn; `OutputTooLong` | Lỗi 1, 2 |
| `tools/llm-host/DlpLlmHost/DiagLog.cs` (mới) | Ghi `ProgramData\DlpLlmHost\llm-diag.log` | Chẩn đoán |
| `tools/llm-host/DlpLlmHost/ClassifyOutputSchema.cs` | `JsonSchemaStrict` | Lỗi 1 |
| `contracts/inference.proto` | `Status.OUTPUT_TOO_LONG = 6` | Lỗi 2 |
| `DlpService/Llm/LlmJobDispatcher.cs`, `GuardedInferenceClient.cs`, `Storage/ResultStore.LlmJobs.cs` | Xử lý `OUTPUT_TOO_LONG`, `FailJobPermanently` | Lỗi 2 |
| `DlpService.Tests/Storage/ResultStoreFailJobPermanentlyTests.cs` (mới) | 3 test | Lỗi 2 |
| `tools/e2e-llm/DlpE2eRun/*` (mới) | Harness end-to-end | Đo end-to-end |
| `docs/kien-truc-trien-khai.md` | `OUTPUT_TOO_LONG`, hash nền, trần token, số đo RAM thật | Đồng bộ tài liệu |

## 8. Việc còn lại

1. Publish lại 2 service (mục 6) và **đổi `binPath` của `LlmSvc`**: lệnh đã đăng ký có sẵn `--memory-limit-mb 10240`, đổi mặc định trong code không có tác dụng với service đang cài — phải cập nhật `binPath` (hoặc gỡ/cài lại bằng `install-service.ps1`) rồi theo dõi `llm-diag.log` trên service thật.
2. Đánh giá chất lượng LLM trên bộ mẫu lớn (hiện 2/5 đúng): xem lại prompt/few-shot (prefix 3168 token), nhìn lại cách Merger dùng verdict (đang đẩy `S_final` lên cả khi LLM sai).
3. Siết ACL file model/exe; cấu hình `Llm:ServerExePath`; dùng `Cancel`; đối chiếu `job_id` phản hồi.
4. Dispatcher chưa lưu `SensitivityLevel` (mức 4 bậc) của LLM; kiểm tra vì sao Merger đổi `S_final` dù LLM chưa điền verdict theo rule.
5. DlpService tự chết 5 lần trong 14 ngày (`0xc0000005`/`0xc0000006`) — chưa điều tra.
