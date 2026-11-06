> **Lưu ý (05/11/2026):** kiến trúc đúng với code hiện tại nằm ở `kien-truc-service.md`. File này giữ để tra lý do thiết kế/lịch sử.

# Tài liệu kỹ thuật — DlpService (trạng thái hiện tại)

> Tài liệu này mô tả **những gì đã có thật trong code** (đọc trực tiếp từ `Worker.cs`, `FileWatcherService.cs`,
> `ResultStore.cs`, `appsettings.json`...), khác với `kien-truc-trien-khai.md` là bản **thiết kế/spec dự định**
> (nhiều phương án, giá trị "đề xuất", phần "chưa làm"). Khi hai tài liệu lệch nhau, tài liệu này đúng với code tại
> thời điểm viết; `kien-truc-trien-khai.md` giữ lại để biết lý do thiết kế ("tại sao làm vậy"). Mục lục file/class
> chi tiết xem `muc-luc-code.md`; lịch sử từng buổi làm xem `buoi-XX-tom-tat.md`.

## 1. Tổng quan

DlpService là Windows Service (.NET 10, `Microsoft.NET.Sdk.Worker`) chạy nền, theo dõi 1 thư mục (`watch-folder`,
hiện hard-code `D:\Service-DLP\watch-folder` trong `Worker.cs`), đọc nội dung file, quét bằng regex để tìm dữ liệu
nhạy cảm, phân loại theo domain (Finance/HR/PII/Org) và mức độ (Public/Internal/Confidential/Restricted), rồi ghi
kết quả vào SQLite. File ở "vùng xám" (không chắc) có thể được gửi sang 1 LLM process riêng (qua gRPC over Named
Pipe) để thẩm định thêm — phần LLM đã có hạ tầng điều phối job đầy đủ (hàng đợi, circuit breaker, idle check, mask),
nhưng **server LLM thật (DlpLlmHost) chưa được coi là bắt buộc** — nếu không cấu hình `Llm:ServerExePath` thì bỏ
qua bước xác thực exe server, và nếu không kết nối được pipe thì service tự thoái hóa về chạy regex-only (đúng
nguyên tắc P3 trong `kien-truc-trien-khai.md`).

## 2. Luồng xử lý 1 file (thực tế trong code)

```
FileSystemWatcher (Created/Changed/Renamed)
        │ debounce 300ms (FileWatcherService.ScheduleProcess)
        ▼
ProcessFile(filePath)
  1. Cache tầng 1: size+mtime giống lần trước VÀ lần trước đã có kết luận → bỏ qua, không mở file
  2. FileReaderService.TryReadFile → trích text theo đuôi file (txt/csv/docx/pptx/xlsx/xls/pdf/eml)
  3. Cache tầng 2: text trích ra có hash (SHA-256) giống lần trước → không quét lại, chỉ cập nhật mtime
  4. RegexScanner.Scan(content) → ScanResult (Matches + TimedOutPatterns)
     - quét CHƯA TRỌN (có rule timeout) → ghi provisional, needs_review, KHÔNG gắn mức, dừng ở đây
  5. Không có match → "sạch"; vẫn gọi Escalate() để xét P2 (tên file đáng nghi) / P3 (lấy mẫu ngẫu nhiên)
  6. Có match → ClassificationEngine.Classify() gán mức + domain
  7. FileScorer.Compute() tính điểm S1 cấp file (noisy-OR theo rule, gộp trước khi nhân)
  8. ScoreRouter.Route() chia 3 vùng: Certain (S1≥THigh) / Grey (TLow..THigh) / NoMatch-nhưng-weak
  9. EscalationPolicy.Decide() xét eligibility (≥ MinTextChars, không ở ExcludedPaths) + gán nhóm P1/P2/P3
 10. ResultStore.SaveScan() — ghi file_state + scan_result + finding, và TẠO llm_job (cùng 1 transaction)
     nếu đủ điều kiện và còn hạn ngạch (JobQuotaOptions)
```

Vòng lặp thứ hai, độc lập, chạy mỗi 10 giây (`Worker.RunLlmDispatchLoopAsync`):

