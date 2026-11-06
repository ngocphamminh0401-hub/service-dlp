# Báo cáo thay đổi ngày 06/11/2026 — Bước 7 tách lõi + GĐ2 host Linux tối thiểu

Nhánh `feature/linux`, máy Ubuntu 18.04.6 (kernel 4.15, glibc 2.27, Python 3.6), .NET SDK 10.0.403.
Kế hoạch: `docs/ke-hoach-tach-loi-dung-chung.md` (Bước 7), `docs/ke-hoach-trien-khai-linux.md` (mục 7, GĐ2).
Tóm tắt buổi: `docs/buoi-17-tom-tat.md`.

## 1. Tổng quan

| # | Hạng mục | Trạng thái | Commit |
|---|---|---|---|
| 0 | Kéo nhánh `feature/linux` (Bước 0–6), cài .NET 10 SDK vào `~/.dotnet` | ✅ | — |
| 1 | Vá SQLite cho glibc 2.27: tự biên dịch `libe_sqlite3.so` + `Directory.Build.targets` | ✅ | `0c63f04` |
| 2 | Bước 7: test kiến trúc Core (`CoreDependencyTests`) | ✅ | `0c63f04` |
| 3 | Bước 7: vá mất sự kiện thư mục con trên Linux (`FileSystemWatcherSource`) | ✅ | `0c63f04` |
| 4 | GĐ2 2.1: khung host Linux `DlpService.Host.Linux` | ✅ | `5e69690` |
| 5 | GĐ2 2.2: adapter Linux (`DlpService.Platform.Linux`) — path policy, chmod, bố cục /etc /var, `AddLinuxPlatform()` | ✅ | commit cuối ngày |
| 6 | GĐ2 2.2e + 2.3: sửa lỗi tìm ra khi chạy thật (content root, log "đã khoá", tên lệnh explain) | ✅ | commit cuối ngày |
| 7 | GĐ2 R7: siết che giá trị trong log + script canary log | ✅ PASS | commit cuối ngày |
| 8 | GĐ2 R8: `Watch:Folders` nhận mẫu `/home/*/Documents` | ✅ | commit cuối ngày |
| 9 | GĐ2 R11: công tắc `FileConfidence:Enabled` (Linux tắt) | ✅ | commit cuối ngày |
| 10 | GĐ2 R12: đo tỉ lệ `needs_review` khi tắt LLM | ✅ 5,9 % | — (chỉ đo) |
| 11 | GĐ2 2.4: kiểm thử đối chiếu Linux ↔ Windows trên `rawdata` | ⚠️ **Chưa chạy được** — máy không có dữ liệu; script đã sẵn sàng | commit cuối ngày |

**Kết quả cuối ngày:** `dotnet build DlpService.slnx` **0 lỗi, 0 cảnh báo** (nay gồm cả project test);
`dotnet test` **189/189** (đầu ngày: 130/143). Host Linux chạy thật được ở cả 2 chế độ bố cục.

## 2. Môi trường

| Việc | Chi tiết |
|---|---|
| .NET | 10.0.403 vào `~/.dotnet` (không sudo). Ubuntu 18.04 không được hỗ trợ chính thức nhưng chạy được. 8.0.427 cũng cài (thử) — không cần, xoá được `~/.dotnet/sdk/8.0.427` |
| PATH | Chưa thêm `dotnet` vào PATH: thêm `export DOTNET_ROOT=$HOME/.dotnet` và `export PATH=$PATH:$HOME/.dotnet` vào `~/.bashrc` |
| SQLite | NuGet mang `libe_sqlite3.so` cần glibc 2.34 → tự biên dịch SQLite **3.53.3** (đúng bản, đúng 100 % compile option) vào `~/.local/lib/dlp-sqlite/`; `Directory.Build.targets` chép đè sau mỗi build (chỉ Linux + có file) |
| gcc | `sudo apt-get install gcc libc6-dev` (người dùng tự chạy ở terminal riêng — prompt `!` không có tty cho sudo) |

