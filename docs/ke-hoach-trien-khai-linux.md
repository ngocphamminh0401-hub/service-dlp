# Kế hoạch triển khai DlpService trên Linux

> Ngày lập: 03/11/2026 · Cập nhật: 04/11/2026 (bỏ LLM, mục tiêu là máy trạm của người dùng; **thêm theo góp ý rà soát:** phạm vi Ubuntu 16+/CentOS 7+, CPU 2%/30%, kiến trúc đa nền tảng Windows/Linux/macOS, đánh giá inotify, sơ đồ luồng)
> Trạng thái: **CHỜ PHÊ DUYỆT** — chưa viết dòng code nào cho Linux.
> Căn cứ: `docs/luong-va-thiet-ke-ky-thuat.md` (luồng + thiết kế bản Windows hiện tại) và việc rà mã nguồn `DlpService/`.

---

## 1. Tóm tắt cho người duyệt

- **Mục tiêu:** một service chạy nền trên **máy trạm Linux của người dùng**. Service theo dõi các thư mục làm việc
  (Documents, Desktop, Downloads…), trích text, quét regex + validator, phân loại mức/domain, chấm S1 và ghi kết quả vào
  SQLite **ngay trên máy đó**.
- **Không có LLM:** chỉ chạy Pass 1 (regex). File vùng xám được chốt `final` + `needs_review`, đúng như cách bản Windows
  đang làm khi không gửi được LLM. Không tạo hàng đợi `llm_job`.
- **Phân phối hỗ trợ tối thiểu:** **Ubuntu 16.04+ và CentOS 7+** (x86-64). Hai bản này quá cũ cho .NET 10 nên bản Linux phải build thêm
  target **.NET 6** và viết unit systemd theo mức systemd 219 (quyết định D9, mục 3).
- **Cách làm:** **dùng chung lõi C#** với bản Windows, và **cải tổ thành bộ interface theo khả năng (không theo hệ điều hành)**
  để Windows, Linux và sau này **macOS** cùng dùng một lõi (mục 5). Phần gắn với hệ điều hành chỉ nằm trong các adapter (mục 4).
- **Mức dùng tài nguyên:** nền **< 2% CPU** khi không có thay đổi; lúc quét đầu/quét bù **tối đa 30%** CPU (mục 3, D6).
- **Ưu tiên riêng của máy người dùng:** không làm chậm máy, người dùng thường không tắt được, bảo vệ dữ liệu của nhiều
  người dùng trên cùng máy, cài/cập nhật bằng gói `.deb` / `.rpm`.
- **Không làm trong đợt này:** chặn dữ liệu, thông báo cho người dùng, gom kết quả về máy chủ trung tâm, USB/ổ rời, **bản macOS** (chỉ đảm bảo thiết kế không chặn macOS).
- **Ước lượng thô:** 6 giai đoạn, khoảng **14–20 buổi** (tăng do thêm net6, `.rpm`, tách interface đa nền tảng và đo inotify).

**Người duyệt cần chốt 9 quyết định ở mục 3 và trả lời câu hỏi mở số 1 ở mục 10** (câu hỏi đó quyết định kết quả quét có
dùng được trong thực tế hay không).

---

## 2. Phạm vi

| Trong phạm vi | Ngoài phạm vi (đợt sau) |
|---|---|
| **Ubuntu 16.04+ và CentOS 7+** (gồm Ubuntu Desktop 22.04/24.04), x86-64. Tối thiểu để chạy: glibc 2.17, systemd 219 | Kiểm thử chính thức Fedora/RHEL 8+/Debian (dự kiến chạy được), ARM64 |
| Kiến trúc lõi dùng chung Windows/Linux, **thiết kế sẵn chỗ cho macOS** (mục 5) | Bản macOS (adapter FSEvents/launchd) |
| Thư mục làm việc của **mọi người dùng** trên máy (khai báo bằng mẫu, VD `/home/*/Documents`) | Toàn bộ hệ thống file, `/media` (USB, ổ rời), ổ mạng |
| Đủ định dạng như bản Windows: txt, csv, docx, pptx, xlsx, xls, pdf, eml | OCR cho PDF scan |
| Chỉ regex (Pass 1) | LLM thẩm định (Pass 2) |
| System service dưới **systemd**, có hardening, ưu tiên thấp | Chặn copy/gửi file, cảnh báo trên màn hình |
| Gói **`.deb`** (Ubuntu) và **`.rpm`** (CentOS) — cài, cập nhật, gỡ | Kho apt riêng, công cụ phân phối hàng loạt |
| Tika native bản `.so` (**tuỳ chọn**, mặc định tắt) | Gom kết quả về máy chủ, dashboard |

---

