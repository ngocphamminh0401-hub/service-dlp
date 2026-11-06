# Buổi 4 — Đăng ký DlpService thành Windows Service thật

Chuyển `DlpService` từ "console app chạy nền" (chỉ chạy khi có ai mở terminal gõ lệnh) thành 1 Windows
Service đúng nghĩa: đăng ký được vào Service Control Manager (SCM), tự khởi động cùng Windows, tự phục hồi
khi crash, ghi log được kể cả khi không có console đính kèm.

---

## 1. Bối cảnh: vì sao cần buổi này

Cuối buổi 3, pipeline phân loại (Worker → RuleLoader → RegexScanner → ClassificationEngine → FileWatcherService)
đã chạy đúng qua `dotnet run`, nhưng đó chỉ là 1 console app tạm — đóng terminal là service chết, không tự
chạy lại khi máy khởi động, không ai quản lý khi nó gặp lỗi. Buổi 4 giải quyết đúng khoảng cách đó, theo
checklist 5 nhóm (A-E) đã lập cuối buổi 3. Buổi này hoàn thành **Nhóm A**, **Nhóm B**, và mục **D5** của
Nhóm D (phát hiện qua lỗi thực tế khi test, không phải làm theo kế hoạch ban đầu).

---

## 2. Nhóm A — Đăng ký thành Windows Service

### A1. Cho app nhận biết được SCM

**Thay đổi gì:** Thêm package `Microsoft.Extensions.Hosting.WindowsServices`; `Program.cs` gọi
`builder.Services.AddWindowsService(options => options.ServiceName = "DlpService")`.

**Lý do:** `Host.CreateApplicationBuilder` mặc định không hiểu giao thức riêng mà Service Control Manager
(SCM) dùng để gửi tín hiệu start/stop/pause. `AddWindowsService()` là lớp "phiên dịch" chính thức của
Microsoft — chỉ phát huy tác dụng khi app THẬT SỰ được SCM khởi động; chạy tay qua `dotnet run` không bị
ảnh hưởng gì.

**Tác động vào đâu:** `DlpService.csproj` (thêm `PackageReference`), `Program.cs` (1 dòng gọi).

### A2. Publish thành file thực thi cố định

**Thay đổi gì:** `dotnet publish -c Release -r win-x64 --self-contained false -o D:\Service-DLP\publish`.

**Lý do:** SCM cần 1 đường dẫn `.exe` CỐ ĐỊNH để gọi mỗi lần khởi động — khác `dotnet run` (biên dịch lại
mỗi lần, đường dẫn tạm trong `bin\Debug\...`). File `.exe` này là **framework-dependent** (cần máy đích có
sẵn .NET 10 Runtime).

**Tác động vào đâu:** Không sửa code — chỉ tạo thư mục output mới `publish/` (chứa `DlpService.exe` +
toàn bộ `rules/**/*.json` nhờ cấu hình `CopyToOutputDirectory` đã có sẵn từ trước).

### A3. Script cài đặt + cấu hình tự phục hồi khi crash

**Thay đổi gì:** 2 file mới `scripts/install-service.ps1` (chạy `sc.exe create` đăng ký service, `start= auto`
để tự khởi động cùng Windows, cộng `sc.exe failure` để tự restart 3 lần cách nhau 5 giây khi crash) và
`scripts/uninstall-service.ps1` (dừng + `sc.exe delete`).

**Lý do:** Viết thành script thay vì gõ tay lệnh `sc.exe` mỗi lần — tránh gõ sai đường dẫn khi cần cài lại
nhiều lần trong quá trình test. `sc.exe failure` quan trọng vì không có nó, service crash 1 lần là nằm im
mãi mãi, không ai biết.

**Tác động vào đâu:** File mới trong `scripts/`, không sửa code C#. Có 1 lỗi phát sinh khi chạy thực tế:
file `.ps1` ban đầu lưu UTF-8 KHÔNG có BOM (byte-order-mark) — Windows PowerShell 5.1 đọc nhầm sang bảng mã
ANSI, làm hỏng các dòng có tiếng Việt (lỗi `Missing closing '}'`/`terminator`). Đã sửa bằng cách ghi lại 2
file với BOM UTF-8.

**Kết quả thực tế:** Đã chạy `install-service.ps1` thành công (`CreateService SUCCESS`), `sc.exe start
DlpService` → `Get-Service` báo `Running`, PID ổn định (không crash-loop).

