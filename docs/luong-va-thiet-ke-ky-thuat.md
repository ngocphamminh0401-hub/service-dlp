# Luồng người dùng, luồng kỹ thuật và phân tích Technical Design — DlpService

> Viết ngày 03/11/2026, đọc trực tiếp từ code hiện tại (`Program.cs`, `Worker.cs`, `Watching/`, `Reading/`, `Scanning/`,
> `Classification/`, `Llm/`, `Storage/`, `scripts/`, `appsettings.json`). Thay thế phần "luồng" của `tai-lieu-ky-thuat.md`
> (bản đó chưa có: giới hạn đồng thời, nhận diện định dạng theo magic bytes, Tika native, FileConfidence shadow).
> Mục 5 ghi các điểm yếu tìm thấy khi đọc code — **chưa sửa gì**, chỉ là đề xuất.

---

## 1. Service này làm gì (1 đoạn)

DlpService là Windows Service (.NET 10 Worker) chạy nền, **theo dõi 1 thư mục**, trích text từ file
(txt/csv/docx/pptx/xlsx/xls/pdf/eml, thêm doc/ppt/rtf nếu bật Tika native), **quét regex + validator** để tìm dữ liệu
nhạy cảm, **gán mức** (Public/Internal/Confidential/Restricted) và **domain** (Finance/HR/PII/Org), tính **độ tin cậy S1**,
rồi **ghi kết quả vào SQLite**. File "không chắc" (vùng xám) được xếp hàng để **1 tiến trình LLM riêng** (gRPC qua
Named Pipe) thẩm định lại khi máy rảnh, sau đó **Merger** gộp ý kiến LLM vào điểm cuối `S_final`.

Service hiện **chỉ phân loại và ghi nhận** — chưa chặn, chưa cảnh báo người dùng, chưa gắn nhãn vào file.

---

## 2. Tác nhân (ai tương tác với hệ thống)

| Tác nhân | Tương tác qua | Mục đích |
|---|---|---|
| **Người dùng cuối** | Tạo/sửa/đổi tên file trong `watch-folder` | Không biết service tồn tại — service quan sát thụ động |
| **Quản trị viên / vận hành** | `install-service.ps1`, `sc.exe`, `appsettings.json`, thư mục `rules/` | Cài, cấu hình, bật/tắt tính năng, cập nhật rule |
| **Người rà soát (reviewer)** | File log, lệnh `DlpService.exe explain <file_id>`, truy vấn SQLite | Xem file nào nhạy cảm, file nào `needs_review`, vì sao |
| **LLM host** (`DlpLlmHost` / `DlpFakeLlm`) | Named Pipe `dlp-llm-host`, contract `inference.proto` | Thẩm định file vùng xám |

---

## 3. Luồng người dùng (User Flows)

### UF1 — Cài đặt và khởi động

```
Admin: dotnet publish DlpService -c Release -o publish
   │
   ▼
Admin (PowerShell Admin): scripts\install-service.ps1
   │  kiểm tra quyền Admin + có publish\DlpService.exe + service chưa tồn tại
   │  sc create DlpService start=auto   (tài khoản chạy: LocalSystem — không chỉ định obj=)
   │  sc failure: crash → tự khởi động lại sau 5 giây (tối đa 3 lần / ngày)
   ▼
Admin: sc.exe start DlpService
   │
   ▼
Service khởi động (xem T0) → quét toàn bộ file đang có trong watch-folder → bắt đầu theo dõi
   │
   ▼
Admin kiểm tra: logs\dlpservice-YYYY-MM-DD.log  (dòng "Đang theo dõi thư mục", "Quét ...: N file")
```

### UF2 — Người dùng lưu 1 file (luồng chính)