## 3. Các quyết định cần phê duyệt

| # | Câu hỏi | Đề xuất | Phương án khác | Lý do chọn |
|---|---|---|---|---|
| **D1** | Cách tổ chức mã nguồn | **Tách `DlpService.Core` + `DlpService.Abstractions` (bộ interface theo khả năng) dùng chung; mỗi hệ điều hành một project adapter `Platform.Windows` / `Platform.Linux` / `Platform.MacOS` (macOS để khung rỗng) và host mỏng** (chi tiết mục 5) | (b) Copy sang project riêng · (c) Viết lại · (d) Interface đặt tên theo hệ điều hành | Sửa lỗi một chỗ, cả 3 nền tảng cùng hưởng; macOS sau này chỉ viết thêm adapter, không đụng Core |
| **D2** | Bản phân phối mục tiêu | **Ubuntu 16.04+ và CentOS 7+** (tối thiểu); kiểm thử bắt buộc: Ubuntu 16.04, CentOS 7, Ubuntu 24.04 | Chỉ Ubuntu 22.04/24.04 | Yêu cầu phạm vi từ rà soát; Ubuntu 22.04/24.04 vẫn nằm trong dải này |
| **D3** | Cơ chế theo dõi file | **inotify** (qua `FileSystemWatcher` của .NET), chỉ trong các thư mục khai báo | fanotify (cả hệ thống) | Đủ cho thư mục làm việc; fanotify để dành cho giai đoạn chặn dữ liệu |
| **D4** | Kiểu service | **System service** (một bản cho cả máy, chạy từ lúc bật máy) | User service (`systemctl --user`, mỗi người dùng một bản) | User service chỉ đọc được file của chính người đó và **người dùng tự tắt được**, chỉ chạy khi đăng nhập. System service thì người dùng không có sudo không tắt được |
| **D5** | Tài khoản chạy + quyền đọc file của người dùng | **User hệ thống `dlp` + đúng 1 capability `CAP_DAC_READ_SEARCH`** (đọc được mọi file, không ghi, không chạy lệnh với quyền root) | (b) root · (c) `dlp` + cấp ACL vào từng thư mục home | (b): lỗ hổng ở parser docx/pdf → chiếm root. (c): phải sửa quyền thư mục của người dùng, dễ sót khi có người dùng mới. Đổi lại, (a) vẫn đọc được mọi file, nên phải kèm hardening ở mục 5 |
| **D6** | Mức ảnh hưởng tới máy | **Hai mức:** *nền* **< 2% CPU** trung bình khi không có file thay đổi; *burst* (quét đầu/quét bù) **trần 30%** (`CPUQuota=30%`). Kèm `Nice=10`, IO `idle`, `MemoryMax=512M` (systemd 219: `MemoryLimit=`), mặc định 2 file cùng lúc (Windows đang là 6) | Giữ 50% như bản nháp đầu · Không giới hạn | Máy người dùng: không được làm giật máy. **Vì sao 30% / 2%:** bản nháp đầu chọn 50% theo cảm tính và đã bị nhận xét là quá lớn. 30% ≈ 0,3 nhân — đủ để quét xong vài chục nghìn file trong vài giờ nhưng vẫn chừa ≥ 70% một nhân (và toàn bộ các nhân còn lại) cho người dùng; kết hợp `Nice`/IO idle thì khi máy bận, service tự nhường nên thực tế thấp hơn trần. Mức nền < 2% là mục tiêu: inotify chạy bằng sự kiện, không quét vòng lặp. **Đây là con số đề xuất, chưa đo** — chốt lại bằng số đo ở GĐ3/GĐ6 (mục 8) |
| **D7** | Đóng gói và cập nhật | **Gói `.deb` (Ubuntu) + `.rpm` (CentOS)** chứa bản self-contained; script cài tạo user `dlp`, bật service; cập nhật rule = cập nhật gói | Tarball + `install.sh` | Máy người dùng cần cài/cập nhật/gỡ chuẩn, cài một lệnh, phân phối được bằng công cụ có sẵn |
| **D8** | Tika native trên Linux | **Để giai đoạn 5, tuỳ chọn** | Làm ngay | Phải build lại `.so` bằng GraalVM; 8 định dạng chính đã có bộ đọc .NET |
| **D9** | Runtime cho bản phân phối cũ (Ubuntu 16.04, CentOS 7) | **`DlpService.Core` đa-target `net10.0` + `net6.0`**; bản Linux cho máy cũ build self-contained bằng net6 (CentOS 7 có glibc 2.17, systemd 219); máy mới dùng net10. Unit systemd viết theo mức tối thiểu 219 (mục 5.4) | (b) Chỉ net10, đổi yêu cầu thành Ubuntu 20.04+/RHEL 8+ · (c) Viết lõi bằng ngôn ngữ khác | .NET 10 không hỗ trợ hai bản này; net6 là bản cuối còn chạy được CentOS 7. **Rủi ro:** net6 đã hết hỗ trợ chính thức (không còn vá bảo mật) → chấp nhận có thời hạn, phải kiểm tra PdfPig/OpenXml/ExcelDataReader/SQLite native chạy được trên net6 (GĐ0/GĐ2) và xác nhận trên VM thật |

