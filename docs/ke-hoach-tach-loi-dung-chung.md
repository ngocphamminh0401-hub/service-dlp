# Kế hoạch tách lõi dùng chung khỏi phần phụ thuộc Windows

> Nhánh: `feature/shared-core` · Ngày lập: 06/11/2026 · Trạng thái: **CHỜ PHÊ DUYỆT** (chưa thực hiện bước nào trừ phần "đã làm" ở mục 1).
> Căn cứ: `docs/nghien-cuu-chuyen-doi-linux.md` (hiện trạng code) và `docs/ke-hoach-trien-khai-linux.md` (đích đa nền tảng, quyết định D1–D9).
> Phạm vi tài liệu này: **chỉ giai đoạn tách lõi** (GĐ1 của kế hoạch Linux). Chưa viết adapter Linux.

---

## 1. Mục tiêu và nguyên tắc

**Mục tiêu:** có một project `DlpService.Core` không chứa gì riêng Windows và không biết LLM, build + test được trên Linux; bản Windows hiện tại là một "host" mỏng bọc quanh Core và **cho kết quả quét giống hệt trước khi tách**.

**Đã làm trên nhánh:** `Watch:Folders` thay đường dẫn `D:` hard-code; `\\` → `/` trong cấu hình; `FileWatcherService` theo dõi nhiều thư mục. 113 test pass.

**Nguyên tắc (ràng buộc cứng):**
1. **Mỗi bước nhỏ, build + test xanh, rồi mới sang bước sau.** Không có bước "đập đi xây lại".
2. **Tách trong nhà trước, tách nhà sau.** Làm gọn ranh giới ngay trong project `DlpService` (bước 3–5), chỉ *di chuyển file sang project mới* khi ranh giới đã sạch (bước 6). Di chuyển file là thao tác cơ học, an toàn hơn nhiều khi code đã tách đúng.
3. **Giữ nguyên namespace** (`DlpService.Scanning`, `DlpService.Storage`…) khi chuyển file sang Core. Lý do: 12 project đang tham chiếu `DlpService` (11 tool + `DlpService.Tests`); giữ namespace thì **không phải sửa `using` ở đâu cả**.
4. **Giữ tên project `DlpService` cho host Windows.** `DlpService` sẽ `ProjectReference` tới Core; tham chiếu dự án mặc định có tính *bắc cầu* nên 12 project kia vẫn thấy các lớp của Core mà không phải sửa `.csproj`.
5. **Kết quả quét là hợp đồng.** Sau mỗi bước chạm vào luồng quét, so lại bằng `tools/eval/DlpBaseline` (từng file: nhãn, mức, `file_hash`, finding) — lệch là dừng.
6. **Core không biết module cụ thể và không `#if WINDOWS`** (CLAUDE.md mục 7.2). Chọn adapter ở host qua DI.

---

## 2. Hiện trạng → đích

```
HIỆN TRẠNG: 1 project "DlpService" chứa tất cả (Worker, Watching, Scanning, Storage, Llm, Program...)

ĐÍCH (sau GĐ này)
DlpService.Core        net10.0, KHÔNG phụ thuộc OS/LLM
  Scanning/ Classification/ Reading/ Text/ Storage/
  Pipeline/   ← ProcessFile + Mask (tách từ FileWatcherService)
  Platform/   ← các interface (IFileChangeSource, IPathPolicy, IDataFolderGuard, IAppLayout...)
DlpService             host Windows (tên giữ nguyên)
  Program.cs, Worker.cs (điều phối), adapter Windows (FSW, ACL, SCM/EventLog)
  Llm/, Grpc.*, PerformanceCounter, PipeServerVerifier  ← CHỈ ở đây
```

Các gói NuGet theo project:

| Project | Được dùng | Không được dùng |
|---|---|---|
| Core | OpenXml, ExcelDataReader, PdfPig, Microsoft.Data.Sqlite, `Microsoft.Extensions.*.Abstractions` | `Hosting.WindowsServices`, `Grpc.*`, `Google.Protobuf`, `System.Diagnostics.PerformanceCounter`, `System.Security.AccessControl` |
| DlpService (host Win) | Core + các gói Windows/LLM ở trên | — |

---

## 3. Các bước thực hiện

Mỗi bước: **việc – file chính – kiểm tra đạt – hoàn tác**. Ước lượng theo "buổi" như các buổi trước.