```
Người dùng lưu "bang_luong_T10.xlsx" vào watch-folder (hoặc thư mục con)
   │
   ▼  (~300 ms sau lần lưu cuối cùng — gộp nhiều sự kiện của cùng 1 lần lưu)
Service đọc → quét → phân loại → ghi DB
   │
   ├─ Sạch                → final, Public          (có thể vẫn được LLM lấy mẫu: P2 vì tên "luong", hoặc P3 ngẫu nhiên 2%)
   ├─ Nhạy cảm, chắc chắn → final, mức X           (S1 ≥ 0,80)
   ├─ Nhạy cảm, vùng xám  → provisional, needs_review, tạo job P1 → LLM xét lại khi máy rảnh → final
   ├─ Quét chưa trọn      → provisional, needs_review, không gắn mức (có rule regex bị timeout)
   └─ Không đọc được      → chỉ ghi log cảnh báo (file khoá, quá lớn, hỏng, có mật khẩu…)
   │
   ▼
Người dùng KHÔNG nhận được phản hồi gì (không popup, không chặn, không nhãn).
```

Các biến thể đã được xử lý:
- Lưu lại mà không sửa nội dung → bỏ qua (cache size/mtime hoặc hash nội dung).
- Trình soạn thảo ghi file tạm rồi đổi tên → bắt bằng sự kiện `Renamed`.
- File khoá tạm của Office `~$abc.docx` → bỏ qua.
- File đổi đuôi (`.docx` → `.txt`) → nhận diện theo magic bytes, đọc bằng đúng bộ đọc, ghi cảnh báo.
- PDF scan (không có chữ) → đánh dấu `needs_ocr`, **không** coi là sạch thật.

Biến thể **chưa** xử lý: xoá file, di chuyển file ra ngoài thư mục (xem mục 5).

### UF3 — Rà soát kết quả

```
Reviewer mở log  ──►  dòng [DLP] <đường dẫn>: mức Confidential / vùng xám ... needs_review
        │
        ├─► tìm file_id trong SQLite (data\dlp.db, bảng file_state / scan_result WHERE needs_review = 1)
        │
        ▼
publish\DlpService.exe explain <file_id>      (chạy console, KHÔNG khởi động service)
   in ra: status, source (regex|llm), S1, S_final, rules_ver (+ cảnh báo nếu rule đã đổi),
          danh sách finding (rule_id, điểm, số lần, hard_rule, llm_policy), trạng thái job LLM, kết quả LLM
```

Không có UI, không có API, không có báo cáo tổng hợp — reviewer phải tự truy vấn DB.

### UF4 — Thay đổi rule / cấu hình

```
Admin sửa rules\<domain>\*.json hoặc appsettings.json
   │
   ▼
sc.exe stop DlpService && sc.exe start DlpService      (BẮT BUỘC — không có hot-reload)
   │
   ▼
rules_ver mới được tính → kết quả quét MỚI ghi kèm rules_ver mới.
File cũ không đổi nội dung KHÔNG bị quét lại (cache tầng 1/2) → vẫn mang kết quả của bộ rule cũ.
```

### UF5 — Bật các thành phần tuỳ chọn

| Thành phần | Cách bật | Mặc định |
|---|---|---|
| LLM thẩm định | Chạy LLM host lắng nghe pipe `Llm:PipeName`; tuỳ chọn `Llm:ServerExePath` để xác thực exe server | Luôn thử kết nối; không có server → tự về chế độ regex-only (circuit breaker mở) |
| Tika native (doc/ppt/rtf hoặc thay toàn bộ bộ đọc) | `TikaNative:Enabled=true` + `Sha256` của DLL | Tắt |
| FileConfidence 2 nhánh | Có file `confidence/file-confidence-v1.json` | Bật ở chế độ **shadow** (chỉ ghi, không quyết định) |

---

## 4. Luồng kỹ thuật (Technical Flows)

### Sơ đồ thành phần

```
                         ┌──────────────────────── DlpService.exe (1 tiến trình) ─────────────────────────┐
                         │                                                                                  │
 watch-folder ──events──►│ Watching/FileWatcherService ──► Reading/FileReaderService ──► Scanning/RegexScanner│
                         │   debounce 300ms                 FileTypeSniffer             RuleLoader (rules/*.json)
                         │   SemaphoreSlim(6)               Extractors/* , Tika native  Validators (Luhn, MST…)│
                         │          │                                                         │             │
                         │          ▼                                                         ▼             │
                         │   Classification/: ClassificationEngine(modules) → FileScorer(S1) → ScoreRouter   │
                         │                    → EscalationPolicy(P1/P2/P3)   FileConfidence (shadow)         │
                         │          │                                                                       │
                         │          ▼                                                                       │
                         │   Storage/ResultStore ──► SQLite data\dlp.db (WAL)                               │
                         │          ▲                                                                       │
                         │          │ TryPickNextJob / CompleteJob                                          │
                         │   Llm/LlmJobDispatcher (vòng 10 s) ─► ChunkSelector → Masker                      │
                         │          │                 MachineIdleChecker, CircuitBreaker                    │
                         └──────────┼───────────────────────────────────────────────────────────────────────┘
                                    │ gRPC over Named Pipe "dlp-llm-host"
                                    ▼
                         DlpLlmHost.exe (tiến trình riêng, model ONNX)  → Merger (trong DlpService) → S_final
```