---

## 4. Những điểm phải chuyển từ Windows sang Linux

### 4.1 Phụ thuộc nền tảng (kết quả rà mã nguồn)

| Thành phần | Bản Windows hiện tại | Trên Linux | Nằm ở |
|---|---|---|---|
| Vòng đời service | `AddWindowsService` + SCM | `AddSystemd` (`Type=notify`) + unit file; trên CentOS 7 (systemd 219) dùng `Type=simple` nếu `notify` không ổn định | `Program.cs` → `IServiceLifetime` |
| Log | EventLog + FileLogger | journald + FileLogger (Ubuntu 16/CentOS 7 vẫn có journald nhưng mặc định không lưu bền → **FileLogger là kênh chính**) | `Program.cs` → `ILogSink` |
| Bảo vệ thư mục dữ liệu | ACL Windows (`DataFolderSecurity`) | chủ sở hữu `dlp`, quyền `0700`, `UMask=0077`; kiểm tra quyền lúc khởi động | `Storage/DataFolderSecurity.cs`, `ResultStore.cs:312` |
| Thư mục theo dõi | hard-code `D:\Service-DLP\watch-folder` | `Watch:Folders` trong cấu hình, có mẫu `*` cho từng người dùng | `Worker.cs:148` |
| Tika native | `tika_native.dll` | `libtika_native.so` | `Reading/Native/*` |

### 4.2 Tắt LLM trong lõi

Hiện chưa có công tắc tắt LLM: lõi vẫn tạo `llm_job` cho file vùng xám và để chúng ở trạng thái `provisional` chờ LLM.
Cần thêm **`Llm:Enabled=false`** để:
- `EscalationPolicy` không xếp nhóm P1/P2/P3 (không tạo job, không tốn hạn ngạch);
- file vùng xám ghi `final` + `needs_review` (giữ nguyên điểm regex);
- `Worker` không dựng `LlmJobDispatcher`, không gọi `EnqueueMissingGreyJobs`.

Bản Windows cũng dùng được công tắc này (đúng với mục "chưa có `Llm.Mode=off`" trong `tai-lieu-ky-thuat.md`).

### 4.3 Khác biệt hành vi cần xử lý trong lõi

| Khác biệt | Hậu quả nếu bỏ qua | Cách xử lý |
|---|---|---|
| Đường dẫn **phân biệt hoa thường** | `ExcludedPaths` đang so `OrdinalIgnoreCase` → loại trừ nhầm | So sánh theo nền tảng (Linux: `Ordinal`) |
| **Không có khoá file bắt buộc** | Đọc phải file đang ghi dở → kết luận sai | So size/mtime trước và sau khi đọc; khác nhau thì quét lại |
| inotify báo **mỗi lần ghi** | Một file lớn tạo hàng trăm sự kiện | Giữ debounce 300 ms, đo lại và chỉnh nếu cần (mục 8.1) |
| Kernel cũ (Ubuntu 16.04: 4.4, CentOS 7: 3.10) có `max_user_watches` mặc định thấp (thường 8.192) | Hết watch rất sớm với thư mục home vừa | Đọc `/proc/sys/fs/inotify/*` lúc khởi động; thiếu thì cảnh báo + hướng dẫn nâng bằng `sysctl` (gói cài đặt có thể ghi `/etc/sysctl.d/`); vẫn thiếu thì quét định kỳ cho phần không gắn được watch |
| inotify **tốn 1 watch cho mỗi thư mục con** | Thư mục home có hàng chục nghìn thư mục con (`.cache`, `node_modules`, `.git`) → hết watch | Mặc định **bỏ qua thư mục ẩn** và danh sách loại trừ (`node_modules`, `.git`, `.cache`…); lúc khởi động đếm số thư mục và so với `max_user_watches`, thiếu thì cảnh báo rõ |
| Hàng đợi sự kiện có giới hạn (`max_queued_events`) | Giải nén/copy hàng loạt → tràn | Tràn → quét bù (đã có), quét bù chạy song song (R5) |
| Người dùng mới, thư mục mới tạo sau khi service chạy | Mẫu `/home/*/Documents` không bao gồm người dùng mới | Theo dõi luôn `/home` ở mức 1 cấp; có thư mục home mới thì gắn watcher cho thư mục đó |
| File tạm của LibreOffice/vim/trình duyệt (`.~lock.*#`, `*.swp`, `*~`, `*.part`, `*.crdownload`) | Quét rác; quét file tải về dở | Mở rộng bộ lọc hiện chỉ chặn `~$` |
| Máy ngủ (suspend) / tắt nắp laptop | Cần chắc không mất sự kiện khi máy thức dậy | Kiểm thử ở GĐ6; nếu mất thì quét bù khi nhận tín hiệu thức dậy |
| **WSL2 khi dev:** inotify không chạy trên `/mnt/c`, `/mnt/d` | Test thấy "không có sự kiện" | Để thư mục test trong ext4 (`~/`), hoặc dùng VM Ubuntu Desktop |