```
LlmJobDispatcher.TryDispatchOnceAsync()
  - MachineIdleChecker: máy có đang rảnh (CPU ≤ ngưỡng, pin ≥ ngưỡng) không? Không thì bỏ lượt này
  - CircuitBreaker: đang open (LLM process lỗi liên tục) thì bỏ lượt này
  - ResultStore.TryPickNextJob(): lấy 1 job pending theo ưu tiên P1 > P2 > P3
  - Đọc lại file (FileReaderService), quét lại (RegexScanner), kiểm tra file_hash CHƯA ĐỔI
  - ChunkSelector chọn đoạn văn bản liên quan, Masker che dữ liệu nhạy cảm trước khi gửi
  - GuardedInferenceClient.Classify() gửi qua InferenceClient (gRPC Named Pipe) tới LLM process
  - Merger gộp S1 (regex) + S2 (LLM) → S_final theo MergePolicyVersion hiện tại
  - ResultStore.CompleteJob()/FailJob()/TimeoutJob() cập nhật llm_job + llm_result + llm_cache
```

Mỗi lượt Worker còn chạy dọn dẹp định kỳ (mặc định 24h/lần): xoá `llm_job` stale/failed quá
`JobRetentionDays` (30 ngày), `llm_cache` quá `CacheDays` (90 ngày), `audit` quá `AuditRetentionDays` (180 ngày).
Không bao giờ xoá job `done` hay `llm_result` (dùng làm cache tầng 1 theo `file_hash`).

## 3. Cấu trúc project theo thư mục

| Thư mục | Vai trò |
|---|---|
| `Watching/` | `FileWatcherService` — theo dõi thư mục, debounce, cache trùng nội dung, nối toàn bộ luồng xử lý 1 file |
| `Reading/`, `Reading/Extractors/` | Trích text từ từng loại file (txt, csv, docx, xlsx/xls, pdf, pptx, eml) |
| `Scanning/`, `Scanning/Validation/` | Rule engine regex (`RegexScanner`, `RuleLoader`), validator checksum (Luhn, MST, IBAN, CCCD...) |
| `Classification/` | Gán domain + mức độ (`ClassificationEngine`, các `Modules/`), tính điểm S1 (`FileScorer`), router 3 vùng (`ScoreRouter`), eligibility P1/P2/P3 (`EscalationPolicy`), gộp kết quả LLM (`Merger`) |
| `Llm/` | Hạ tầng gọi LLM process: contract gRPC, `InferenceClient`, `GuardedInferenceClient` (bọc circuit breaker), `LlmJobDispatcher`, `CircuitBreaker`, `MachineIdleChecker`, `ChunkSelector`, `Masker` |
| `Storage/` | `ResultStore` (partial 3 file) — toàn bộ SQLite: schema, cache, hàng đợi job, cache LLM, ACL thư mục dữ liệu |
| `Logging/` | `FileLogger`/`FileLoggerProvider` — ghi log ra `logs/dlpservice-YYYY-MM-DD.log` |
| `Diagnostics/` | `ExplainCommand` — lệnh CLI `DlpService.exe explain <file_id>` |
| `rules/` | Dữ liệu (không phải code): rule JSON theo domain `financial/`, `hr/`, `pii/`, `org/`, + `compound.json` |
| `scripts/` | `install-service.ps1` / `uninstall-service.ps1` — đăng ký/gỡ Windows Service qua `sc.exe` |
| `tools/` | Các console app phụ trợ, tham chiếu `DlpService.csproj` (xem mục 7) |
| `DlpService.Tests/` | Unit test (xUnit) |

Nguyên tắc namespace: theo đúng tên thư mục (`DlpService.Watching`, `DlpService.Scanning`,
`DlpService.Classification`, `DlpService.Llm`, `DlpService.Storage`...). Core (`Classification`, `Scanning`)
không biết cụ thể `Watching` hay `Llm` gọi nó thế nào.

## 4. Cấu hình thực tế (`appsettings.json`)