## 3. Bước 7 tách lõi (commit `0c63f04`)

- **Test kiến trúc** `DlpService.Tests/Architecture/CoreDependencyTests.cs`: Core không tham chiếu `System.Security.AccessControl`, `Grpc.*`,
  `System.Diagnostics.PerformanceCounter`, `Microsoft.Extensions.Hosting.WindowsServices`; không P/Invoke `kernel32`/`advapi32`. Đã thử ngược (chèn tạm DllImport → đỏ).
- **Mất sự kiện thư mục con trên Linux** (`DlpService.Core/Platform/FileSystemWatcherSource.cs`): inotify không đệ quy, khoảng hở < 50 ms;
  `mv` thư mục có sẵn file vào → mất **toàn bộ**. Vá: bỏ `Filters`, tự lọc đuôi, quét bù khi có thư mục mới / chuyển vào, báo lần lượt.
  So sánh 6 phương án: `docs/fsw-thu-muc-con-linux.md` (khuyến nghị lâu dài: A + quét đối soát định kỳ).

## 4. GĐ2 — host Linux tối thiểu

### 4.1 Cấu trúc mới

```
DlpService.Core/              (+ Watching/WatchFolderPatterns.cs)
DlpService.Platform.Linux/    MỚI — class library: LinuxPathPolicy, LinuxDataFolderGuard, LinuxAppLayout, LinuxHost
DlpService.Host.Linux/        MỚI — file chạy dlp-service: Program.cs + appsettings.json
DlpService/                   host Windows — KHÔNG đổi
DlpService.Tests/             + tham chiếu Platform.Linux; nay nằm trong DlpService.slnx
```

Adapter Linux để ở class library riêng (đúng mục 5.3 của kế hoạch) thay vì trong host: project test tham chiếu được mà không kéo theo
file chạy thứ hai (trùng `Program` / `appsettings.json` với host Windows).

### 4.2 Adapter Linux (2.2a–d)

| Interface | Bản tạm (2.1) | Bản Linux (2.2) |
|---|---|---|
| `IPathPolicy` | `CaseInsensitivePathPolicy` (sai trên Linux) | `LinuxPathPolicy`: `Ordinal`; file tạm `~$`, `.~lock.*`, `.goutputstream-*`, `*~`, `.swp/.swo/.swx`, `.kate-swp`, `.part`, `.crdownload`, `.tmp` |
| `IDataFolderGuard` | `NoOpDataFolderGuard` | `LinuxDataFolderGuard`: thư mục 0700, thư mục con 0700, file có sẵn 0600 (`File.SetUnixFileMode`, không P/Invoke); áp cho cả thư mục **log** |
| `IAppLayout` | `ExecutableAppLayout` | `LinuxAppLayout` 2 chế độ (bảng dưới) |

| | System (cài gói / systemd) | Portable (dev, chạy tay) |
|---|---|---|
| Rule | `/etc/dlp-service/rules` | `<thư mục chạy>/rules` |
| Cấu hình thêm | `/etc/dlp-service/appsettings.json` (thắng file đi kèm, **thua** biến môi trường + dòng lệnh) | — |
| DB | `/var/lib/dlp-service/dlp.db` | `<thư mục chạy>/data/dlp.db` |
| Log | `/var/log/dlp-service` | `<thư mục chạy>/logs` |
| Đường dẫn tương đối | tính theo `/var/lib/dlp-service` | tính theo thư mục chạy |
| Chọn khi | `DLP_LAYOUT=system`, hoặc file chạy nằm trong `/opt/dlp-service/` | mặc định khi chạy từ chỗ khác, hoặc `DLP_LAYOUT=portable` |