---

## 5. Kiến trúc đích — đa nền tảng Windows / Linux / macOS

### 5.1 Cải tổ hướng thiết kế interface

Hướng "interface để tách phần phụ thuộc hệ điều hành" là đúng và được giữ. Điểm cải tổ: **interface đặt tên theo khả năng
(capability), không theo hệ điều hành** — không có `IWindowsX` / `ILinuxX`. Nhờ vậy Windows, Linux và macOS cùng cắm vào một
bộ interface; macOS sau này dùng lại lõi, chỉ viết thêm adapter.

Quy tắc:
- Core **không** dùng `#if WINDOWS`, `RuntimeInformation`, hay tham chiếu API riêng của hệ điều hành. Việc chọn adapter nằm ở host (đăng ký DI lúc khởi động).
- Mỗi interface có **hợp đồng hành vi** (đầu vào, đầu ra, lỗi) và một **bộ test hợp đồng** chạy cho mọi adapter → 3 hệ điều hành cư xử giống nhau ở mức Core nhìn thấy.
- Interface nằm trong project riêng `DlpService.Abstractions` (không phụ thuộc gì) để adapter hoặc module khác tham chiếu mà không kéo cả Core.

### 5.2 Bộ interface và adapter theo hệ điều hành

| Interface | Việc phải làm | Windows | Linux | macOS (làm sau) |
|---|---|---|---|---|
| `IFileChangeSource` | phát sự kiện tạo/sửa/xoá/đổi tên + sự kiện "tràn, cần quét bù" | `ReadDirectoryChangesW` (FSW) | inotify | FSEvents |
| `IWatchRootProvider` | mở rộng thư mục theo dõi (mẫu `*`, người dùng mới) | `C:\Users\*` | `/home/*` | `/Users/*` |
| `IPathPolicy` | so sánh đường dẫn hoa/thường, thư mục ẩn/loại trừ, file tạm | IgnoreCase | Ordinal | thường IgnoreCase (APFS) |
| `IFileStabilityChecker` | file đang ghi dở có đọc được chưa | thử mở độc quyền | so size/mtime trước/sau | như Linux |
| `IDataFolderGuard` | bảo vệ thư mục DB/log | ACL | owner `dlp` + `0700` | owner + `0700` |
| `IServiceLifetime` / `ILogSink` | vòng đời service và log hệ thống | SCM + EventLog | systemd + journald/FileLogger | launchd + `os_log` |
| `INativeLibraryLocator` | tìm thư viện native (Tika, SQLite) | `.dll` | `.so` | `.dylib` |
| `IResourceLimiter` | giới hạn CPU/RAM/IO của chính tiến trình | Job Object | cgroup qua systemd | `Nice` / `LowPriorityIO` của launchd |

Ánh xạ từ mã nguồn hiện tại: `DataFolderSecurity` → `IDataFolderGuard`; thư mục hard-code ở `Worker.cs:148` → `IWatchRootProvider`;
so sánh `OrdinalIgnoreCase` của `ExcludedPaths` → `IPathPolicy`; `Reading/Native/*` → `INativeLibraryLocator`;
`AddWindowsService`/EventLog ở `Program.cs` → `IServiceLifetime`/`ILogSink`; `FileSystemWatcher` trong `Watching/` → `IFileChangeSource`.

### 5.3 Cấu trúc solution