```jsonc
{
  "Score": { "THigh": 0.80, "TLow": 0.10 },        // ngưỡng router 3 vùng (mục 4.7 kien-truc-trien-khai.md)
  "Storage": { "DbPath": "data\\dlp.db" },          // tương đối tính theo thư mục chứa .exe
  "Llm": {
    "PipeName": "dlp-llm-host",
    "JobDeadlineSeconds": 600,
    "MinTextChars": 200,                            // eligibility: dưới mức này không đẩy LLM
    "ExcludedPaths": [],
    "SuspiciousKeywords": [ "luong", "bangluong", "hopdong", "nhansu", "password", "matkhau", "backup", "cccd", "salary" ],
    "SampleRatePct": 2,                              // P3: % file sạch được lấy mẫu ngẫu nhiên
    "CacheDays": 90, "JobRetentionDays": 30, "AuditRetentionDays": 180, "CleanupIntervalHours": 24,
    "Quota": { "P1PerHour": 200, "P2PerDay": 300, "P3PerDay": 50, "MaxPendingJobs": 1000 }
  }
}
```

Giá trị thật đang dùng (`THigh=0.80`, `TLow=0.10`) **khác** giá trị "mặc định đề xuất" trong
`kien-truc-trien-khai.md` (0.90/0.30) — đã được chỉnh qua `tools/eval` (xem mục 7). Các khoá chưa xuất hiện ở
`appsettings.json` (`PipeName` khác, `ServerExePath`, `TokenBudget`, `MaxCpuPercent`, `MinBatteryPercent`,
`RequireServerSignature`...) đều có giá trị mặc định cứng trong `Worker.cs` nếu không cấu hình — xem đoạn đọc
`configuration.GetValue(...)` trong `RunLlmDispatchLoopAsync`.

Watch-folder: **chưa đọc từ cấu hình**, hard-code `D:\Service-DLP\watch-folder` ở `Worker.ExecuteAsync`.

## 5. Mô hình dữ liệu SQLite thực tế

File: theo `Storage:DbPath` (mặc định `<thư mục chạy>/data/dlp.db`). Khi mở: bật
`journal_mode=WAL; synchronous=NORMAL; busy_timeout=5000; foreign_keys=ON`, khoá ACL thư mục chứa DB
(`DataFolderSecurity`, tắt kế thừa, chỉ SYSTEM + Administrators + tài khoản chạy service), ghi `PRAGMA
user_version=1` (schema version).

Bảng đang có **và được ghi thật**: `file_state`, `scan_result`, `finding` — đúng lược đồ mục 3.1 của
`kien-truc-trien-khai.md`, cộng thêm 3 cột không có trong bản thiết kế gốc: `scan_result.needs_ocr`,
`scan_result.rules_ver`, `scan_result.merge_policy_ver` (phục vụ "lưu vết" mục 7.4 — biết kết quả được tính bằng
bộ rule/công thức merge nào).

Bảng đang có **và được ghi bởi tầng LLM**: `llm_job` (hàng đợi, máy trạng thái pending/running/done/failed/
timeout/stale như mục 3.2), `llm_result`, `llm_cache`, `audit` — 4 bảng này được tạo sẵn cùng lúc với 3 bảng trên
(không phải tạo sau), và **đã có code ghi/đọc thật** qua `ResultStore.LlmJobs.cs` / `ResultStore.LlmCache.cs`
(khác với ghi chú trong `kien-truc-trien-khai.md` mục 3.1 nói "chưa có code ghi" — đoạn đó đã lỗi thời).

Không bảng nào lưu giá trị thật đã khớp (CCCD, số thẻ...) — chỉ lưu `rule_id`, điểm, số lần khớp, vị trí
(`offset:N`).

## 6. Giao tiếp với LLM process

- Contract đã chốt: `contracts/inference.proto` (`contract_ver=1`), sinh code **client only**
  (`GrpcServices="Client"` trong `DlpService.csproj`) — DlpService chỉ gọi, không đóng vai server.
- Kênh: gRPC over Named Pipe, tên pipe cấu hình ở `Llm:PipeName` (mặc định `dlp-llm-host`).
- `GuardedInferenceClient` bọc `InferenceClient` bằng `CircuitBreaker` — lỗi/timeout liên tiếp thì mở circuit,
  tạm ngừng gọi LLM trong một khoảng thời gian rồi thử lại (half-open).
- `MachineIdleChecker` chặn gửi job khi máy không rảnh (CPU vượt `MaxCpuPercent` hoặc pin dưới
  `MinBatteryPercent`) — dùng `System.Diagnostics.PerformanceCounter`.