### Bước 0 — Đóng băng đường cơ sở *(0,5 buổi)*
- Chạy `DlpBaseline` trên bộ dữ liệu có sẵn, lưu kết quả làm chuẩn; ghi `git tag` điểm xuất phát.
- Sửa lỗi `DlpService.slnx` (dòng trùng `DlpResilience.csproj`) để build được cả solution — hiện chỉ build từng project.
- **Đạt:** `dotnet test` xanh, có file kết quả chuẩn, build solution không lỗi.

### Bước 1 — `IAppLayout`: gom thư mục rule / data / log / confidence *(1 buổi)*
- Việc: một nơi duy nhất trả lời "rule, DB, log, model nằm ở đâu". Bản Windows trả về đúng như cũ (cạnh file `.exe`).
- Chạm: `Worker.cs:31, 67–71, 121–125`, `Program.cs:47`.
- **Đạt:** Windows không đổi hành vi; không còn `AppContext.BaseDirectory` rải rác trong code lõi.
- **Hoàn tác:** revert 1 commit.

### Bước 2 — Công tắc `Llm:Enabled` *(1 buổi)*
- Việc: khi `false` → không dựng `LlmJobDispatcher`, không `EnqueueMissingGreyJobs`, vùng xám ghi `final + needs_review` (nhánh này đã có sẵn ở `FileWatcherService.cs:489–493`, chỉ cần ép `waitsForLlm=false`).
- Chạm: `Worker.cs` (khối LLM), `FileWatcherService.ProcessFile` (khối `Escalate`), có thể `EscalationPolicy`.
- **Đạt:** `Llm:Enabled=false` → 0 dòng `llm_job` mới; `DlpBaseline` ở chế độ này khớp với chế độ cũ *trừ* các file vùng xám (đổi từ `provisional` sang `final + needs_review` — phải **giải thích được từng file lệch**). `Llm:Enabled=true` (mặc định) → khớp 100%.

### Bước 3 — Tách pipeline xử lý 1 file ra khỏi `FileWatcherService` *(2 buổi — bước rủi ro nhất)*
- Hiện `FileWatcherService` (746 dòng) gồm 3 trách nhiệm: nguồn sự kiện OS, debounce/đồng thời, và `ProcessFile` (~215 dòng). Chỉ cái đầu phụ thuộc OS.
- Việc: tạo `Pipeline/FileProcessor` (tên tạm) nhận đường dẫn → trả `ProcessOutcome`; `FileWatcherService` chỉ còn lo sự kiện + debounce + gọi processor. Tách luôn `Mask()` (`:690`) thành lớp dùng chung — **bắt buộc** vì quy tắc không log dữ liệu nhạy cảm.
- Làm theo kiểu "di chuyển nguyên văn, không đổi logic": copy thân hàm sang lớp mới, đổi chỗ gọi, chưa tối ưu gì.
- **Đạt:** 113 test pass; `DlpBaseline` khớp 100%; log `[Perf]` không chậm hơn đáng kể (so với `docs/bao-cao-hieu-nang-service-dlp.md`).
- **Hoàn tác:** revert; vì chỉ di chuyển nên dễ.

### Bước 4 — Các interface nền tảng *(2 buổi)*
Chỉ tạo interface **khi đã có ít nhất 1 chỗ dùng thật** trong code hiện tại (tránh trừu tượng hóa suông):

| Interface | Thay cho | Adapter Windows (bọc code sẵn có) |
|---|---|---|
| `IFileChangeSource` | `FileSystemWatcher` trong `FileWatcherService` | `FileSystemWatcherSource` — phát `Created/Changed/Renamed/Deleted` + `Overflow` |
| `IPathPolicy` | `EscalationPolicy.cs:170`, `IsSupported` (`~$`), `PathComparison` mới thêm | IgnoreCase, chặn `~$` |
| `IDataFolderGuard` | `DataFolderSecurity.Restrict`, `ResultStore.RestrictFolder` | ACL hiện có |
| `IAppLayout` | bước 1 | cạnh `.exe` |
| `IServiceLifetime` / `ILogSink` | `Program.cs:18–42` | SCM + EventLog |