### T0 — Khởi động (`Program.cs` → `Worker.ExecuteAsync`)

1. `Program.cs`: nếu tham số đầu là `explain` → chạy `ExplainCommand` rồi thoát (không lên service).
2. Dựng Host: `AddWindowsService("DlpService")`, logger EventLog + **FileLogger** (`logs/`), đăng ký `Worker`.
3. `Worker.ExecuteAsync`, theo đúng thứ tự:
   1. `TikaNativeRuntime.Configure` — phải chạy **trước** khi tạo `FileReaderService` (bộ đọc đăng ký bằng reflection lúc tạo).
   2. `RuleLoader.LoadFromFolder(rules/)` → danh sách pattern; `ComputeRulesVersion` → `rules_ver`.
   3. Đọc `Scan:MinConfidence`, `Score:{THigh,TLow}` — sai thì dùng mặc định + cảnh báo, không dừng service.
   4. Tạo `RegexScanner`, `ClassificationEngine` (tự nạp module `IClassifierModule` bằng reflection).
   5. Mở `ResultStore` (SQLite, WAL, khoá ACL thư mục `data/`). Lỗi → service vẫn chạy, chỉ không ghi DB.
   6. `RecoverRunningJobs`: job `running` của lần chạy trước → `pending`.
   7. Dọn dẹp lần đầu (`PurgeOldJobs/LlmCache/Audit`).
   8. Nạp model FileConfidence (lỗi → bỏ qua).
   9. Tạo `FileWatcherService` (giữ ở **trường** của Worker để GC không thu hồi) → `StartWatching`:
      bật `FileSystemWatcher` (đệ quy, buffer 64 KB, filter theo đuôi hỗ trợ) **rồi** quét đồng bộ mọi file có sẵn.
   10. `EnqueueMissingGreyJobs` — tạo bù job cho file vùng xám chưa có job (chạy **sau** lượt quét đầu để dùng hash mới nhất).
   11. Chạy song song 2 vòng lặp: **vòng chính** (1 phút/lần: log nhịp + dọn dẹp mỗi 24 h) và **vòng LLM** (10 giây/lần).

### T1 — Pass 1: xử lý 1 file (`FileWatcherService.ProcessFile`)