```
DlpService.sln
├── DlpService.Abstractions/      (interface ở 5.2, không phụ thuộc gì)
├── DlpService.Core/              (net10.0 + net6.0 — D9; KHÔNG phụ thuộc nền tảng)
│     Scanning/  Classification/  Reading/  Storage/  Watching/(logic xử lý 1 file)  Text/  Llm/(chỉ host Windows dùng)
├── DlpService.Platform.Windows/  adapter: FSW, ACL, SCM/EventLog, Job Object
├── DlpService.Platform.Linux/    adapter: inotify, chmod, systemd, mở rộng mẫu /home/*/...
├── DlpService.Platform.MacOS/    CHỈ KHUNG RỖNG (biên dịch được, TODO) — chứng minh thiết kế không chặn macOS
├── DlpService.Host.Windows/      host mỏng: đăng ký adapter Windows + vòng điều phối LLM
├── DlpService.Host.Linux/        host mỏng: đăng ký adapter Linux, không LLM
├── DlpService.Tests/             test Core + test hợp đồng cho mọi adapter (CI: windows-latest + ubuntu-latest [+ macos-latest sau])
└── tools/                        giữ nguyên, trỏ sang Core
```

Core chỉ biết interface, đúng quy tắc "Core không được biết module cụ thể" trong CLAUDE.md. Sơ đồ quan hệ ở Phụ lục A.4.

**Tiêu chí đạt của phần kiến trúc:** Core build được mà không tham chiếu project nền tảng nào; test hợp đồng xanh cho Windows và Linux;
khung `Platform.MacOS` vẫn biên dịch được.

### 5.4 Triển khai trên máy Linux

**Luồng trên máy người dùng**

```
Bật máy → systemd khởi động dlp-service (user dlp, ưu tiên thấp)
   → nạp rule, mở /var/lib/dlp-service/dlp.db
   → mở rộng Watch:Folders (VD /home/*/Documents, /home/*/Desktop, /home/*/Downloads)
   → bật inotify, quét các file đã có (bỏ qua file không đổi nhờ cache) — chạy chậm, không giành máy
Người dùng lưu file → debounce 300 ms → đọc → so size/mtime trước/sau → quét regex → phân loại → S1
   → Certain: final · Grey: final + needs_review · Sạch: final Public
   → ghi SQLite. Người dùng không thấy gì.
Admin (có sudo) → sudo dlp-service explain <file_id>, hoặc truy vấn DB
```

**Bố cục trên máy**

| Đường dẫn | Nội dung | Chủ sở hữu / quyền |
|---|---|---|
| `/opt/dlp-service/` | file chạy (self-contained) | root, `0755` |
| `/etc/dlp-service/appsettings.json`, `/etc/dlp-service/rules/` | cấu hình + rule | root:dlp, `0640` |
| `/var/lib/dlp-service/dlp.db` | kết quả quét — **chứa đường dẫn file của mọi người dùng** | dlp, `0700` |
| `/var/log/dlp-service/` | file log | dlp, `0700` |
| `/lib/systemd/system/dlp-service.service` | unit file (cài bởi gói `.deb`/`.rpm`; CentOS: `/usr/lib/systemd/system/`) | root |

**Hardening của systemd unit** (dự kiến, chốt ở GĐ4). Vì phải chạy được trên **systemd 219 (CentOS 7)** và 229 (Ubuntu 16.04),
mỗi thiết lập có cột phiên bản tối thiểu và phương án thay thế; gói cài đặt chọn unit phù hợp theo `systemctl --version`.

| Nhóm | Thiết lập (máy mới) | Phiên bản tối thiểu | Thay thế trên máy cũ (219/229) | Mục đích |
|---|---|---|---|---|
| Quyền | `User=dlp`, `NoNewPrivileges=yes`, `CapabilityBoundingSet=CAP_DAC_READ_SEARCH` | 187 / 187 / 187 | dùng được ở 219 | Chỉ có đúng một quyền |
| Cấp quyền cho non-root | `AmbientCapabilities=CAP_DAC_READ_SEARCH` | **229** | CentOS 7 (219): không có → chạy tiến trình bằng `User=dlp` kèm `setcap cap_dac_read_search+ep` trên file chạy, hoặc khởi động bằng root rồi tự bỏ quyền thừa (drop) trong chương trình | Đọc được file người dùng (D5) |
| File system | `ProtectSystem=strict`, `ProtectHome=read-only`, `ReadWritePaths=…`, `PrivateTmp=yes` | `strict` **232** | 219: `ProtectSystem=full` + `ReadOnlyDirectories=/home` + `ReadWriteDirectories=/var/lib/dlp-service /var/log/dlp-service` | Không ghi được ngoài DB và log |
| Mạng | `PrivateNetwork=yes`, `RestrictAddressFamilies=AF_UNIX` | 202 / 211 | dùng được ở 219 | Không LLM → không cần mạng |
| Tài nguyên | `Nice=10`, `IOSchedulingClass=idle`, `CPUQuota=30%`, `MemoryMax=512M`, `CPUWeight=20` | `CPUQuota` 213; `MemoryMax` 231; `CPUWeight` 231 | 219: `CPUQuota=30%` dùng được; `MemoryLimit=512M`; `CPUShares=` thay `CPUWeight` | Không làm chậm máy (D6): burst ≤ 30%, nền < 2% |
| Khởi động lại | `Restart=on-failure`, `RestartSec=5` | 188 | dùng được | Như `sc failure` bản Windows |