---

## 3. Nhóm B — Logging khi không có console

### Phát hiện: Console.WriteLine "biến mất" khi chạy dưới SCM

Đúng như dự đoán ở checklist buổi 3: khi service chạy dưới SCM (không có console đính kèm), mọi
`Console.WriteLine` trong `FileWatcherService.cs` không có nơi để hiển thị. Kiểm tra Event Viewer sau khi
service chạy chỉ thấy 2 dòng **"Service started/stopped successfully"** — do `WindowsServiceLifetime` (bên
trong `AddWindowsService()`) tự ghi thẳng vào Event Log bằng `System.Diagnostics.EventLog`, KHÔNG đi qua
`ILogger` — không chứng minh được `ILogger` đã có đường ra.

### Lần thử 1 — `AddEventLog()` mặc định (thất bại)

**Thay đổi:** `Program.cs` thêm `builder.Logging.AddEventLog()` (bọc `OperatingSystem.IsWindows()` để hết
cảnh báo CA1416 — `AddEventLog` chỉ chạy được trên Windows).

**Kết quả:** Sau khi publish + restart service + thả file test, Event Log **vẫn chỉ có 2 dòng start/stop**
— không có dòng nào từ `Worker.cs` (kể cả nhịp tim "Worker running at..." chạy mỗi giây, log Information,
không có lý do gì bị lọc).

### Lần thử 2 — chỉ định `SourceName` (vẫn thất bại)

**Giả thuyết:** `AddEventLog()` không chỉ định `SourceName` sẽ dùng mặc định `.NET Runtime` — nguồn này có
thể có vấn đề đăng ký trên máy test. Sửa thành `settings.SourceName = "DlpService"` (nguồn đã CHỨNG MINH
ghi được, dùng chung với `WindowsServiceLifetime`).

**Kiểm tra:** Dùng `Get-WinEvent -FilterHashtable @{LogName='Application'} -MaxEvents 50` quét TOÀN BỘ log
30 phút gần nhất (không lọc theo Source) — xác nhận **không có bất kỳ entry nào** từ `ILogger`, dưới bất kỳ
tên nguồn nào. Kết luận: `EventLogLoggerProvider` trên máy này ghi lỗi mà tự nuốt hoàn toàn — đây là thiết
kế có chủ đích của Microsoft (1 lỗi ghi log không được phép làm crash app), nhưng hệ quả là không có cách
nào chẩn đoán lý do thật từ xa.

### Giải pháp cuối — tự viết File Logger (thành công)

**Thay đổi gì:** 2 file mới `Logging/FileLogger.cs` + `Logging/FileLoggerProvider.cs`, tự implement
`ILogger`/`ILoggerProvider` chuẩn của .NET, ghi mỗi dòng log (kèm timestamp/level/category) vào file
`logs/dlpservice-YYYY-MM-DD.log` (đặt cạnh `.exe`, tự đổi file theo ngày). Đăng ký qua
`builder.Logging.AddProvider(new FileLoggerProvider(...))` trong `Program.cs`.

**Lý do quyết định đổi hướng:** Sau 2 lần sửa Event Log đều thất bại và không có cách chẩn đoán (thư viện
nuốt lỗi im lặng), tiếp tục dò theo hướng đó sẽ tốn nhiều vòng dừng/publish/start mà chưa chắc ra kết quả.
File logger đơn giản hơn — không có tầng trung gian nào có thể "nuốt" lỗi, mở bằng Notepad xem trực tiếp
được, đúng phong cách tự viết nhỏ gọn đã dùng xuyên suốt dự án (không thêm thư viện ngoài như Serilog/NLog).

**Tác động vào đâu:**
- `Logging/FileLogger.cs`, `Logging/FileLoggerProvider.cs` (file mới, namespace `DlpService.Logging`).
- `Program.cs` — thêm 1 dòng đăng ký provider.
- Không sửa `Worker.cs`/`FileWatcherService.cs` — chúng gọi `ILogger` y hệt cũ, không cần biết log đi đâu.