`DLP_SYSTEM_ROOT` đổi gốc "/" của chế độ System — chỉ để chạy thử không cần sudo. Chạy System thật mà thiếu quyền → lỗi rõ
("cần cài gói hoặc sudo; chạy thử thì đặt DLP_LAYOUT=portable"); `DLP_LAYOUT` sai → lỗi rõ.

`DlpService.Host.Linux/Program.cs` còn: content root = thư mục chạy, `AddLinuxPlatform()`, `Worker`. Không đăng ký vòng LLM.

### 4.3 Thay đổi trong Core (ảnh hưởng cả bản Windows)

| File | Thay đổi | Ảnh hưởng Windows |
|---|---|---|
| `Pipeline/LogMasking.cs` | **R7**: che chỉ giữ 2 ký tự cuối khi > 8 ký tự (cũ: 3 đầu + 3 cuối) | Có — log che mạnh hơn |
| `Pipeline/FileProcessor.cs` | **R7**: Information chỉ ghi tên rule × số lần; dòng từng chỗ khớp (vị trí + giá trị đã che) xuống **Debug** | Có — log Information gọn hơn |
| `Worker.cs` | **R8**: `Watch:Folders` nhận mẫu; **R11**: `FileConfidence:Enabled` (mặc định `true`) | Không (đường dẫn thường + mặc định như cũ) |
| `Watching/WatchFolderPatterns.cs` | Mới: mở rộng `*`/`?` từng cấp, bỏ thư mục không đọc được, cấm `**` | Không |
| `Diagnostics/ExplainCommand.cs` | Nhận bố cục + cấu hình từ host; tên lệnh theo file chạy thật | Không (tham số tuỳ chọn, mặc định như cũ) — trừ câu "Cách dùng" in `DlpService` thay vì `DlpService.exe` |
| `Storage/ResultStore.cs` | Câu log "Đã khoá quyền … ((không khoá))" → "Quyền thư mục dữ liệu …: …" | Có — chỉ chữ trong log |

### 4.4 Lỗi tìm ra khi chạy thật (2.3)

| Lỗi | Hậu quả | Sửa |
|---|---|---|
| **Content root = thư mục làm việc** | Chạy `dotnet /duong/dan/dlp-service.dll` từ chỗ khác — và **dưới systemd (thư mục làm việc "/")** — không đọc `appsettings.json` → mọi cấu hình về mặc định: LLM không bị tắt bằng cấu hình, FileConfidence bật, 6 file cùng lúc, ngưỡng 0,30/0,90 | `ContentRootPath = AppContext.BaseDirectory` (bên Windows `AddWindowsService` tự làm) |
| `explain` dùng cứng bố cục "cạnh file chạy" | Chế độ System tìm DB sai chỗ | Host truyền bố cục + cấu hình |
| Log "Đã khoá … ((không khoá))" | Log tự mâu thuẫn | Câu trung lập |
| `explain` in "DlpService.exe" | Sai tên trên Linux | `AppDomain.CurrentDomain.FriendlyName` |
| `snapshot-ket-qua.py` dùng `reconfigure` (Python 3.7+) | Không chạy trên Python 3.6 (Ubuntu 18.04) | Kiểm `hasattr` |

### 4.5 Kết quả chạy thật

Dữ liệu giả, cây "nhiều người dùng" `~/dlp-fakehome/{an,binh,chi}`, cấu hình `Watch:Folders = ~/dlp-fakehome/*/Documents`:

| Kiểm tra | Portable | System (gốc giả) |
|---|---|---|
| Mẫu `*/Documents` → 2 thư mục (`chi/Desktop` không theo dõi) | ✅ | ✅ |
| File có sẵn + file mới tạo khi đang chạy được quét | ✅ | ✅ |
| Đuôi viết hoa `DANH-SACH.TXT` được quét; `.~lock.ho-so.docx#` bị bỏ | ✅ | ✅ |
| Cấu hình đọc đúng khi chạy từ thư mục khác / từ "/" | ✅ (sau khi sửa content root) | ✅ |
| `LLM tắt`, `FileConfidence tắt`, `tối đa 2 file`, ngưỡng 0,10/0,80 | ✅ | ✅ |
| Thư mục dữ liệu + log quyền 0700 | ✅ | ✅ |
| Rule đọc từ `etc/dlp-service/rules`, DB ở `var/lib`, log ở `var/log`, `/etc/.../appsettings.json` ghi đè được | — | ✅ |
| `dlp-service explain <id>` mở đúng DB | ✅ | ✅ |
| Thiếu quyền / `DLP_LAYOUT` sai → lỗi rõ | — | ✅ |

