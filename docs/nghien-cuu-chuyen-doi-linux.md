# Nghiên cứu source code Windows để xây dựng DlpService trên Linux

> Giai đoạn 1 — Phân tích: xác định phần lõi tái sử dụng, phần phụ thuộc Windows và các interface cần trừu tượng hóa.
> Căn cứ: đọc trực tiếp `DlpService/` (không dựa trên phỏng đoán). Mọi vị trí code ghi dạng `file:dòng` ở thời điểm 06/11/2026.
> Tài liệu này **bổ sung** cho `docs/ke-hoach-trien-khai-linux.md` (kế hoạch + quyết định D1–D9). Kế hoạch trả lời "làm thế nào, theo lộ trình nào";
> tài liệu này trả lời "code hiện tại thực sự đang gắn với Windows ở đâu, và phần nào dùng lại nguyên được".

---

## 1. Kết luận nhanh

1. **Lõi nghiệp vụ gần như sạch phụ thuộc Windows.** Các thư mục `Scanning/`, `Classification/`, `Reading/Extractors/`, `Text/` và phần lớn `Storage/` không dùng
   P/Invoke, registry, named pipe hay ACL. Chúng không tham chiếu namespace `DlpService.Llm` (đã grep: 0 file).
2. **Phụ thuộc Windows nằm ở một số điểm rất hẹp** (mục 3): 1 file ACL, 1 file P/Invoke lấy PID server pipe, 1 file đọc CPU/pin, 2 dòng đăng ký SCM/EventLog trong `Program.cs`, và `FileSystemWatcher`.
3. **Mối nguy lớn hơn không phải API Windows mà là "giả định ngầm"** (mục 4): đường dẫn viết bằng `\\` trong `appsettings.json`, thư mục dữ liệu/log đặt cạnh file chạy,
   thư mục theo dõi bị hard-code `D:\...`, so sánh đường dẫn không phân biệt hoa/thường. Những lỗi này **biên dịch được, chạy được, nhưng sai âm thầm** trên Linux.
4. **Cần chỉnh lại danh sách "tái sử dụng" ban đầu** (mục 2.2): `ChunkSelector`, `Masker`, `Queue/Worker (llm_job)` thực chất chỉ phục vụ LLM — Linux không có LLM nên không cần mang sang.
5. **Named Pipe → Unix Domain Socket không còn là việc phải làm** vì Linux không nối LLM. Contract `inference.proto` chỉ cần *giữ nguyên trong repo* (mục 6).

---

## 2. Phân loại thành phần

### 2.1 Nhóm A — Lõi dùng lại nguyên (không sửa logic)

| Thành phần | Vị trí | Nhận xét từ code |
|---|---|---|
| Scanner (Regex) | `Scanning/RegexScanner.cs`, `RuleLoader.cs`, `SensitivePattern.cs`… | Thuần .NET. `RuleLoader` sắp xếp file theo `OrdinalIgnoreCase` để kết quả ổn định giữa hệ điều hành (`RuleLoader.cs:35`) → `rules_ver` giống nhau giữa Windows/Linux |
| Validator | `Scanning/Validation/*.cs`, `Validators.cs` | Thuần toán (CCCD, MST, IBAN, Luhn, entropy). Không I/O |
| Scoring | `Scanning/CandidateScorer.cs`, `Classification/FileScorer.cs`, `FileConfidence.cs` | Thuần tính toán. `FileConfidence` đọc `confidence/file-confidence-v1.json` bằng đường dẫn tương đối `Path.Combine("confidence", ...)` (`Worker.cs:121`) — ổn trên Linux |
| Router / phân loại | `Classification/ScoreRouter.cs`, `ClassificationEngine.cs`, `Modules/*.cs`, `Merger.cs` | Thuần logic. Mô-đun domain nạp bằng reflection (không tên Windows) |
| Chuẩn hoá text | `Text/Normalizer.cs`, `Storage/ContentHash.cs` | Thuần. **Giữ nguyên** để `file_hash` Windows ↔ Linux khớp 100% (tiêu chí nghiệm thu GĐ2) |
| Bộ trích nội dung | `Reading/Extractors/*` (txt, csv, docx, pptx, xlsx, xls, pdf, eml), `FileTypeSniffer.cs` | Dùng OpenXml, ExcelDataReader, PdfPig — đều managed. `.xls` và `Windows-1258` cần bảng mã code page: đã gọi `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` ở `FileReaderService.cs:30`, gói này có sẵn trong .NET, chạy được trên Linux |
| SQLite | `Storage/ResultStore*.cs` | `Microsoft.Data.Sqlite` mang theo native SQLite cho từng RID (có linux-x64). WAL, `busy_timeout`, checkpoint nền đều là tính năng SQLite, không riêng Windows. Chỉ có **1 điểm Windows**: `RestrictFolder` (mục 3.4) |
| Tika native (tuỳ chọn) | `Reading/Native/*` | Mặc định **tắt** (`TikaNative:Enabled=false`, `appsettings.json:22`). Mã nạp dùng `NativeLibrary.Load` (đa nền tảng) nhưng file thật là `tika_native.dll` → cần build lại `.so`. Hoãn sang GĐ5 như kế hoạch |