**Chi tiết kỹ thuật đáng chú ý:** `FileLogger` dùng 1 `object writeLock` DÙNG CHUNG cho mọi category logger
(được `FileLoggerProvider` truyền vào khi tạo) — vì `FileWatcherService` có thể xử lý nhiều file cùng lúc
qua debounce (nhiều `Task` chạy song song), nếu không khoá, các dòng log ghi cùng lúc vào CÙNG 1 file sẽ bị
chèn/mất chữ.

**Verify trước khi bắt người dùng lặp lại chu trình stop/publish/start:** Chạy `dotnet run` cục bộ trước,
xác nhận file nhận đủ log (nạp 63 rule, danh sách 5 module, nhịp tim) — mới publish thật và yêu cầu restart
service. Kết quả: log phân loại xuất hiện đầy đủ (`[DLP] ...: phát hiện N chỗ nhạy cảm`, mức độ, lý do từng
domain) trong file, xác nhận bởi chính người dùng qua `Get-Content ... -Tail 30`.

---

## 4. Nhóm D (một phần) — Bắt sự kiện `Renamed`

### Phát hiện lỗi qua test thực tế

Người dùng tạo file `test2.txt` trong `watch-folder` qua VS Code — **không có log nào xuất hiện**, kể cả
"sạch" hay lỗi đọc file. Chẩn đoán: service vẫn chạy bình thường (nhịp tim đều), nghĩa là sự kiện của
`FileSystemWatcher` cho file này **chưa từng được xử lý**.

**Nguyên nhân:** `FileWatcherService.StartWatching()` trước đó chỉ đăng ký `watcher.Created` và
`watcher.Changed`. Nhiều trình soạn thảo (VS Code khi tạo file mới) ghi ra 1 file tạm rồi **đổi tên**
(rename) thành tên thật — thao tác này chỉ phát sự kiện `Renamed`, KHÔNG phát `Created` lẫn `Changed`. Đây
đúng là mục D5 đã liệt kê trong checklist cuối buổi 3 ("Xử lý `Renamed` event") — giờ có bằng chứng thực tế
tái hiện đúng lỗi đó.

**Thay đổi gì:** `Watching/FileWatcherService.cs` — thêm `watcher.Renamed += OnFileRenamed;`; tách phần
logic debounce (huỷ timer cũ, tạo timer mới, gọi `ProcessAfterDebounceAsync`) ra hàm dùng chung
`ScheduleProcess(filePath)`, để cả `OnFileChanged` và `OnFileRenamed` (chữ ký sự kiện khác nhau —
`FileSystemEventArgs` vs `RenamedEventArgs`) đều gọi lại đúng 1 logic, không chép lặp code.

**Kết quả verify:** Tạo `test3.txt` qua VS Code sau khi publish lại — log bắt được đầy đủ (`sạch` rồi
`phát hiện 1 chỗ nhạy cảm` khi nội dung PhoneVN được ghi vào).

### Phát hiện phụ (chưa sửa): 1 lần lưu file → nhiều lần quét/log trùng

Log thực tế cho thấy `test3.txt` bị quét **4 lần** trong 6 giây cho 1 lần thao tác "tạo + gõ nội dung + lưu"
qua VS Code (`sạch` → `sạch` → `phát hiện 1 chỗ` → `phát hiện 1 chỗ` — 2 lần cuối kết quả HỆT NHAU). Đây là
hành vi đã biết của `FileSystemWatcher`: 1 thao tác lưu ở tầng ứng dụng thường được OS chia thành nhiều sự
kiện rời rạc (ghi nội dung, cập nhật thời gian sửa đổi/thuộc tính riêng...), mỗi sự kiện cách nhau ĐỦ XA
(hơn 300ms) nên bộ debounce hiện tại (chỉ gộp sự kiện gần nhau) không gộp được. Xem mục 6 — đây là việc ưu
tiên tiếp theo.

---

## 5. Bảng data flow — file đã thay đổi trong buổi 4