Lưu ý: mức "đạt" của `systemd-analyze security` (GĐ4) chỉ áp dụng cho máy có systemd ≥ 240; máy cũ kiểm bằng danh sách thiết lập trên.

`PrivateNetwork=yes` là lợi ích lớn nhất của việc bỏ LLM: một service đọc được mọi file nhưng không có mạng.

---

## 6. Các rủi ro có thể xem xét


| Mã | Vấn đề | Mức trên Linux | Xử lý ở GĐ |
|---|---|---|---|
| R3 | Service chạy với quyền cao nhất | Cao | 4 |
| R7 | Log lộ một phần giá trị nhạy cảm và đường dẫn file | Cao | 2 |
| R4 | Không xử lý xoá / di chuyển / đổi tên file | Cao | 3 |
| R6 | Watcher lỗi thì ngừng theo dõi mà không ai biết | Cao | 3 |
| R13 | Kết quả chỉ nằm trong SQLite, không ai xem | Cao | Ngoài đợt này |
| R5 | Quét lúc khởi động chạy tuần tự từng file | Trung bình | 3 |
| R8 | Thư mục theo dõi bị hard-code | Trung bình (chặn triển khai) | 2 |
| R14 | Mã gắn thẳng vào Windows nên khó mang sang Linux/macOS | Cao | 1 (tách interface, mục 5) |
| R9 | Đổi rule không quét lại file cũ | Trung bình | Cần chọn phương án |
| R12 | Vùng xám rộng → nhiều file `needs_review` | Trung bình | 2 |
| R11 | Hai hệ chấm điểm chạy song song | Thấp | 2 |
| R1, R2, R10 | Các vấn đề của tầng LLM | Không áp dụng | — |

---

## 7. Lộ trình theo giai đoạn

| GĐ | Nội dung | Sản phẩm bàn giao | Tiêu chí đạt (phải đạt mới sang GĐ sau) | Ước lượng |
|---|---|---|---|---|
| **0** | Duyệt kế hoạch; dựng **3 VM: Ubuntu 16.04, CentOS 7, Ubuntu 24.04**; cài .NET 10 SDK (máy mới) + .NET 6 (máy cũ); thử build Core với `net6.0` và chạy thư viện bên thứ ba trên net6 (D9) | Môi trường build được | `dotnet build` + `dotnet test` chạy được trên Linux; ghi lại test fail; **kết luận khả thi net6** (PdfPig, OpenXml, ExcelDataReader, SQLite) trước khi sang GĐ1 | 1–2 buổi |
| **1** | Tách `DlpService.Abstractions` + `DlpService.Core` (đa-target net10/net6) theo mục 5 (interface theo khả năng); thêm `Llm:Enabled` (mục 4.2); bản Windows chuyển sang `Platform.Windows` + `Host.Windows`; dựng khung rỗng `Platform.MacOS`; viết test hợp đồng cho từng interface | Solution mới, bản Windows chạy như cũ | Mọi test cũ đạt; **kết quả quét giống hệt** trước khi tách (`tools/eval/DlpBaseline`, so từng file); `Llm:Enabled=false` → 0 job, vùng xám = final + needs_review; Core không tham chiếu project nền tảng; khung macOS biên dịch được | 3–4 buổi |
| **2** | Host Linux tối thiểu: `AddSystemd`, cấu hình `/etc`, `Watch:Folders` có mẫu (R8), quét một lượt, ghi SQLite; sửa log (R7); không đóng gói model FileConfidence (R11); đo lại ngưỡng vùng xám khi không có LLM (R12) | `Host.Linux` + `Platform.Linux` chạy tay được | **Kiểm thử đối chiếu:** cùng tập dữ liệu → nhãn, mức, `file_hash`, finding giống bản Windows 100%, chỗ nào lệch phải giải thích được; canary không lộ trong log; có báo cáo tỉ lệ `needs_review` | 2–3 buổi |
| **3** | Watcher inotify cho thư mục home: sự kiện, debounce, quét bù, xoá/di chuyển (R4), tự dựng lại (R6), quét khởi động song song (R5), bỏ thư mục ẩn/loại trừ, người dùng mới, kiểm tra `max_user_watches`, so size/mtime trước/sau khi đọc, lọc file tạm; **thêm metric inotify (mục 8.1) và đo hiệu năng** | Watcher ổn định + báo cáo hiệu năng inotify | Test: tạo/sửa/đổi tên/xoá/di chuyển/ghi dở/tải file về/giải nén 10.000 file/xoá rồi tạo lại Documents/thêm người dùng mới → không sót file, không quét trùng; **đạt các ngưỡng ở mục 8.1** | 3–5 buổi |
| **4** | Gói **`.deb` + `.rpm`**: tạo user `dlp`, unit systemd có hardening + giới hạn tài nguyên (bản 219 và bản mới, mục 5.4), logrotate, `explain` qua `sudo`; gỡ gói xoá sạch (tuỳ chọn giữ DB) | `dlp-service_x.y.z_amd64.deb`, `dlp-service-x.y.z.x86_64.rpm` | `apt install` / `yum install` / nâng cấp / gỡ / `purge` đều đúng; `systemd-analyze security` đạt "OK" (máy systemd ≥ 240) hoặc đủ danh sách thiết lập ở mục 5.4 (máy cũ); người dùng thường không dừng được service; `kill -9` → tự khởi động lại, DB không hỏng | 3–4 buổi |
| **5** | *(Tuỳ chọn)* build `libtika_native.so`, ghim SHA-256, bật chế độ Supplement | Hỗ trợ doc/ppt/rtf | Qua bộ test an toàn extractor (`tools/safety`) như bản Windows | 1–2 buổi |
| **6** | Kiểm thử trên máy thật **Ubuntu 16.04, CentOS 7, Ubuntu 24.04** (+22.04 nếu có): ảnh hưởng tới người dùng (CPU nền < 2%, burst ≤ 30%, RAM, pin), chịu lỗi, canary, nhiều người dùng, suspend/resume; chốt lại con số CPU bằng số đo | Báo cáo đánh giá | Mục 8 đạt hết trên cả 3 bản phân phối | 2–3 buổi |