### 2.2 Nhóm A' — Có trong danh sách "tái sử dụng" nhưng thực tế **không cần** cho bản Linux

| Mục trong danh sách | Thực tế trong code | Đề xuất |
|---|---|---|
| Chunk Selector | `Llm/ChunkSelector.cs` — chọn đoạn gửi cho LLM | Không mang sang. Để lại ở thư mục `Llm/` (chỉ Host.Windows dùng) |
| Masker | `Llm/Masker.cs` — che dữ liệu **trước khi gửi LLM** | Không mang sang. **Lưu ý:** hàm `Mask()` che giá trị khi *ghi log* là hàm riêng, `FileWatcherService.cs:690` — hàm này **phải** đi theo vì luật "không log dữ liệu nhạy cảm" (CLAUDE.md mục 7.5). Nên tách thành lớp dùng chung |
| Queue / Worker | Không có hàng đợi quét riêng. Đồng thời được điều khiển bởi `SemaphoreSlim` (`FileWatcherService.cs:80`) + debounce `Task.Delay(300)` (`:254`). Bảng `llm_job` + `LlmJobDispatcher` là hàng đợi **riêng của LLM** | Giữ cơ chế semaphore + debounce. Bỏ `llm_job` dispatcher |
| Merger | `Classification/Merger.cs` gộp kết quả theo domain/mức (không phải gộp kết quả LLM) | Dùng lại. Phần gộp S1 với S2 của LLM không có đầu vào trên Linux |
| SQLite "job, cache, audit" | Có 7 bảng: `file_state`, `scan_result`, `finding`, **`llm_job`, `llm_result`, `llm_cache`, `audit`** (`ResultStore.cs:116–192`) | Linux chỉ cần 3 bảng đầu. Xem rủi ro ở 4.6 |

### 2.3 Nhóm B — Phụ thuộc Windows, phải thay bằng adapter

Xem mục 3. Nhóm C (đa nền tảng nhưng có "giả định ngầm") xem mục 4.

---

## 3. Điểm phụ thuộc Windows đã xác nhận

### 3.1 Vòng đời service (SCM) — `Program.cs:18–21`
- `AddWindowsService(ServiceName="DlpService")` đăng ký với Service Control Manager. Gói `Microsoft.Extensions.Hosting.WindowsServices` có trong `DlpService.csproj:15`.
- Trên Linux không có SCM; thay bằng `AddSystemd()` (Microsoft.Extensions.Hosting.Systemd). Khi chạy không dưới systemd, cả hai đều **không làm gì** — nên có thể gọi cả hai có điều kiện ở host.
- Dừng service: .NET tự chuyển `SIGTERM` thành `StopAsync` → `ExecuteAsync` nhận `stoppingToken` huỷ. Đoạn `finally` ở `Worker.cs:210–218` (dispose watcher, shutdown Tika) **chạy đúng cả trên Linux**, không cần xử lý tín hiệu riêng. Cần test `kill -TERM` và `kill -9` (DB WAL phải không hỏng).