| File | Thay đổi | Lý do |
|---|---|---|
| `DlpService.csproj` | Thêm `PackageReference Microsoft.Extensions.Hosting.WindowsServices` | Cần API `AddWindowsService()` |
| `Program.cs` | Thêm `AddWindowsService()`, `AddEventLog()` (giữ lại dù không hoạt động trên máy test — vô hại, có thể hữu ích trên máy khác), `AddProvider(new FileLoggerProvider(...))` | Đăng ký SCM + 2 kênh log (Event Log dự phòng, File Logger chính) |
| `Logging/FileLogger.cs` | **File mới** — implement `ILogger`, ghi 1 dòng/lần gọi log vào file theo ngày, có khoá dùng chung | Kênh log đáng tin cậy duy nhất hoạt động được trên máy test |
| `Logging/FileLoggerProvider.cs` | **File mới** — implement `ILoggerProvider`, tạo `FileLogger` theo category, tự tạo thư mục `logs/` | Cầu nối giữa `ILoggingBuilder` và `FileLogger` |
| `Watching/FileWatcherService.cs` | Constructor nhận thêm `ILogger`; mọi `Console.WriteLine` → `_logger.Log...`; thêm `watcher.Renamed` + `OnFileRenamed`; tách `ScheduleProcess()` dùng chung | Log sống được khi không có console; bắt đúng file tạo theo kiểu rename |
| `Worker.cs` | Truyền `logger` vào `new FileWatcherService(scanner, engine, logger)` | Khớp constructor mới |
| `scripts/install-service.ps1` | **File mới** — `sc.exe create` + `sc.exe description` + `sc.exe failure` | Đăng ký service + tự phục hồi khi crash |
| `scripts/uninstall-service.ps1` | **File mới** — dừng + `sc.exe delete` | Gỡ service khi cần cài lại |

---

## 6. Đề xuất cải thiện tiếp theo — hướng dẫn Nhóm D và E

### Nhóm D — còn lại (độ tin cậy giám sát thư mục)

| # | Việc | Vì sao | Gợi ý cách làm |
|---|---|---|---|
| **D-mới** (ưu tiên cao nhất, phát hiện trong buổi này) | **Bỏ qua quét trùng khi nội dung không đổi** | 1 lần lưu file qua VS Code hiện tạo ra 2-4 dòng log giống hệt nhau (xem mục 4) — gây nhiễu log, tốn tài nguyên quét lại vô ích | Thêm `Dictionary<string, string> _lastContentHash` trong `FileWatcherService` (khoá = filePath, giá trị = hash SHA256/MD5 của content lần quét gần nhất). Trong `ProcessAfterDebounceAsync`, sau khi đọc file, tính hash — nếu TRÙNG hash đã lưu cho path đó thì bỏ qua (không log, không classify), chỉ cập nhật khi hash khác. Cẩn thận: dict này sẽ phình to nếu file bị xoá — nên dọn entry khi có sự kiện `Deleted` (xem D2 dưới) |
| D1 | Quét TOÀN BỘ file có sẵn khi service khởi động | `FileSystemWatcher` chỉ bắt sự kiện XẢY RA SAU khi bắt đầu theo dõi — file có sẵn trong `watch-folder` TRƯỚC khi service start (ví dụ máy vừa reboot, service tự khởi động, nhưng file nhạy cảm đã nằm đó từ trước) không bao giờ được quét | Trong `Worker.cs`, trước khi gọi `fileWatcher.StartWatching(...)`, thêm vòng lặp `Directory.GetFiles(folderPath, "*.txt")` rồi gọi lại đúng logic xử lý 1 file (nên refactor `ProcessAfterDebounceAsync` để tách phần "đọc+quét+log" ra khỏi phần debounce, dùng lại được cho cả 2 luồng: sự kiện watcher và quét khởi động) |
| D2 | Bật `IncludeSubdirectories = true` | Hiện tại watcher chỉ theo dõi ĐÚNG 1 cấp `watch-folder`, bỏ sót toàn bộ thư mục con | Thêm dòng `IncludeSubdirectories = true` vào object initializer của `FileSystemWatcher` trong `StartWatching()`. Lưu ý: nên cân nhắc cùng lúc xử lý sự kiện `Deleted` (dọn `_pendingTimers`/hash cache khi file bị xoá) vì thư mục con dễ có thao tác xoá/di chuyển hàng loạt hơn |
| D3 | Bắt `watcher.Error` (buffer tràn) | `FileSystemWatcher` có buffer nội bộ giới hạn (mặc định 8KB, ~vài chục sự kiện) — thư mục có nhiều file đổi dồn dập (ví dụ đồng bộ hàng trăm file cùng lúc) sẽ làm watcher "câm" mà không báo lỗi nếu không xử lý sự kiện `Error` | Đăng ký `watcher.Error += OnError;`, trong đó ít nhất log cảnh báo (`_logger.LogWarning`) và có thể tăng `watcher.InternalBufferSize` (tối đa khuyến nghị ~64KB, tăng quá sẽ tốn memory kernel) |
| D4 | Bọc try/catch rộng quanh `ProcessAfterDebounceAsync` | Hiện chỉ bắt `TaskCanceledException` — 1 lỗi bất ngờ (file bị khoá bởi chương trình khác, encoding lạ, hết dung lượng đĩa khi ghi log...) có thể làm `Task` lỗi âm thầm (unobserved exception), không thấy trong log | Thêm `catch (Exception ex) { _logger.LogError(ex, "Lỗi xử lý file {filePath}", filePath); }` sau khối `catch(TaskCanceledException)` hiện có |

