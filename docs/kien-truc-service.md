# Kiến trúc DlpService (đúng với code hiện tại)

> Viết ngày 05/11/2026, đọc từ `Program.cs`, `Worker.cs`, `Watching/`, `Reading/`, `Scanning/`, `Classification/`,
> `Llm/`, `Storage/`, `appsettings.json`. Đây là **nguồn tra cứu kiến trúc duy nhất**.
> Các tài liệu khác chỉ bổ trợ:
> - `kien-truc-trien-khai.md` — thiết kế dự định và lý do ("tại sao làm vậy"), một số chỗ đã lệch code.
> - `luong-va-thiet-ke-ky-thuat.md` — luồng người dùng, luồng kỹ thuật từng bước và danh sách rủi ro R1–R13.
> - `muc-luc-code.md` — file nào chứa class nào.

---

## 1. Bức tranh tổng thể

DlpService là một **Windows Service (.NET 10 Worker)** chạy nền. Nó theo dõi thư mục, đọc file, quét regex, phân loại
mức nhạy cảm rồi ghi kết quả vào SQLite. Những file "không chắc" được đưa cho **một tiến trình LLM riêng** thẩm định lại.

```
┌──────────────────────────── DlpService.exe (tiến trình 1) ─────────────────────────────┐
│                                                                                         │
│  Program.cs ─► Generic Host ─► Worker (BackgroundService)                                │
│                                   │ dựng và nối các thành phần (không dùng DI container) │
│        ┌──────────────────────────┼──────────────────────────────────┐                  │
│        ▼                          ▼                                  ▼                  │
│   PASS 1 (thời gian thực)     PASS 2 (nền, 10 s/lần)           Bảo trì (1 phút/lần)     │
│   Watching → Reading →        Llm/LlmJobDispatcher             dọn dẹp theo lịch 24 h   │
│   Scanning → Classification                                                             │
│        │                          │                                                     │
│        └────────► Storage/ResultStore ◄───────┘      (SQLite data\dlp.db, WAL)          │
└──────────────────────────────────────┬──────────────────────────────────────────────────┘
                                       │ gRPC qua Named Pipe "dlp-llm-host" (contracts/inference.proto)
                                       ▼
                         DlpLlmHost.exe (tiến trình 2, model ONNX) — có thể vắng mặt
```

Ba ý chính:

1. **Hai pass.** Pass 1 rẻ và nhanh (regex). Pass 2 đắt (LLM) và chỉ dành cho vùng xám.
2. **Hai tiến trình.** LLM chết hoặc chậm không kéo service chết (vòng ngắt mạch, quay về chế độ chỉ regex).
3. **Một kho dữ liệu.** SQLite là điểm giao giữa Pass 1 và Pass 2 (bảng `llm_job` là hàng đợi).

---

## 2. Các tầng và trách nhiệm

Mỗi thư mục là một namespace `DlpService.<Tên>` và đúng một trách nhiệm.