### 3.2 Log hệ thống — `Program.cs:28–42`
- `AddEventLog` đã bọc `OperatingSystem.IsWindows()` nên **không phá build Linux**.
- Code *đã tự thừa nhận* Event Log không đáng tin (`Program.cs:44–46`, `FileLogger.cs:2–3`) và dùng `FileLoggerProvider` làm kênh chính. Đây là lợi thế: trên Linux giữ `FileLoggerProvider` làm kênh chính, còn journald **đã có sẵn** vì console logger ghi ra stdout → systemd chuyển vào journal, **không cần viết provider journald riêng**.
- **Vấn đề:** `Program.cs:47` ghi log vào `AppContext.BaseDirectory/logs` — xem 4.2.

### 3.3 Named Pipe & xác minh server — `Llm/InferenceClient.cs:36–67`, `Llm/PipeServerVerifier.cs`
- `NamedPipeClientStream(".", pipeName, ...)` và `TokenImpersonationLevel.Anonymous` là Windows-style.
- `PipeServerVerifier` gọi `kernel32.dll!GetNamedPipeServerProcessId` bằng `DllImport` (`PipeServerVerifier.cs:26`). Trên Linux P/Invoke này sẽ ném `DllNotFoundException`.
- Chỉ dùng bởi `Worker.RunLlmDispatchLoopAsync` (`Worker.cs:246`). **Bản Linux không gọi nên không cần chuyển.** Điều cần đảm bảo là *không biên dịch/nạp* các file này vào host Linux.

### 3.4 Quyền thư mục dữ liệu (ACL) — `Storage/DataFolderSecurity.cs`, `ResultStore.cs:310–332`
- Dùng `DirectorySecurity`, `SecurityIdentifier(LocalSystemSid)`, `WindowsIdentity` — đánh dấu `[SupportedOSPlatform("windows")]`.
- Gọi từ `RestrictFolder`, đã chặn bằng `if (!OperatingSystem.IsWindows()) return;` (`ResultStore.cs:312`) → **trên Linux hiện hàm này thoát im lặng, DB không được khoá quyền nào**. Đây là chỗ "an toàn giả": chạy được nhưng bỏ mất yêu cầu bảo vệ DB (DB chứa đường dẫn file của mọi người dùng).
- Ngữ nghĩa cần giữ: *chỉ tài khoản dịch vụ + quản trị đọc/ghi được; áp xuống cả file con (`dlp.db`, `-wal`, `-shm`)*. Tương đương Linux: chủ `dlp`, thư mục `0700`, `UMask=0077` để file WAL/SHM tạo sau cũng `0600`.

### 3.5 Đo CPU và pin — `Llm/MachineIdleChecker.cs`
- `PerformanceCounter("Processor", ...)` và `GetSystemPowerStatus` (kernel32). Đã bọc `IsWindows()` và **fail-open**.
- Chỉ phục vụ "máy rảnh mới gọi LLM". Linux không cần. Nhưng **mục tiêu "nền < 2% CPU, burst ≤ 30%"** (D6) lại cần *một cơ chế khác*: giới hạn bằng cgroup của systemd (`CPUQuota`) chứ không tự đo trong code.
- `DlpService.csproj:22` vẫn tham chiếu `System.Diagnostics.PerformanceCounter`; gói này khôi phục được trên Linux nhưng ném `PlatformNotSupportedException` khi dùng. Nên chuyển vào project Windows.

### 3.6 Theo dõi file — `Watching/FileWatcherService.cs`
`FileSystemWatcher` của .NET **dùng được trên Linux** (bản chất là inotify) — nên đây không phải "viết lại hoàn toàn". Nhưng hành vi khác ở các điểm sau, đều đã thấy trong code:

| Điểm trong code | Windows | Linux |
|---|---|---|
| `InternalBufferSize = 64 * 1024` (`:109`) | Quyết định kích thước buffer | Không có tác dụng; hàng đợi do kernel quyết định (`max_queued_events`). Cần xác nhận khi thử |
| `OnWatcherError` + `InternalBufferOverflowException` (`:205`) → `ScanExistingFiles` quét bù | Tràn buffer | Tràn hàng đợi inotify cũng phát lỗi này; cơ chế quét bù **vẫn đúng** nhưng phải đo tần suất |
| `IncludeSubdirectories = true` (`:106`) | Một handle cho cả cây | **Mỗi thư mục con tốn 1 inotify watch** → `max_user_watches` (mặc định thấp trên kernel cũ) là giới hạn thực |
| `Filters.Add("*" + ext)` (`:114`) | Lọc ở tầng API | Lọc ở tầng .NET, vẫn đúng. Mẫu `*.xls` **không** khớp nhầm `.xlsx` như comment ở `:144` mô tả (quirk 8.3 chỉ có trên Windows) |
| `Created/Changed/Renamed` (`:116–118`) | Có | Có. **Không có handler `Deleted`** → file xoá/di chuyển ra ngoài vẫn còn trong DB (rủi ro R4 trong kế hoạch) |
| `_fileReader.IsSupported` chỉ lọc `~$` (`FileReaderService.cs:90`) | Đủ cho Office | Thiếu file tạm Linux: `.~lock.*#`, `*.swp`, `*~`, `*.part`, `*.crdownload` |
| Debounce 300 ms (`:254`) | Đủ | inotify bắn sự kiện theo từng lần `write()`; cần đo lại |
| Đọc file đang ghi | Có khoá chia sẻ; đọc dùng `FileShare.ReadWrite\|Delete` (`ExtractorHelpers.cs:49`) | **Không có khoá bắt buộc** — có thể đọc nửa chừng. `FileWatcherService.cs:317–325` đã chụp size/mtime *trước* khi đọc, nên file sửa giữa chừng sẽ được quét lại lần sau: **cơ chế này đã đúng hướng**, chỉ thiếu bước "so lại sau khi đọc" |

> `FileWatcherService` hiện gộp *3 trách nhiệm trong 1 lớp*: (1) nguồn sự kiện OS (`FileSystemWatcher`), (2) debounce/đồng thời, (3) pipeline xử lý 1 file (`ProcessFile`, ~215 dòng). Chỉ (1) là phụ thuộc OS. Đây chính là chỗ cần cắt để có `IFileChangeSource`.

---

## 4. "Giả định ngầm" — biên dịch được nhưng sai trên Linux