### 4.6 R7 — canary trong log (`scripts/canary-log.py`)

Chạy host Linux thật (chế độ System, gốc tạm) trên file chứa 6 giá trị đánh dấu (CCCD, SĐT, email, secret, số thẻ, số tài khoản), ở mức log
**Information** và **Debug**; tìm trong file log, console, DB: giá trị nguyên văn, đoạn liên tiếp ≥ 6 ký tự, dạng "lộ quanh dấu che"
(≥ 2 ký tự đầu + `*`, hoặc `*` + ≥ 3 ký tự cuối).

| Code | Kết quả |
|---|---|
| **Mới** | ✅ **PASS** — 0 lộ ở cả 2 mức log |
| Cũ (thử ngược) | ❌ 24 FAIL (VD `*111` của số thẻ, `*123` của số tài khoản) |

Đường dẫn file vẫn có trong log (cần để vận hành) → thư mục log khoá 0700.

### 4.7 R12 — `needs_review` khi không có LLM

Đo trên ảnh kết quả Windows đã lưu khi **tắt LLM** (`docs/snapshot/baseline-2-llm-tat.tsv`, 320 file, ngưỡng 0,10/0,80 — giống Linux):

| Chỉ số | Giá trị |
|---|---|
| `needs_review` | **19 / 320 = 5,9 %** (cả 19 đều thuộc vùng xám 0,10 ≤ S1 < 0,80) |
| Phân vùng | ≥ 0,80: 209 · vùng xám: 19 · sạch (S1 = 0): 92 |
| Theo nhãn thật của thư mục | confidential 0/80 · internal 5/80 · public 8/80 · top_secret 6/80 |
| Theo lĩnh vực | hr 10/80 · insurance 5/80 · finance 2/80 · healthcare 2/80 |
| Nếu đổi `THigh` | 0,70 → 4,7 % · 0,80 → 5,9 % · 0,90 → 12,2 % |

Nhận xét: 5,9 % là chấp nhận được, **giữ nguyên ngưỡng**. Đáng xem thêm: 6/80 file `top_secret` rơi vùng xám (bị đánh giá thấp?) và
8/80 file `public` vào vùng xám (báo nhầm?) — cần xem từng file khi có dữ liệu.

### 4.8 2.4 — đối chiếu Linux ↔ Windows: CHƯA chạy được

Dữ liệu `rawdata` (`D:\dlp-classifier\rawdata`) không có trên máy Linux. Đã chuẩn bị:
- `scripts/snapshot-ket-qua.py` chạy được trên Linux (mặc định host Linux + `~/dlp-rawdata`, tương thích Python 3.6); đã chạy thử với 2 file
  `.docx` tự tạo → quét, ghi DB, xuất TSV đúng.
- Cách chạy khi có dữ liệu: chép thư mục `rawdata` sang `~/dlp-rawdata`, rồi
  ```
  python3 scripts/snapshot-ket-qua.py --out /tmp/linux.tsv
  git diff --no-index docs/snapshot/baseline-2-llm-tat.tsv /tmp/linux.tsv
  ```
  Kỳ vọng: giống 100 %; khác thì phải giải thích (VD cách PdfPig đọc font khác OS).

## 5. Danh sách file thay đổi trong ngày