- **Hoãn** (chưa có code Windows để bọc): `IFileStabilityChecker`, `IResourceLimiter`, `INativeLibraryLocator` — làm khi viết adapter Linux.
- Kèm **test hợp đồng** cho từng interface (cùng 1 bộ test chạy với mọi adapter, sau này chạy cả adapter Linux).
- `ResultStore` nhận `IDataFolderGuard` qua constructor (hiện gọi thẳng `DataFolderSecurity`).
- **Đạt:** Core code không còn gọi trực tiếp `FileSystemWatcher`, `DirectorySecurity`, `AddWindowsService`; test hợp đồng xanh với adapter Windows.

### Bước 5 — Dọn ranh giới trong project *(1 buổi)*
- Chuyển `Worker.cs` thành lớp điều phối mỏng: nạp cấu hình → dựng Core → chạy; khối LLM tách thành `LlmCoordinator` trong thư mục `Llm/` (host Windows).
- `Diagnostics/ExplainCommand` chỉ cần Storage → để Core (Linux cần lệnh `explain`).
- Rà `DlpService.csproj`: gom `Grpc.*`, `PerformanceCounter`, `WindowsServices` vào một nhóm riêng, chuẩn bị chuyển.
- **Đạt:** thư mục `Scanning/ Classification/ Reading/ Text/ Storage/ Pipeline/ Platform/` **không có** `using` tới `DlpService.Llm` hay namespace Windows (kiểm bằng `grep`, ghi thành một test tự động ở bước 7).

### Bước 6 — Tạo `DlpService.Core` và di chuyển file *(2 buổi)*
- Tạo project mới, `DlpService` thêm `ProjectReference` tới Core.
- Di chuyển theo thứ tự **từ lá lên gốc**, mỗi lần 1 thư mục, build sau mỗi lần: `Text/` → `Scanning/` → `Classification/` → `Reading/` → `Storage/` → `Pipeline/` → `Platform/`.
- Các file nhúng đi theo Core: `rules/**/*.json`, `confidence/*.json` (hiện cấu hình copy ở `DlpService.csproj:34–36`) — **đã chốt: Core sở hữu** (xem câu hỏi 3).
- Chuyển `InternalsVisibleTo("DlpService.Tests")` sang Core (`DlpService.csproj:41`).
- **Giữ namespace** → 12 project còn lại không phải sửa. Nếu có project nào lỗi, đó là tín hiệu của một phụ thuộc ẩn cần xem lại.
- **Đạt:** build solution xanh; 113 test pass; `DlpBaseline` khớp 100%; 11 tool còn build được.