- Server LLM thật nằm ở `tools/llm-host/DlpLlmHost` (chạy tách biệt, không phải 1 project trong `DlpService.sln`
  chính) — xem mục 7. Nếu `Llm:ServerExePath` không cấu hình, `PipeServerTrustPolicy` không được tạo và bước
  kiểm tra "server đúng là exe mong đợi" (mục 5.5 kien-truc-trien-khai.md) bị bỏ qua.

## 7. Công cụ phụ trợ (`tools/`)

Các console app độc lập, tham chiếu `DlpService.csproj` để dùng lại pipeline thật, không chạy cùng service:

| Thư mục | Dùng để |
|---|---|
| `tools/eval/DlpBaseline` | Đo precision/recall/F1 của pipeline trên dữ liệu có nhãn (`rawdata/_metadata.jsonl`), dò ngưỡng S1 (`S1Tuning`) — dùng để chỉnh `Score.THigh/TLow` thật trong `appsettings.json` |
| `tools/perf-llm/DlpLlmBenchmark` | Đo hiệu năng suy luận LLM (latency, token/giây) |
| `tools/resilience/DlpResilience` | Test chịu lỗi: **kill thật** `DlpService.exe` giữa lúc quét/giữa lúc job LLM chạy, khởi động lại, kiểm tra bất biến dữ liệu |
| `tools/resilience-llm/DlpLlmResilience` | Test chịu lỗi riêng cho tầng LLM (circuit breaker, timeout, crash LLM process) |
| `tools/safety/DlpExtractorSafety` | Test an toàn extractor (file hỏng, zip bomb, encoding lạ) |
| `tools/fake-llm/DlpFakeLlm` | LLM giả (trả kết quả cố định/ngẫu nhiên theo contract) — dùng thay LLM thật khi test tầng điều phối |
| `tools/llm-host/DlpLlmHost` | LLM host thật: nạp model ONNX, dựng prompt, chạy suy luận, trả theo contract |
| `tools/canary/DlpCanaryTest` | Test "canary" — dữ liệu đánh dấu để kiểm tra dữ liệu thật không lọt vào log/LLM |

Kết quả các lần đo nằm ở `tools/*/results/*.md` (đã có lịch sử chạy, xem ngày trong tên file).

## 8. Build, chạy, cài đặt

```powershell
dotnet build                      # build toàn solution
dotnet test                       # chạy unit test (DlpService.Tests)
dotnet run --project DlpService   # chạy tay (console), KHÔNG qua SCM — AddWindowsService() không có tác dụng
dotnet publish DlpService -c Release -o publish   # xuất ra publish/DlpService.exe (script install dùng đường dẫn này)

# Cài thành Windows Service (cần PowerShell Admin, chạy sau khi publish):
scripts\install-service.ps1
sc.exe start DlpService
scripts\uninstall-service.ps1     # gỡ

# Chẩn đoán 1 file đã quét (không khởi động service):
publish\DlpService.exe explain <file_id>
```

Log: `logs/dlpservice-YYYY-MM-DD.log` (file logger tự viết, nằm cạnh thư mục chạy) — là nơi đáng tin cậy nhất để
xem log khi chạy dưới SCM (Event Log từng được thử nhưng không ghi được trên máy test, xem comment trong
`Program.cs`).

## 9. Giới hạn hiện tại (so với thiết kế đầy đủ ở `kien-truc-trien-khai.md`)

- Watch-folder hard-code, không đọc từ `appsettings.json`.
- Chưa có OCR cho PDF scan — đánh dấu `needs_ocr`, không tự xử lý tiếp.
- Chưa có cơ chế tự khởi động lại watcher khi lỗi nặng (khác tràn buffer) — chỉ log lỗi.
- Server LLM thật (`DlpLlmHost`) chạy độc lập, chưa được đóng gói/khởi động cùng installer; cấu hình mặc định
  coi việc xác thực exe server là tùy chọn.
- Chưa có bước "shadow mode" tách riêng khỏi luồng chính — LLM chạy thật và có thể đổi `s_final` ngay khi đủ
  điều kiện (không có công tắc `Llm.Mode=off|shadow|...` như mục 11 của `kien-truc-trien-khai.md`).