```
Sự kiện Created/Changed/Renamed
  │ IsSupported? (đuôi hỗ trợ, không phải "~$...")             không → bỏ
  │ huỷ lần chờ cũ của cùng file, chờ 300 ms (debounce)
  │ _gate.WaitAsync()  — tối đa 6 file cùng lúc
  ▼
① Chụp size + mtime (TRƯỚC khi đọc)
② Cache tầng 1: file_state có kết luận và size/mtime trùng                    → Unchanged (không mở file)
③ FileReaderService.TryReadFile
     FileTypeSniffer (magic bytes) → chọn lại bộ đọc nếu đuôi sai
     kiểm tra kích thước tối đa của bộ đọc
     đọc streaming từng block, dừng ở 2.000.000 ký tự, timeout 30 s (kiểm tra giữa các block)
     chuẩn hoá Unicode NFC; text rỗng mà file > 0 byte → NeedsOcr
     thất bại                                                                  → log cảnh báo, Failed
④ SHA-256 của text
⑤ Cache tầng 2: hash trùng file_state → cập nhật size/mtime                    → Unchanged
   Cache RAM (_lastContentHash) trùng                                           → Unchanged
⑥ RegexScanner.Scan(text) → Matches + TimedOutPatterns + Telemetry
⑦ Rẽ nhánh:
   ┌ Quét chưa trọn (rule timeout) → provisional, needs_review, S1=0, không nhãn; xoá hash RAM để lần sau thử lại
   ├ 0 match → Public, final. EscalationPolicy(NoMatch): P2 (tên đáng nghi) / P3 (mẫu 2% theo hash) / không
   └ Có match:
       ClassificationEngine.Classify → mức = max các module, danh sách domain
       FileScorer.Compute → S1 theo từng mức (noisy-OR top-5 giá trị/rule × hệ số mật độ)
       ScoreRouter.Route → confidence = S1 của đúng mức đã gán
            ≥ 0,80 Certain │ 0,10–0,80 Grey │ < 0,10 Low
       FileConfidence (shadow) — chỉ log + ghi cột, không ảnh hưởng quyết định
       EscalationPolicy.Decide:
            Certain → không gửi LLM
            text < 200 ký tự hoặc thuộc ExcludedPaths → không gửi
            Grey → P1;  Low → P2/P3 như file sạch
       Grey + P1  → status = provisional  (chờ LLM)
       Grey không đủ điều kiện → final + needs_review (không có ai chốt hộ)
       còn lại   → final
⑧ ResultStore.SaveScan — 1 transaction: file_state + scan_result + finding (+ llm_job nếu còn hạn ngạch:
   P1 200/giờ (vượt → hoãn), P2 300/ngày, P3 50/ngày, tối đa 1.000 job pending (vượt → bỏ))
⑨ Log [Perf] thời gian từng bước
```

Finding chỉ lưu `rule_id`, điểm, số lần khớp, `offset` đầu tiên — **không lưu giá trị nhạy cảm**.

### T2 — Pass 2: thẩm định LLM (`LlmJobDispatcher.TryDispatchOnceAsync`, mỗi 10 giây, 1 job/lần)

```
① CircuitBreaker đang Open? (3 lỗi liên tiếp → mở 2 phút, tăng dần tới 30 phút)   → bỏ lượt
② MachineIdleChecker: CPU ≤ 50%, nếu chạy pin thì pin ≥ 20%                     → không rảnh: bỏ lượt
③ Lần đầu / sau mất kết nối: GetInfo → contract_ver khớp?                         → lệch: regex-only
④ ResultStore.TryPickNextJob — pending, next_try_utc ≤ now, ưu tiên P1 > P2 > P3 → running
⑤ Trích lại text bằng CÙNG bộ đọc (FindExtractor có sniff), NFC, so hash          → khác: stale
⑥ ChunkSelector chọn đoạn liên quan trong ngân sách 2.000 token
   Masker che giá trị (token giữ hình dạng); chunk còn sót giá trị qua checksum → bỏ chunk
⑦ GuardedInferenceClient.TryClassifyAsync (deadline 600 s)
     không phản hồi / BUSY / NOT_READY → FailJob (thử lại sau 1 → 5 → 20 phút, rồi failed)
     TIMEOUT → TimeoutJob;  OUTPUT_TOO_LONG → failed vĩnh viễn
⑧ Trích lại lần 2, so hash (file có thể đổi trong lúc chờ)                         → khác: stale
⑨ Merger.MergeFile (theo từng finding):
     llm_policy = never hoặc không có verdict → giữ S1
     LLM xác nhận → S1 + 0,30 × conf
     LLM bác bỏ: hard_rule (checksum) → giữ; conf ≥ 0,8 → S1 − 0,40 × conf; conf < 0,8 → giữ + needs_review
     các chunk mâu thuẫn → giữ + needs_review;  LLM phát hiện thêm → needs_review (không tự nâng điểm)
     S_final = max(S1 file, điểm từng rule)
⑩ CompleteJob — 1 transaction: llm_job=done, llm_result, cập nhật scan_result (S_final, source, needs_review)
```

### T3 — Vòng đời `llm_job`