Mỗi buổi kết thúc bằng `docs/buoi-XX-tom-tat.md` như quy định hiện tại.

---

## 8. Kiểm thử và tiêu chí nghiệm thu

| Nhóm | Cách đo | Ngưỡng đạt (đề xuất, chốt khi duyệt) |
|---|---|---|
| Đúng | Kiểm thử đối chiếu Windows ↔ Linux trên `rawdata/` + corpus `tools/tika-bench` | Giống 100%, chỗ lệch phải giải thích được |
| Không làm chậm máy | Đo khi quét lần đầu một home ~50.000 file trong lúc người dùng làm việc bình thường, và đo riêng lúc rảnh (không file đổi) | **Nền (rảnh): CPU trung bình < 2%.** **Burst (quét đầu/bù): ≤ 30% một nhân** (khớp `CPUQuota=30%`, D6), RAM ≤ 300 MB, không giật khi gõ phím/cuộn trang |
| Pin | Laptop chạy pin, đo mức tiêu thụ thêm khi rảnh | Gần như 0 khi không có file thay đổi |
| Không sót file | Kịch bản ở GĐ3 + suspend/resume | 0 file bị sót |
| Bảo mật | `systemd-analyze security`; người dùng A không đọc được DB/log; service không mở được kết nối mạng | Đạt cả 3 |
| Không lộ dữ liệu | `DlpCanaryTest`: giá trị đánh dấu không xuất hiện trong journald, file log, DB | 0 lần lộ |
| Chịu lỗi | `tools/resilience` chuyển sang Linux (`kill -9` giữa lúc quét) | DB không hỏng, quét tiếp đúng |
| Đa nền tảng | Test hợp đồng cho từng interface (mục 5.2) chạy với adapter Windows và Linux; khung macOS biên dịch | Đạt cả 2 + biên dịch được |
| inotify | Mục 8.1 | Đạt các ngưỡng ở 8.1 |
| CI | `dotnet test` trên `windows-latest` + `ubuntu-latest` (target net10 và net6) | Đạt cả 2 |

### 8.1 Theo dõi và đánh giá hiệu năng giám sát bằng inotify

Mục đích: biết inotify có đủ nhanh, đủ nhẹ và không sót sự kiện trên máy trạm thật, đặc biệt ở kernel cũ (Ubuntu 16.04, CentOS 7).
Làm ở GĐ3 (dựng số đo) và GĐ6 (đo trên máy thật).

**Chỉ số cần theo dõi**