### Bước 7 — Chứng minh Core sạch + build trên Linux *(1–2 buổi)*
- Thêm test kiểm tra tự động: assembly Core **không tham chiếu** `System.Security.AccessControl`, `Grpc.*`, `System.Diagnostics.PerformanceCounter`, `Microsoft.Extensions.Hosting.WindowsServices`; không có P/Invoke tới `kernel32`/`advapi32`.
- Build + test `DlpService.Core` và `DlpService.Tests` trên Linux (WSL2/VM Ubuntu; để thư mục test trong `~/`, vì inotify không chạy trên `/mnt/d`).
- Ghi lại test fail do khác biệt hệ điều hành (kỳ vọng: những test liên quan đường dẫn `\`, khoá file, ACL) và xử lý.
- **Đạt:** `dotnet build` + `dotnet test` chạy được trên Linux; mọi fail còn lại đều được giải thích.
- *Chưa làm trong GĐ này:* host Linux, systemd, inotify adapter riêng, gói `.deb/.rpm` → thuộc GĐ2 trở đi của kế hoạch Linux.

**Tổng ước lượng: khoảng 10–12 buổi.**

---

## 4. Thứ tự và phụ thuộc giữa các bước

```
0 → 1 → 2 ─┐
           ├→ 3 → 4 → 5 → 6 → 7
(1,2 độc lập nhau; cả hai phải xong trước 3 để pipeline mới sạch LLM + đường dẫn)
```

Bước 3 là "cổ chai": làm hỏng ở đây thì mọi bước sau không tin được. Vì vậy 0–2 chuẩn bị đường cơ sở và làm sạch trước, và bước 3 chỉ được phép *di chuyển không đổi logic*.

---

## 5. Rủi ro và cách giảm

| Rủi ro | Mức | Giảm thiểu |
|---|---|---|
| Tách `ProcessFile` làm lệch kết quả quét | Cao | Di chuyển nguyên văn; `DlpBaseline` so từng file sau bước 2, 3, 6; giữ nhánh gốc để so |
| `ResultStore` là 1 `partial class` trải trên 4 file (`LlmJobs`, `LlmCache`, `Explain`) nên **không tách được qua hai assembly** | Trung bình | Giữ nguyên cả `ResultStore` trong Core; bảng `llm_*` vẫn tồn tại nhưng không dùng khi `Llm:Enabled=false`. Tách bảng LLM ra là việc sau (không chặn Linux) |
| 12 project tham chiếu `DlpService` bị vỡ khi di chuyển | Trung bình | Giữ namespace + giữ tên `DlpService` (nguyên tắc 3, 4); build solution sau mỗi thư mục ở bước 6 |
| Core vô tình kéo phụ thuộc Windows qua gói NuGet bắc cầu | Trung bình | Test tự động ở bước 7; kiểm `dotnet list package --include-transitive` |
| Trừu tượng hóa quá sớm → interface sai hình dạng | Trung bình | Chỉ tạo interface khi có chỗ dùng thật (bước 4); hoãn 3 interface chưa có code Windows |
| `Lock` (.NET 9+) và API mới khác không biên dịch trên net6 (máy Ubuntu 16.04/CentOS 7, D9) | Thấp lúc này | Chưa đa-target net6 ở GĐ này; ghi nhận để xử lý ở GĐ0/2 của kế hoạch Linux |
| Test hiệu năng/đồng thời bị ảnh hưởng khi thêm tầng interface | Thấp | Chạy lại `DlpConcurrencyBench`, `DlpDbBench` sau bước 3 và 6 |

---

## 6. Tiêu chí hoàn thành cả giai đoạn

1. `DlpService.Core` build + test được trên **cả Windows và Linux**.
2. Core không tham chiếu gói/namespace Windows hoặc LLM (có test tự động bảo vệ).
3. Bản Windows chạy như cũ; `DlpBaseline` khớp 100% (ngoại lệ chỉ là file vùng xám khi bật `Llm:Enabled=false`, đã giải thích từng file).
4. 113 test cũ vẫn pass; 11 tool vẫn build.
5. Có `docs/buoi-XX-tom-tat.md` cho mỗi buổi.

---

## 7. Câu hỏi cần bạn quyết định

> **Đã chốt (06/11/2026, theo đề xuất):** (1) interface để trong `Core/Platform/`, chưa tách `Abstractions`; (2) giữ tên project `DlpService` cho host Windows;
> (3) `rules/` + `confidence/` thuộc Core; (4) giữ bảng `llm_*` trong Core ở giai đoạn này; (5) bắt đầu từ bước 0 rồi bước 1;
> (6) copy `rules/` + `confidence/` ra thư mục chạy (nhúng vào `.dll` là tối ưu làm sau); (7) commit phần đã làm trước khi sang bước 0.
> Chi tiết các lựa chọn giữ nguyên bên dưới để tra cứu.

1. **Có tách `DlpService.Abstractions` riêng ngay không?** Kế hoạch Linux (D1) đề xuất tách. Đề xuất ở đây: **để interface trong `Core/Platform/` trước** (ít project hơn, dễ học hơn), tách ra khi macOS thật sự cần.
2. **Tên project Windows:** giữ `DlpService` (đề xuất — không phải sửa 12 chỗ) hay đổi sang `DlpService.Host.Windows` như D1 (sạch tên hơn nhưng phải sửa toàn bộ tham chiếu)?
3. ~~**Rule và file confidence (`rules/`, `confidence/`) đặt ở đâu?**~~ **ĐÃ CHỐT: thuộc Core.** `confidence/*.json` là hệ số của công thức, gắn chặt với code `FileConfidenceModel`; `rules/` là nội dung nghiệp vụ phải giống hệt giữa Windows và Linux (`rules_ver`, `file_hash` khớp 100%), để mỗi host giữ một bản sẽ lệch dần. Core là nguồn duy nhất và copy bản mặc định ra thư mục chạy; **vị trí lúc chạy do `IAppLayout` quyết định** — mặc định dùng bản đi kèm Core, host có thể trỏ sang nơi khác (Linux: `/etc/dlp-service/rules/`, gói `.deb/.rpm` tự đặt vào) mà không đổi code Core.
4. **Bảng `llm_*` trong `ResultStore`:** chấp nhận giữ trong Core ở GĐ này (đề xuất) hay muốn tách ngay (tốn thêm ~2 buổi, đụng schema)?
5. **Bắt đầu từ đâu?** Đề xuất bước 0 (đường cơ sở + sửa `.slnx`) rồi bước 1.