```
          tạo (SaveScan / EnqueueMissingGreyJobs)
                    │
                    ▼
   ┌────────────► pending ──TryPickNextJob──► running ──ok──────────► done
   │                ▲                           │
   │                │ service khởi động lại     ├─ file đổi / không trích được ──► stale
   │                └───────────────────────────┤
   │   lỗi tạm (attempts < 3, chờ 1/5/20 phút)   ├─ lỗi tạm / timeout ──► (pending | failed)
   └─────────────────────────────────────────────┘
                                                └─ OUTPUT_TOO_LONG ──► failed
Dọn dẹp: stale/failed > 30 ngày bị xoá. done + llm_result giữ vĩnh viễn (làm cache).
```

### T4 — Dừng service

`stoppingToken` huỷ → 2 vòng lặp thoát → `FileWatcherService.Dispose` (tắt watcher, huỷ các file đang chờ debounce;
file đang xử lý dở chạy nốt, `ResultStore` tự bỏ ghi nếu đã đóng) → `TikaNativeRuntime.Shutdown` → đóng DB.
Job đang `running` sẽ được đưa về `pending` ở lần khởi động sau (T0 bước 6).

---

## 5. Phân tích Technical Design

### 5.1 Các quyết định thiết kế chính

| Quyết định | Lý do | Đánh đổi |
|---|---|---|
| **Quét theo sự kiện** (`FileSystemWatcher`) + quét toàn bộ lúc khởi động + quét bù khi tràn buffer | Phản hồi gần thời gian thực, không tốn CPU khi rảnh | Watcher có thể mất sự kiện; chỉ 1 thư mục |
| **Regex trước, LLM sau** (2 pass, LLM chạy bất đồng bộ) | Regex rẻ, xác định, giải thích được; LLM đắt nên chỉ dành cho vùng xám | Kết quả vùng xám ở trạng thái `provisional` có thể rất lâu |
| **LLM ở tiến trình riêng**, gRPC qua Named Pipe | Cô lập RAM/crash của model; service vẫn chạy khi LLM chết | Phải quản lý vòng đời 2 tiến trình, xác thực server |
| **"LLM không quyết định"** — Merger trong service, `hard_rule` không bị bác bỏ, `llm_policy=never` | An toàn: lỗi của LLM không hạ được dữ liệu đã qua checksum | Phải tinh chỉnh `WUp/WDown/ngưỡng 0,8` bằng dữ liệu thật |
| **Rule là dữ liệu JSON**, module domain nạp bằng reflection | Thêm rule/domain không sửa core | Không có schema validation lúc build; lỗi rule chỉ lộ lúc chạy |
| **SQLite nhúng, WAL**, 1 kết nối ghi + khoá, 1 kết nối đọc | Không cần cài DB; transaction gộp scan + job | Ghi bị tuần tự hoá; chỉ 1 máy |
| **Cache 2 tầng** (size/mtime → hash nội dung) | Bỏ qua phần đắt nhất (trích text) cho file không đổi | Đổi rule không tự quét lại file cũ |
| **Thoái hoá mềm**: lỗi DB, lỗi LLM, lỗi model FileConfidence, cấu hình sai → cảnh báo, chạy tiếp | Service DLP không được "chết" vì 1 thành phần phụ | Lỗi có thể bị bỏ qua lâu nếu không ai đọc log |
| **Giới hạn đồng thời = 6** | Đo thực tế: thông lượng bão hoà ở 6 luồng, p95 thấp nhất | Lượt quét lúc khởi động không được hưởng (xem R5) |
| **Không lưu/không log giá trị thật**; mask trước khi gửi LLM | Kho kết quả và log không trở thành nguồn rò rỉ | Khó gỡ lỗi false positive (phải mở file gốc) |

### 5.2 Mô hình dữ liệu (SQLite `data\dlp.db`)

| Bảng | Vai trò | Ghi bởi |
|---|---|---|
| `file_state` | path, size, mtime, file_hash — cache tầng 1/2 | Pass 1 |
| `scan_result` | status (final/provisional), s1, s_final, labels_json, needs_review, needs_ocr, source, rules_ver, merge_policy_ver, file_conf | Pass 1, Merger |
| `finding` | rule_id, score, count, hard_rule, validator_passed, location | Pass 1 |
| `llm_job` | hàng đợi + máy trạng thái (T3), priority, attempts, next_try_utc | Pass 1, Dispatcher |
| `llm_result` / `llm_cache` | kết quả LLM theo job / theo hash | Dispatcher |
| `audit` | lịch sử chuyển trạng thái job | Dispatcher |