### 4.1 Dấu `\\` trong cấu hình — `appsettings.json:16, 25`
`"DbPath": "data\\dlp.db"` và `"DllPath": "native\\tika\\tika_native.dll"`. Trên Linux `\` là **ký tự hợp lệ trong tên file**, `Path.Combine` không đổi nó thành `/`.
Hậu quả: DB sẽ được tạo thành **một file tên `data\dlp.db`** ngay trong thư mục gốc, không phải thư mục `data/`. Không báo lỗi. → Phải dùng `/` (chạy được cả Windows) hoặc dựng đường dẫn bằng `Path.Combine` từ nhiều đoạn. Đáng làm **ngay cả với bản Windows**.

### 4.2 Dữ liệu và log đặt cạnh file chạy — `Worker.cs:31, 67–71`, `Program.cs:47`
`rules`, `data`, `confidence`, `logs` đều tính từ `AppContext.BaseDirectory`. Mô hình Linux đã chốt ở kế hoạch 5.4: `/opt/dlp-service` (chỉ đọc, root) tách khỏi `/var/lib/dlp-service`, `/var/log/dlp-service`, `/etc/dlp-service` — và unit systemd bật `ProtectSystem` nên **ghi vào thư mục cạnh file chạy sẽ bị từ chối**.
Cần 3 vị trí cấu hình riêng: thư mục *rule*, thư mục *dữ liệu*, thư mục *log*, mặc định lấy theo hệ điều hành, ghi đè được bằng cấu hình. Đây là nội dung của `IWatchRootProvider`/`IDataFolderGuard` mở rộng thành "bố cục thư mục".

### 4.3 Thư mục theo dõi bị hard-code — `Worker.cs:148–149`
`StartWatching(@"D:\Service-DLP\watch-folder")`. Tên ổ đĩa không tồn tại trên Linux. Chưa có `Watch:Folders` trong `appsettings.json` (chỉ có `Watch:MaxConcurrency`). Đây là rủi ro R8 trong kế hoạch, và là **chỗ chặn triển khai duy nhất** theo nghĩa nghiêm ngặt: code sẽ chạy nhưng không theo dõi gì.
Ngoài ra `FileWatcherService` chỉ nhận **1 thư mục** (`_watchFolder` là một chuỗi, `:43`); máy trạm Linux cần nhiều thư mục (`/home/*/Documents`, …) → cần danh sách + mở rộng mẫu `*`.

### 4.4 So sánh đường dẫn không phân biệt hoa thường
- `EscalationPolicy.IsExcluded` — `StartsWith(..., OrdinalIgnoreCase)` (`EscalationPolicy.cs:170`): trên Linux `/Data/Tmp` và `/data/tmp` là 2 thư mục khác nhau → loại trừ nhầm.
- `ResultStore.cs:317` — so gốc ổ đĩa (`Path.GetPathRoot`); trên Linux gốc là `/`.
- `PipeServerVerifier.cs:53` — Windows-only, bỏ qua.
- `file_state.path` có ràng buộc `UNIQUE` (`ResultStore.cs:118`): SQLite so chuỗi theo mặc định phân biệt hoa/thường (BINARY) — **đúng** cho Linux. Nhưng nếu dùng chung DB hay so khớp với bản Windows (không phân biệt) thì cần để ý.
- Các so sánh `OrdinalIgnoreCase` còn lại là so **từ khoá / tên rule / phần mở rộng** — không phải đường dẫn, an toàn. (`.PDF` vs `.pdf`: `_extractors` dùng `OrdinalIgnoreCase`, `FileReaderService.cs:24`, đúng ý muốn.)

### 4.5 Quyền đọc file của người dùng khác
Windows service chạy `LocalSystem` đọc được hầu hết. Linux: tài khoản `dlp` không đọc được `/home/<user>` mặc định (`0700` trên nhiều bản). Kế hoạch đã chọn `CAP_DAC_READ_SEARCH` (D5). Từ phía code: lỗi `UnauthorizedAccessException` khi đọc phải được log *một dòng, không gồm nội dung*, và file đó tính là `Failed` (đã có: `FileWatcherService.cs:514–518`).

### 4.6 Lõi vẫn "biết" LLM dù không dùng LLM
Dù namespace `Llm` không bị tham chiếu từ các thư mục lõi, **hành vi vẫn gắn LLM** ở 3 chỗ:
1. `ProcessFile` luôn gọi `Escalate(...)` → `EscalationPolicy.Decide` và truyền `llmPriority` vào `SaveScan`; `SaveScan` tạo job `llm_job` trong cùng transaction (`FileWatcherService.cs:484–506`, `ResultStore.cs`).
2. Vùng xám được ghi `provisional` *chờ LLM* (`:489–506`). Nếu không có LLM mà vẫn để `provisional`, file **kẹt mãi ở trạng thái chờ**. Nhánh đã có sẵn: *"không đủ điều kiện gửi LLM → final + needs_review"* (`:486–493`) — công tắc `Llm:Enabled=false` chỉ cần ép `waitsForLlm=false`.
3. `Worker.cs:74–80, 94–171` đọc `Llm:*`, tạo hàng đợi, `EnqueueMissingGreyJobs`, và `RunLlmDispatchLoopAsync` (`:175–177`).
→ Đây là lý do Core cần tách khỏi `Llm`, và kế hoạch cần mục 4.2 (`Llm:Enabled`).

### 4.7 Điểm không phải vấn đề (đã kiểm tra, để khỏi tốn công)
- `Path.DirectorySeparatorChar` / `Path.GetRelativePath` / `TrimEndingDirectorySeparator` được dùng đúng cách (không nối chuỗi bằng `\`).
- Không có registry, không có `Environment.SpecialFolder`/`ProgramData`, không có `icacls`/`cmd` trong `DlpService/`.
- `Encoding.GetEncoding(1258)` (`TextFileReader.cs:33`) nhờ CodePages provider nên chạy được.
- `DateTime.Now` trong `FileLogger.cs:35` phụ thuộc múi giờ máy — ổn, nhưng journald dùng UTC; nếu so log hai nơi nhớ đổi.
- Tên file trong test: không có đường dẫn `C:\`/`D:\` hay `IsWindows` trong `DlpService.Tests/` (chỉ có comment). Test Tika thật chỉ chạy khi có biến `DLP_TIKA_DLL` — trên Linux sẽ tự bỏ qua, chấp nhận được.

---

## 5. Bản đồ interface cần trừu tượng hóa (rút ra từ code)

| Interface đề xuất | Rút từ | Hành vi hợp đồng Core cần | Windows (đã có) | Linux (cần viết) |
|---|---|---|---|---|
| `IFileChangeSource` | `FileWatcherService.StartWatching`, `OnFileChanged/Renamed/WatcherError` | Phát `Created/Changed/Renamed/Deleted` + sự kiện `Overflow` (cần quét bù) + `Failed` | `FileSystemWatcher` | `FileSystemWatcher` (inotify) bọc lại; **bổ sung `Deleted`** |
| `IWatchRootProvider` | `Worker.cs:148` | Trả danh sách thư mục cần theo dõi từ cấu hình, mở rộng mẫu | Trả `Watch:Folders` | Mở rộng `/home/*/Documents`…, theo dõi thêm khi có user mới |
| `IPathPolicy` | `EscalationPolicy.cs:170`, `IsSupported` | So sánh đường dẫn; loại trừ ẩn/tạm | IgnoreCase, chặn `~$` | Ordinal, chặn `.swp`/`~`/`.part`… |
| `IDataFolderGuard` | `DataFolderSecurity.Restrict`, `ResultStore.RestrictFolder` | "Chỉ tài khoản dịch vụ + admin truy cập thư mục này" | ACL | `chmod 0700` + kiểm tra chủ sở hữu |
| `IServiceLifetime` / `ILogSink` | `Program.cs:18, 28–42` | Đăng ký vòng đời; thêm kênh log hệ thống | SCM + EventLog | systemd (+ journald qua stdout) |
| `IAppLayout` *(đề xuất thêm)* | `Worker.cs:31, 67, 121`, `Program.cs:47` | Cho biết thư mục rule / data / log / cấu hình | Cạnh file chạy | `/opt`, `/etc`, `/var/lib`, `/var/log` |
| `INativeLibraryLocator` | `TikaNativeOptions.DllPath` | Tên + đường dẫn thư viện native | `.dll` | `.so` |
| `IResourceLimiter` | `MachineIdleChecker` (chỉ nửa phía LLM) | Giới hạn tài nguyên của chính tiến trình | (không có / Job Object) | cgroup do systemd lo → có thể là **không cần interface** ở bản Linux |

Đánh giá: danh sách 8 interface trong kế hoạch (mục 5.2) **phù hợp**, cần thêm `IAppLayout` (hiện chưa có) và cân nhắc bỏ `IResourceLimiter`/`IFileStabilityChecker` ở giai đoạn đầu (chưa có code Windows nào để trừu tượng hóa; xem 7).

---

## 6. IPC: giữ contract, bỏ transport (cho bản Linux)

- Hiện trạng: `contracts/inference.proto` (`contract_ver = 1`) sinh **chỉ client** (`DlpService.csproj:28`, `GrpcServices="Client"`); transport là named pipe qua `SocketsHttpHandler.ConnectCallback` (`InferenceClient.cs:38–66`).
- Cách thay transport đúng kỹ thuật (nếu sau này có LLM trên Linux): cùng `ConnectCallback`, dùng `UnixDomainSocketEndPoint` + `Socket`. Contract, `GuardedInferenceClient`, `CircuitBreaker` giữ nguyên. `PipeServerVerifier` thay bằng `SO_PEERCRED` (lấy PID/UID phía server) — việc này không đụng contract.
- **Quyết định hiện tại: không làm.** Bản Linux không kết nối LLM nên:
  - không đưa `Grpc.Net.Client`, `Grpc.Tools`, `Google.Protobuf` vào host Linux (giảm kích thước và bề mặt tấn công; giúp `PrivateNetwork=yes` hợp lý hơn);
  - giữ nguyên `inference.proto` và code client trong thư mục/project của Windows để không mất công sức;
  - ghi rõ trong tài liệu: *"transport UDS: đã nghiên cứu, hoãn"*.

---

## 7. Đề xuất ranh giới Core / Adapter

```
Core (không biết OS, không biết LLM)
  Scanning/  Classification/  Reading/  Text/  Storage/ (3 bảng chính)
  Pipeline xử lý 1 file  ← tách từ FileWatcherService.ProcessFile
  Debounce + giới hạn đồng thời (SemaphoreSlim)
  Mask() để log      ← tách từ FileWatcherService.Mask