**Thứ tự khuyến nghị trong Nhóm D:** làm "quét trùng" (mục ưu tiên cao nhất) trước tiên vì vừa rẻ vừa giải
quyết đúng vấn đề vừa gặp; D4 (try/catch rộng) nên làm cùng lúc vì rất rẻ và là lưới an toàn cho mọi thay
đổi khác trong nhóm; D1/D2 nên làm chung 1 đợt vì cùng đụng vào `StartWatching()`; D3 có thể để sau cùng vì
chỉ thật sự cần thiết khi thư mục theo dõi có khối lượng file lớn (chưa đo được ở quy mô hiện tại).

### Nhóm E — Bảo mật & vận hành (làm sau khi Nhóm D ổn định)

| # | Việc | Vì sao | Gợi ý cách làm |
|---|---|---|---|
| E1 | Chọn tài khoản chạy service, không dùng `LocalSystem` mặc định | Service hiện cài bằng `sc.exe create` không chỉ định `obj=`, mặc định chạy dưới `LocalSystem` — tài khoản có TOÀN QUYỀN trên máy (đọc/ghi mọi file, mọi registry, mọi service khác). Nếu code có lỗ hổng (ví dụ regex ReDoS làm treo, hay sau này đọc thêm định dạng file phức tạp như `.docx`/`.pdf` có lỗi parse), rủi ro bị khai thác cao hơn nhiều so với 1 tài khoản chỉ có quyền đọc đúng thư mục cần giám sát | Tạo Local Service Account hoặc Group Managed Service Account (gMSA) chỉ cấp quyền Đọc trên các thư mục cần giám sát + quyền Ghi trên thư mục `logs/`; cài lại bằng `sc.exe config DlpService obj= ".\TênTàiKhoản" password= "..."` (hoặc sửa `install-service.ps1` thêm tham số) |
| E2 | Lưu kết quả phân loại vào nơi bền vững, truy vấn được | File `.log` dạng text hiện tại đủ để XEM TỪNG DÒNG nhưng khó trả lời câu hỏi tổng hợp kiểu "tuần này có bao nhiêu file SECRET", "file nào bị phân loại RESTRICTED trong 30 ngày qua" | Cân nhắc: (a) đơn giản nhất — ghi thêm 1 dòng CSV riêng (`audit.csv`: timestamp, filepath, level, domains) song song với log text hiện có, dễ mở bằng Excel; (b) nếu cần truy vấn phức tạp hơn — SQLite (file DB đơn, không cần cài server riêng, hợp với quy mô 1 service nhỏ) |

**Lưu ý khi làm Nhóm E:** khác Nhóm D (thuần code, test được ngay qua `dotnet run`/service test), E1 đụng
tới cấu hình tài khoản Windows — nên làm cẩn thận, xác nhận từng bước, và LUÔN giữ được đường lùi (biết cách
đổi lại `obj= LocalSystem` nếu tài khoản mới thiếu quyền làm service không khởi động được).

---

## 7. Việc chưa làm (nhắc lại từ Bậc 3 - buổi 3, vẫn còn nguyên)

Các mục Bậc 3 (nâng độ chính xác phân loại: `escalation`, `proximity_window`, `min_confidence_bypass`,
`volume_escalation`, `placeholder_exclusion`, `doc_context_discount_keywords`, confidence scoring, tiền lọc
Aho-Corasick) đã liệt kê chi tiết trong `docs/buoi-03-tom-tat.md`, mục 4 — buổi 4 không đụng vào phần này,
vẫn giữ nguyên độ ưu tiên đã đề xuất trước đó.