Điểm tốt: có `rules_ver` + `merge_policy_ver` → truy vết được kết quả tính bằng phiên bản nào (`explain` báo drift).

### 5.3 Đồng thời và hiệu năng

- Pass 1: N task debounce, `SemaphoreSlim(6)` chặn số file xử lý cùng lúc; các dict dùng chung là `ConcurrentDictionary`;
  debounce dùng `TryRemove(KeyValuePair)` để không xoá nhầm CTS mới hơn.
- Ghi DB tuần tự qua `_writeLock` — với 6 luồng quét, ghi DB thường không phải nút cổ chai (trích text mới là).
- Pass 2: đúng 1 job tại 1 thời điểm (vòng lặp `await` tuần tự), chỉ khi máy rảnh.
- Regex có timeout từng rule → không bị ReDoS treo luồng; timeout thì file bị đánh "chưa trọn" thay vì "sạch".
- Có số đo thật: `docs/bao-cao-hieu-nang-service-dlp.md`, `docs/danh-gia-xu-ly-dong-thoi.md`, `tools/perf/`.

### 5.4 Chịu lỗi

| Sự cố | Cách xử lý hiện tại |
|---|---|
| Service crash | SCM tự khởi động lại sau 5 s (3 lần/ngày); job `running` → `pending` |
| Kill giữa lúc ghi | Transaction SQLite + WAL; đã có test kill thật (`tools/resilience`) |
| LLM chết / chậm | Circuit breaker + backoff; deadline job; regex-only |
| Lệch contract | `GetInfo` mỗi lần kết nối, lệch → không gửi request |
| File đổi trong lúc chờ LLM | So hash 2 lần (trước và sau) → `stale` |
| Watcher tràn buffer | Quét bù toàn thư mục |
| Watcher lỗi khác (mất thư mục, mất quyền) | **Chỉ log** — không tự khởi động lại (R6) |
| GC thu hồi watcher | Đã sửa: giữ tham chiếu mạnh từ `Worker` |

### 5.5 Bảo mật

Đã có: ACL thư mục `data/` (chỉ SYSTEM + Administrators + tài khoản service), ghim SHA-256 cho DLL Tika native,
tuỳ chọn xác thực exe server LLM (`PipeServerTrustPolicy`), masker 2 lớp trước khi gửi LLM, không lưu giá trị nhạy cảm,
chỉ log tên loại exception (không log `ex.Message` có thể chứa nội dung file).

### 5.6 Điểm yếu và rủi ro (xếp theo mức ảnh hưởng)

> "Đã xác nhận trong code" = đọc code thấy rõ; "cần test" = suy luận từ code, chưa chạy thử để chứng minh.