Adapter theo OS (thin)
  Windows: SCM, EventLog, ACL, FileSystemWatcher bọc, vị trí cạnh .exe
  Linux  : systemd, chmod/chown, FileSystemWatcher bọc (+Deleted), vị trí /opt|/etc|/var

Chỉ Windows
  Llm/ (InferenceClient, PipeServerVerifier, MachineIdleChecker, ChunkSelector, Masker, dispatcher…)
  Grpc.* và System.Diagnostics.PerformanceCounter
```

Thứ tự tách ít rủi ro nhất (mỗi bước đều build + test + so kết quả quét Windows trước/sau):
1. Chuyển cấu hình `\\` → `/`; thêm `Watch:Folders`; thay hard-code `Worker.cs:148` (bản Windows vẫn chạy như cũ).
2. Thêm `IAppLayout` (3 thư mục rule/data/log). Bản Windows trả về "cạnh .exe" như cũ.
3. Thêm công tắc `Llm:Enabled` (4.6).
4. Tách `ProcessFile` + `Mask` khỏi `FileWatcherService` (pipeline thuần, test được không cần watcher).
5. Bọc `FileSystemWatcher` thành `IFileChangeSource`; thêm `Deleted`.
6. Rút `Llm/` + `Grpc` + `PerformanceCounter` sang project Windows.
7. Mới viết adapter Linux.

---

## 8. Rủi ro và điểm chưa chắc

| # | Rủi ro / điều chưa xác minh | Cách kiểm chứng |
|---|---|---|
| 1 | `FileSystemWatcher` trên Linux: `InternalBufferSize` có bị bỏ qua không; `Error` có bắn khi `IN_Q_OVERFLOW` không | Tạo burst 10.000 file trên ext4, xem log `[FileWatcher] Buffer tràn` |
| 2 | `Directory.EnumerateFiles(..., AllDirectories)` (`FileWatcherService.cs:146`) có đi vào thư mục ẩn / symlink vòng trên Linux không (mặc định thay đổi theo overload) | Thử trên cây có `.git`, symlink vòng; nếu có thì phải truyền `EnumerationOptions` rõ ràng |
| 3 | `ResultStore` mở WAL trên hệ thống file lạ (NFS, overlay) | Đã có `VerifyPragmas` cảnh báo nếu không bật được WAL (`ResultStore.cs:336`) — chỉ cần xem log |
| 4 | Hai file khác nhau chỉ khác hoa/thường trên Linux (`A.txt`, `a.txt`) | Test hợp đồng cho `IPathPolicy` |
| 5 | `file_hash`, `finding`, `rules_ver` Windows ≠ Linux do chuẩn hoá Unicode / xuống dòng (`\r\n` vs `\n`) khi *đọc cùng file* | Chạy `tools/eval/DlpBaseline` đối chiếu từng file (tiêu chí GĐ2). Nếu lệch, nghi `Normalizer` và `TextFileReader` đầu tiên |
| 6 | Bản .NET: lõi hiện `net10.0` (`DlpService.csproj:4`); máy Ubuntu 16.04/CentOS 7 cần net6 (D9) | Kế hoạch đã nêu — GĐ0 phải thử build `net6.0` cho các gói `PdfPig 0.1.11`, `OpenXml 3.5.1`, `ExcelDataReader` |
| 7 | `Lock` (`private readonly Lock _writeLock`, `ResultStore.cs:76`) là kiểu của **.NET 9+** | Sẽ **không biên dịch** trên net6 → phải đổi sang `object` + `lock` nếu đa-target net6. Đây là ví dụ của rủi ro 6; nên rà thêm các API .NET 9/10 khác |
| 8 | Kiến trúc tách project làm đổi `InternalsVisibleTo` (`DlpService.csproj:41`) và các `tools/*` tham chiếu `DlpService` | Rà `tools/` trước khi đổi tên project |

---

## 9. Việc tiếp theo đề xuất (chờ bạn duyệt trước khi sửa code)

1. Xác nhận 3 điều chỉnh ở mục 2.2 (bỏ ChunkSelector/Masker/llm_job khỏi bản Linux; giữ `Mask()` ghi log).
2. Chọn việc làm đầu tiên — đề xuất **bước 1 của mục 7** (cấu hình + `Watch:Folders`): nhỏ, an toàn, có lợi cho cả Windows, và là chặn triển khai lớn nhất.
3. Nếu bạn đồng ý, mình sẽ gửi **Change Brief 4 ý** (thay đổi gì / ý tưởng / tác động vào đâu / tác động thế nào) cho bước đó, rồi mới có code.