| File | Loại | Commit |
|---|---|---|
| `Directory.Build.targets` | Mới | `0c63f04` |
| `DlpService.Core/Platform/FileSystemWatcherSource.cs` | Sửa | `0c63f04` |
| `DlpService.Tests/Architecture/CoreDependencyTests.cs` | Mới | `0c63f04` |
| `DlpService.Tests/Platform/FileChangeSourceContractTests.cs` | Sửa (+2 test) | `0c63f04` |
| `docs/fsw-thu-muc-con-linux.md`, `docs/buoi-17-tom-tat.md` | Mới | `0c63f04` (buoi-17 cập nhật thêm ở commit cuối) |
| `DlpService.Host.Linux/` (csproj, Program.cs, appsettings.json) | Mới | `5e69690`, sửa ở commit cuối |
| `DlpService.Platform.Linux/` (csproj + 4 file .cs) | Mới | cuối ngày |
| `DlpService.Core/Watching/WatchFolderPatterns.cs` | Mới | cuối ngày |
| `DlpService.Core/Worker.cs`, `Pipeline/LogMasking.cs`, `Pipeline/FileProcessor.cs`, `Diagnostics/ExplainCommand.cs`, `Storage/ResultStore.cs` | Sửa | cuối ngày |
| `DlpService.Tests/Platform/LinuxAppLayoutTests.cs`, `Watching/WatchFolderPatternsTests.cs` | Mới | cuối ngày |
| `DlpService.Tests/Platform/PathPolicyContractTests.cs`, `DataFolderGuardContractTests.cs`, `Pipeline/LogMaskingTests.cs`, `Reading/TikaNativeTests.cs`, `DlpService.Tests.csproj` | Sửa | cuối ngày |
| `DlpService.slnx` | + Host.Linux, Platform.Linux, Tests | `5e69690`, cuối ngày |
| `scripts/canary-log.py` | Mới | cuối ngày |
| `scripts/snapshot-ket-qua.py` | Sửa (Linux + Python 3.6) | cuối ngày |
| `docs/muc-luc-code.md`, `docs/bao-cao-thay-doi-2026-11-06.md` | Sửa / mới | cuối ngày |

Test: 143 → **189** (+46): kiến trúc 3, watcher 2, LinuxPathPolicy 18, LinuxDataFolderGuard 4, LinuxAppLayout/LinuxHost 12, WatchFolderPatterns 6,
LogMasking +1 (và đổi kỳ vọng 2 test cũ theo cách che mới).

## 6. Việc còn lại / cần quyết định

| # | Việc | Khi nào |
|---|---|---|
| 1 | **Chạy lại toàn bộ test trên Windows** — thay đổi Core: watcher (đuôi hoa/thường, `mv` thư mục), che log, câu log | Trước khi gộp vào `main` |
| 2 | **Chạy 2.4 đối chiếu** khi chép được `rawdata` sang máy Linux | Đóng GĐ2 |
| 3 | File DB/log tạo SAU lúc khoá có quyền theo umask (0644/0664) — thư mục cha 0700 vẫn chặn người khác; đặt `UMask=0077` trong unit systemd | GĐ4 |
| 4 | Người dùng tạo sau lúc khởi động chưa được thêm vào mẫu `/home/*/…`; bỏ thư mục ẩn; `max_user_watches` (máy này 8192) | GĐ3 |
| 5 | `ScheduleProcess` không an toàn đa luồng (có từ trước) → `AddOrUpdate` | Change Brief riêng |
| 6 | Quyết định D9 (net6 cho Ubuntu 16.04 / CentOS 7): Core đang dùng `Lock` (.NET 9+) — giữ D9 thì phải sửa | Cần quyết định |
| 7 | Rule họ tên bắt lan sang dòng sau (thấy khi chạy thử: "Họ tên…" nuốt cả "Số CCCD") — có thể cả bản Windows | Xem lại rule |
| 8 | 6/80 `top_secret` và 8/80 `public` rơi vùng xám (R12) | Khi có dữ liệu |