| Chỉ số | Cách lấy | Ngưỡng đề xuất (chốt khi duyệt) |
|---|---|---|
| Số watch đang dùng / `max_user_watches` | service đếm số thư mục đã gắn; đọc `/proc/sys/fs/inotify/max_user_watches` | dùng ≤ 80% giới hạn; vượt → cảnh báo |
| Thời gian dựng watcher lúc khởi động | đo từ lúc bắt đầu tới lúc gắn xong | home 100.000 thư mục: ghi nhận, không chặn quét |
| Độ trễ sự kiện → bắt đầu quét (p50 / p95) | thời điểm kernel báo → thời điểm bắt đầu đọc (đã trừ debounce 300 ms) | p95 ≤ 2 giây |
| Số lần tràn hàng đợi (`IN_Q_OVERFLOW`) | đếm sự kiện tràn trong service | 0 file sót sau mỗi lần tràn (quét bù phải bù đủ) |
| Hàng đợi sự kiện thừa sau debounce | đếm sự kiện thô / sự kiện sau gộp | ghi nhận tỉ lệ; chỉnh debounce nếu > 10 sự kiện thô / 1 lần quét |
| CPU lúc rảnh | `pidstat -p <pid> 5` | < 2% (D6) |
| RAM của watcher | `/proc/<pid>/status`; kernel tốn ~1 KB/watch (`/proc/<pid>/fdinfo`) | ghi nhận, nằm trong `MemoryMax` |

**Kịch bản đo:** home 10.000 và 100.000 thư mục · giải nén 10.000 file · ghi liên tục một file lớn · tải file (`.part`/`.crdownload`) ·
nhiều người dùng cùng lúc · suspend/resume · chạy 24 giờ không có file đổi (đo CPU nền).

**Công cụ:** `/proc/sys/fs/inotify/*`, `inotifywait -m` (đối chiếu sự kiện), `pidstat`, `strace -c -p`, `perf top`; service ghi **metric định kỳ**
(mỗi 60 giây ở mức Info: số watch, số tràn, độ trễ p50/p95, sự kiện thô/gộp) vào FileLogger — chỉ ghi số, không ghi đường dẫn/nội dung (R7).

**Hành động khi vượt ngưỡng:** hết watch → cảnh báo rõ + bỏ bớt thư mục ẩn/loại trừ + quét định kỳ phần không gắn được; tràn hàng đợi → quét bù
(R5, đã có); độ trễ cao → giảm số file đọc cùng lúc hoặc tăng debounce; CPU nền cao → kiểm tra vòng lặp sự kiện thừa.

---

## 9. Rủi ro của kế hoạch

| Rủi ro | Khả năng | Ảnh hưởng | Giảm thiểu |
|---|---|---|---|
| Tách Core làm hỏng bản Windows đang chạy | Trung bình | Cao | GĐ1 bắt buộc so kết quả từng file trước/sau; tách từng phần một |
| Lượt quét đầu làm người dùng thấy máy chậm | Cao | Cao | Ưu tiên thấp (D6), giới hạn đồng thời, đo ở GĐ6 |
| Hết inotify watch trên thư mục home lớn (nặng hơn ở kernel cũ, `max_user_watches` thấp) | Trung bình | Cao | Bỏ thư mục ẩn + danh sách loại trừ; cảnh báo rõ; nâng `sysctl` qua gói cài; quét định kỳ dự phòng; theo dõi bằng mục 8.1 |
| .NET 6 hết hỗ trợ (không còn vá bảo mật) cho máy Ubuntu 16.04/CentOS 7 (D9) | Chắc chắn | Trung bình | Chỉ dùng net6 cho máy cũ; máy mới dùng net10; service không mạng (`PrivateNetwork`) giảm bề mặt tấn công; đặt thời hạn rà lại |
| Systemd 219 thiếu nhiều thiết lập hardening (không `AmbientCapabilities`, `ProtectSystem=strict`) | Chắc chắn | Trung bình | Phương án thay thế ở mục 5.4; kiểm thử bảo mật riêng cho máy cũ ở GĐ6 |
| Thiết kế interface chưa đủ cho macOS (FSEvents, quyền Full Disk Access/TCC khác) | Trung bình | Trung bình | Có khung `Platform.MacOS` biên dịch được ngay từ GĐ1; xác nhận lại khi bắt đầu bản macOS |
| `CAP_DAC_READ_SEARCH` vẫn là quyền mạnh (đọc được mọi file) | Chắc chắn | Trung bình | Không mạng, không ghi, chỉ một capability; chấp nhận như phương án ít rủi ro nhất cho system service (D5) |
| Kết quả nằm rải rác trên từng máy, không ai xem | Cao | Cao | **Câu hỏi mở 1** — cần chốt trước khi triển khai rộng |
| Thư mục home mã hoá (eCryptfs cũ) chỉ đọc được khi người dùng đăng nhập | Thấp | Thấp | Phát hiện và log; quét khi người dùng đăng nhập |
| Thư viện bên thứ ba chạy khác trên Linux và **trên net6** (PdfPig, OpenXml, ExcelDataReader, SQLite native) | Trung bình | Trung bình | Thử ngay ở GĐ0, kiểm thử đối chiếu ở GĐ2 |

---