| Tầng | Trách nhiệm | Thành phần chính | Biết gì về tầng khác |
|---|---|---|---|
| **Host** (gốc project) | Khởi động, đăng ký Windows Service, logging, dựng và nối thành phần, vòng lặp nền | `Program.cs`, `Worker.cs` | Biết tất cả (là nơi lắp ráp) |
| **Watching/** | Theo dõi thư mục, debounce 300 ms, giới hạn đồng thời, cache nội dung không đổi, điều phối Pass 1 | `FileWatcherService` | Reading, Scanning, Classification, Storage |
| **Reading/** | Biến file thành text: chọn bộ đọc theo đuôi rồi chọn lại theo magic bytes, kiểm tra kích thước, chuẩn hoá NFC, cắt ở 2 triệu ký tự | `FileReaderService`, `FileTypeSniffer`, `IContentExtractor` + `Extractors/*`, `Native/*` (Tika) | Không biết ai gọi mình |
| **Scanning/** | Quét text bằng rule, kiểm tra checksum, tính điểm từng chỗ khớp | `RuleLoader`, `RegexScanner`, `SensitivePattern`, `CandidateScorer`, `Validation/*` | Không biết Classification |
| **Classification/** | Gán mức và domain, tính điểm S1 cấp file, định tuyến ba vùng, quyết định có gửi LLM không, gộp ý kiến LLM | `ClassificationEngine` + `Modules/*`, `FileScorer`, `ScoreRouter`, `EscalationPolicy`, `Merger`, `FileConfidence` | Chỉ nhận `ScanMatch` |
| **Llm/** | Thẩm định vùng xám: chọn đoạn, che giá trị, gọi LLM an toàn, kiểm tra máy rảnh | `LlmJobDispatcher`, `ChunkSelector`, `Masker`, `InferenceClient`, `GuardedInferenceClient`, `CircuitBreaker`, `MachineIdleChecker`, `PipeServerVerifier` | Storage, Reading, Scanning |
| **Storage/** | Ghi và đọc SQLite, hàng đợi job, cache LLM, khoá ACL thư mục dữ liệu | `ResultStore` (+ partial `.LlmJobs`, `.LlmCache`, `.Explain`), `DataFolderSecurity`, `ContentHash` | Không biết tầng nào khác |
| **Text/** | Chuẩn hoá chuỗi dùng chung | `Normalizer` | — |
| **Logging/** | Ghi log ra file theo ngày | `FileLoggerProvider`, `FileLogger` | — |
| **Diagnostics/** | Lệnh `DlpService.exe explain <file_id>` (chạy tay, không lên service) | `ExplainCommand` | Storage |

### Hướng phụ thuộc

```
Worker ─► Watching ─► Reading
                 ├──► Scanning ─► (rules/*.json)
                 ├──► Classification
                 └──► Storage ◄── Llm (đi qua Reading, Scanning, Storage)
```

Quy tắc giữ cho kiến trúc sạch:

- **Core không biết module cụ thể.** `ClassificationEngine` chỉ biết `IClassifierModule`. Nó tự nạp mọi module bằng
  reflection. Thêm domain mới là thêm một class, không sửa engine.
- **`FileReaderService` làm tương tự với `IContentExtractor`.** Thêm định dạng mới là thêm một extractor.
- **Rule là dữ liệu, không phải code.** Thư mục `rules/<domain>/*.json` được `RuleLoader` nạp, kèm `compound.json`
  do `CompoundRuleModule` nạp riêng. Thêm rule không cần build lại.
- **Lỗi một thành phần phụ không làm chết service** (xem mục 7).

---

## 3. Vòng đời tiến trình

`Worker.ExecuteAsync` lắp ráp mọi thứ **bằng tay** (không dùng DI container) theo đúng thứ tự dưới đây. Thứ tự có ý nghĩa:

| # | Bước | Vì sao thứ tự này |
|---|---|---|
| 1 | `TikaNativeRuntime.Configure` | Phải chạy trước khi tạo `FileReaderService`, vì nó đăng ký extractor lúc được tạo |
| 2 | `RuleLoader` nạp rule và `ComputeRulesVersion` | `rules_ver` ghi kèm mọi kết quả để truy vết |
| 3 | Đọc ngưỡng (`Scan:MinConfidence`, `Score`), tạo `RegexScanner` và `ClassificationEngine` | Sai cấu hình thì dùng mặc định và cảnh báo, không dừng |
| 4 | Mở `ResultStore` (SQLite, WAL, khoá ACL) | Lỗi thì chạy tiếp mà không ghi DB |
| 5 | `RecoverRunningJobs` | Job `running` của lần trước quay về `pending` |
| 6 | Dọn dẹp lần đầu, nạp model FileConfidence | Model lỗi thì bỏ qua (chỉ là tín hiệu shadow) |
| 7 | Tạo `FileWatcherService` (giữ ở **trường** của Worker), `StartWatching` | Bật watcher rồi quét file có sẵn; giữ tham chiếu mạnh để GC không thu hồi |
| 8 | `EnqueueMissingGreyJobs` | Phải chạy **sau** lượt quét đầu để dùng hash mới nhất |
| 9 | Chạy song song vòng chính (1 phút) và vòng LLM (10 giây) | Hai vòng độc lập |

**Dừng service:** huỷ token, hai vòng lặp thoát, `FileWatcherService.Dispose`, `TikaNativeRuntime.Shutdown`, đóng DB.
Job đang chạy dở được khôi phục ở lần khởi động sau.

---

## 4. Hai luồng xử lý

### Pass 1: xử lý một file (thời gian thực)

```
Sự kiện file ─► debounce 300 ms ─► cổng SemaphoreSlim(6)
   ① chụp size + mtime
   ② cache tầng 1: size/mtime không đổi          ─► bỏ qua
   ③ Reading: sniff magic bytes → trích text (≤ 2 triệu ký tự, timeout 30 s)
   ④ SHA-256 của text
   ⑤ cache tầng 2: hash không đổi                ─► bỏ qua
   ⑥ Scanning: RegexScanner.Scan
   ⑦ Classification:
        engine (mức = max các module) → FileScorer (S1) → ScoreRouter → EscalationPolicy
   ⑧ Storage: SaveScan (1 transaction: file_state + scan_result + finding + llm_job)
```

Kết quả định tuyến theo S1 (ngưỡng trong `appsettings.json`):

| Vùng | Điều kiện | Hành động |
|---|---|---|
| Certain | S1 ≥ `THigh` (0,80) | Ghi `final`, không gửi LLM |
| Grey (vùng xám) | `TLow` (0,10) ≤ S1 < `THigh` | Ghi `provisional`, tạo job **P1** |
| Low | S1 < `TLow` | Xử lý như file sạch (có thể vào P2 hoặc P3) |

Quét chưa trọn (có rule bị timeout) thì ghi `provisional` + `needs_review`, không gắn mức. Đây không được coi là "sạch".

### Pass 2: thẩm định LLM (nền)

Mỗi 10 giây, `LlmJobDispatcher.TryDispatchOnceAsync` chạy **đúng một job**:

```
ngắt mạch mở? ─► máy rảnh? (CPU ≤ 50%, pin ≥ 20%) ─► kiểm tra contract_ver
   ─► lấy job (P1 > P2 > P3) ─► trích lại text, so hash ─► ChunkSelector (≤ 2000 token) ─► Masker
   ─► GuardedInferenceClient (qua Named Pipe) ─► trích lại, so hash lần 2
   ─► Merger.MergeFile ─► CompleteJob (1 transaction: job + llm_result + cập nhật scan_result)
```

Nguyên tắc của Merger ("LLM không quyết định"):

- Finding có `llm_policy=never` hoặc `hard_rule` (đã qua checksum) thì LLM không hạ được.
- LLM xác nhận thì cộng điểm. LLM bác bỏ với độ tin cậy ≥ 0,8 thì trừ điểm. Bác bỏ với độ tin cậy thấp thì giữ nguyên
  và đánh `needs_review`.
- LLM phát hiện thêm thì chỉ đánh `needs_review`, không tự nâng điểm.

### Phân nhóm job

| Nhóm | Nguồn | Hạn ngạch mặc định |
|---|---|---|
| P1 | Vùng xám | 200/giờ (vượt thì hoãn) |
| P2 | Tên file hoặc thư mục đáng nghi (`SuspiciousKeywords`) | 300/ngày (vượt thì bỏ) |
| P3 | Lấy mẫu 2% theo `file_hash` | 50/ngày (vượt thì bỏ) |

Hàng đợi tối đa 1.000 job `pending`. Vượt thì P2 và P3 bị bỏ.

---

## 5. Mô hình dữ liệu (SQLite `data\dlp.db`)

| Bảng | Vai trò | Ghi bởi |
|---|---|---|
| `file_state` | path, size, mtime, file_hash (cache tầng 1 và 2) | Pass 1 |
| `scan_result` | status (`final`/`provisional`), s1, s_final, nhãn, `needs_review`, `needs_ocr`, `source`, `rules_ver`, `merge_policy_ver`, `file_conf` | Pass 1, Merger |
| `finding` | rule_id, điểm, số lần, `hard_rule`, vị trí. **Không lưu giá trị nhạy cảm** | Pass 1 |
| `llm_job` | Hàng đợi và máy trạng thái | Pass 1, Dispatcher |
| `llm_result`, `llm_cache` | Kết quả LLM theo job hoặc theo hash | Dispatcher |
| `audit` | Lịch sử chuyển trạng thái job | Dispatcher |

Máy trạng thái `llm_job`:

```
pending ─► running ─► done
   ▲          ├─► stale     (file đổi trong lúc chờ)
   │          ├─► pending   (lỗi tạm, chờ 1 → 5 → 20 phút, tối đa 3 lần)
   │          └─► failed / timeout
   └── khởi động lại: running → pending
```

Dọn dẹp mỗi 24 giờ: xoá job `stale`/`failed` quá 30 ngày, `llm_cache` quá 90 ngày, `audit` quá 180 ngày.
Job `done` và `llm_result` giữ vĩnh viễn vì làm cache.

---

## 6. Đồng thời

| Chỗ | Cơ chế |
|---|---|
| Pass 1 | Mỗi file một task debounce. `SemaphoreSlim(Watch:MaxConcurrency = 6)` giới hạn số file xử lý cùng lúc. Dict dùng chung là `ConcurrentDictionary` |
| Ghi DB | Một kết nối ghi có khoá, một kết nối đọc, chế độ WAL |
| Pass 2 | Đúng 1 job một lúc, vì vòng lặp `await` tuần tự |
| Regex | Timeout từng rule để chống ReDoS |
| Watcher | Buffer 64 KB. Tràn thì quét bù toàn thư mục |

---

## 7. Chịu lỗi (thoái hoá mềm)

| Sự cố | Cách xử lý |
|---|---|
| Crash | SCM tự khởi động lại sau 5 giây (tối đa 3 lần/ngày); job `running` quay về `pending` |
| Mở DB lỗi | Service chạy tiếp, không ghi DB |
| LLM chết hoặc chậm | `CircuitBreaker` (3 lỗi liên tiếp thì mở 2 phút, tăng dần tới 30 phút); quay về chế độ chỉ regex |
| Lệch `contract_ver` | Không gửi request |
| File đổi lúc chờ LLM | So hash 2 lần, nếu khác thì `stale` |
| Cấu hình sai | Dùng mặc định và cảnh báo, không dừng |
| Model FileConfidence lỗi | Bỏ qua điểm này |
| Lỗi trong vòng LLM | Bắt, ghi log, thử lại ở lượt sau |

---

## 8. Bảo mật

- Thư mục `data/` bị khoá ACL: chỉ SYSTEM, Administrators và tài khoản đang chạy service có Full Control.
- DB và log **không chứa giá trị nhạy cảm nguyên văn** (CLAUDE.md mục 7.5).
- `Masker` che giá trị hai lớp trước khi gửi LLM. Chunk còn sót giá trị qua checksum thì bị bỏ.
- `PipeServerTrustPolicy` xác thực exe của LLM host (tuỳ chọn, qua `Llm:ServerExePath`).
- DLL Tika native được ghim SHA-256 trước khi nạp.
- Log exception chỉ ghi tên loại, không ghi `ex.Message`.

---

## 9. Cấu hình (`appsettings.json`)

| Mục | Khoá | Mặc định trong file |
|---|---|---|
| `Scan` | `MinConfidence` | 0,60 |
| `Score` | `THigh` / `TLow` | 0,80 / 0,10 (mặc định *trong code* `RouterOptions` là 0,90 / 0,30) |
| `Storage` | `DbPath` | `data\dlp.db` (tương đối so với thư mục exe) |
| `Watch` | `MaxConcurrency` | 6 |
| `TikaNative` | `Enabled`, `DllPath`, `Sha256`, `TimeoutMs`... | Tắt |
| `Llm` | `PipeName`, `JobDeadlineSeconds`, `MinTextChars`, `SuspiciousKeywords`, `SampleRatePct`, `Quota`, các mốc dọn dẹp | Xem file |
| `FileConfidence` | `ModelPath` | `confidence\file-confidence-v1.json` (không có trong file, dùng mặc định trong code) |

Ghi chú: các khoá `Llm:TokenBudget`, `Llm:MaxCpuPercent`, `Llm:MinBatteryPercent`, `Llm:RequireServerSignature`,
`Llm:ServerExePath` chỉ có mặc định trong `Worker.cs` và không có trong `appsettings.json`.

---

## 10. Khoảng trống kiến trúc đã biết

Chi tiết và mức chắc chắn của từng mục xem `luong-va-thiet-ke-ky-thuat.md` mục 5.6.

| Khoảng trống | Hệ quả kiến trúc |
|---|---|
| Watch-folder hard-code trong `Worker.cs`, chỉ 1 thư mục (R8) | Không triển khai được sang máy khác nếu không build lại |
| Không xử lý xoá hoặc di chuyển file (R4) | DB giữ bản ghi của file đã mất |
| Watcher lỗi ngoài tràn buffer thì không tự dựng lại (R6) | Service "sống" nhưng ngừng quét |
| Pass 2 không dùng chung giới hạn đọc của Pass 1 (R1, R2) | File lớn có thể luôn `stale` |
| `Worker.cs` làm quá nhiều việc: nạp cấu hình, lắp ráp, 2 vòng lặp, dọn dẹp | Khó test và khó mở rộng. Có thể tách thành các hosted service riêng |
| Service chạy bằng LocalSystem (R3) | Lỗi parser file thì có quyền SYSTEM |
| Chỉ phân loại, chưa chặn hay cảnh báo (R13) | Mới là "Classification", chưa là "Prevention" |