| # | Vấn đề | Vị trí | Ảnh hưởng | Mức chắc |
|---|---|---|---|---|
| **R1** | Pass 1 băm text **đã cắt** ở 2.000.000 ký tự, Pass 2 băm text **đầy đủ** → file > 2 triệu ký tự luôn lệch hash → job luôn `stale_before`, file vùng xám không bao giờ được LLM chốt | `FileReaderService.TryReadFile` vs `LlmJobDispatcher` dòng 97, 106 | File lớn (bảng lương, export CRM…) — đúng loại file quan trọng nhất | Cần test |
| **R2** | Pass 2 trích text **không có** timeout, không giới hạn ký tự, `ToList()` cả file vào RAM, và trích **2 lần** mỗi job | `LlmJobDispatcher` dòng 93, 165 | File bất thường có thể treo/ngốn RAM vòng LLM — đúng thứ Pass 1 đã chặn | Đã xác nhận trong code |
| **R3** | Service chạy bằng **LocalSystem** (script không có `obj=`), lại nạp DLL native trong tiến trình | `scripts/install-service.ps1` dòng 38 | Lỗ hổng ở bộ đọc file (parser docx/pdf/Tika) = quyền SYSTEM | Đã xác nhận trong code |
| **R4** | Không xử lý **xoá / di chuyển** file: không nghe `Deleted`; đổi tên → quét lại từ đầu, bản ghi đường dẫn cũ còn trong DB; `_lastContentHash` phình theo số đường dẫn từng thấy | `FileWatcherService.StartWatching`, `_lastContentHash` | DB báo file nhạy cảm "đang tồn tại" dù đã xoá; RAM tăng dần | Đã xác nhận trong code |
| **R5** | Quét lúc khởi động và quét bù khi tràn buffer chạy **tuần tự 1 file/lần** (`_gate.Wait()` trong vòng `foreach`), quét bù còn chiếm 1 luồng ThreadPool suốt thời gian quét | `ScanExistingFiles` | Khởi động với thư mục lớn chậm ~6 lần so với khả năng | Đã xác nhận trong code |
| **R6** | Watcher gặp lỗi khác tràn buffer → chỉ log, không tự dựng lại | `OnWatcherError` | Service "sống" nhưng ngừng quét âm thầm | Đã xác nhận trong code |
| **R7** | Mask trong log giữ 3 ký tự đầu + 3 cuối: CCCD 12 số lộ 6 số; mỗi chỗ khớp 1 dòng log mức Information kèm đường dẫn | `FileWatcherService.Mask`, dòng 439 | Log thành nguồn rò rỉ một phần, trái nguyên tắc 5 của CLAUDE.md | Đã xác nhận trong code |
| **R8** | Watch-folder **hard-code** `D:\Service-DLP\watch-folder`, chỉ 1 thư mục | `Worker.cs` dòng 148 | Không triển khai được sang máy khác nếu không build lại | Đã xác nhận trong code |
| **R9** | Đổi rule không quét lại file cũ (cache tầng 1/2 không xét `rules_ver`) | `ProcessFile` bước ② ⑤ | Rule mới không áp cho dữ liệu đã có | Đã xác nhận trong code |
| **R10** | Năng lực LLM có thể thấp hơn hạn ngạch: 1 job/lần, vòng 10 s, deadline 600 s, chỉ khi máy rảnh; trong khi P1 cho tới 200 job/giờ | `Worker`, `appsettings.json` | Hàng đợi chạm 1.000 → job bị bỏ, file kẹt `provisional` | Cần đo |
| **R11** | 2 hệ chấm điểm song song (S1 + Router đang quyết định, FileConfidence shadow) chưa có tiêu chí để chọn 1 | `FileScorer`, `FileConfidence` | Tăng độ phức tạp, dễ lệch khi chỉnh 1 bên | Nhận định thiết kế |
| **R12** | Vùng xám rất rộng (`TLow=0,10`, `THigh=0,80`) | `appsettings.json` | Nhiều file vào P1 → khuếch đại R10 | Cần đo trên dữ liệu thật |
| **R13** | Kết quả không đi ra đâu ngoài SQLite/log: không cảnh báo, không chặn, không gắn nhãn, không API/báo cáo | Toàn hệ thống | Đúng nghĩa là "Data Classification", chưa phải "Prevention" | Nhận định thiết kế |

### 5.7 Đề xuất thứ tự xử lý (chưa làm — cần bạn đồng ý từng mục)

1. **R1 + R2** (cùng chỗ): cho Pass 2 dùng chung đường đọc của Pass 1 (cùng giới hạn ký tự, timeout) để hash luôn khớp.
   Viết test tái hiện với 1 file > 2 triệu ký tự **trước** khi sửa.
2. **R6 + R4**: dựng lại watcher khi lỗi; nghe `Deleted`/`Renamed` để cập nhật/xoá bản ghi DB và dọn `_lastContentHash`.
3. **R8**: đưa `Watch:Folders` vào `appsettings.json` (danh sách thư mục).
4. **R3**: cài service với virtual account `NT SERVICE\DlpService`, cấp quyền đọc watch-folder + ghi `data/`, `logs/`.
5. **R7**: log chỉ tên rule + số lượng ở Information; chi tiết vị trí xuống Debug, mask mạnh hơn.
6. **R5**: quét khởi động song song qua cùng trạm gác (`WaitAsync` + `Task.WhenAll` theo lô).
7. **R10/R12**: đo thông lượng LLM thật rồi chỉnh ngưỡng/hạn ngạch cho khớp năng lực.
8. **R9, R11, R13**: quyết định sản phẩm — cần bàn trước khi code.
