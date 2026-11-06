# Báo cáo GĐ3 — Watcher inotify cho thư mục home (06/11/2026)

Nhánh `feature/linux`, Ubuntu 18.04 (kernel 4.15, glibc 2.27, `max_user_watches` = 8192), .NET 10.0.403.
Kế hoạch: `docs/ke-hoach-trien-khai-linux.md` mục 4.3, 7 (GĐ3), 8.1. Phân tích trước đó: `docs/fsw-thu-muc-con-linux.md`.

## 1. Kết quả

| | |
|---|---|
| Build `DlpService.slnx` | 0 lỗi, 0 cảnh báo |
| Test | **233/233** (trước GĐ3: 189) |
| Kịch bản chạy thật (`scripts/watcher-scenarios.py`) | **17/17 PASS** |
| Canary log (`scripts/canary-log.py`) | PASS ở Information **và** Debug (nay có kiểm chứng mức Debug thật sự áp dụng) |
| Tiêu chí "Đạt" GĐ3 | **Đạt**, trừ ngưỡng p95 ≤ 2 s **trong đợt dồn 10.000 file** (2,6 s) — cần quyết định (mục 5) |

## 2. Thay đổi thiết kế quan trọng: nguồn sự kiện inotify riêng

`FileSystemWatcher` của .NET **không đáp ứng được GĐ3 trên Linux**:

| Yêu cầu GĐ3 | `FileSystemWatcher` | `InotifyFileChangeSource` (mới) |
|---|---|---|
| Bỏ thư mục ẩn / `node_modules` khỏi việc theo dõi | ❌ `IncludeSubdirectories` gắn watch lên MỌI thư mục con; chỉ lọc được sự kiện | ✅ không gắn watch cho thư mục bị loại |
| Biết số watch đang dùng / cảnh báo sắp hết | ❌ | ✅ `IWatchStatistics` |
| Phát hiện tràn hàng đợi | Có (`InternalBufferOverflowException`) | ✅ `IN_Q_OVERFLOW` + đếm |
| Số sự kiện cho 1 lần lưu file lớn | ~1 sự kiện / lần `write` | ✅ 1 sự kiện khi **đóng** file (`IN_CLOSE_WRITE`) |
| Gốc bị xoá / chuyển đi | Không chắc có báo | ✅ `IN_DELETE_SELF`/`IN_MOVE_SELF` → `Failed` → tự theo dõi lại |

Nằm ở `DlpService.Platform.Linux/InotifyFileChangeSource.cs` (P/Invoke `libc`: `inotify_init1`, `inotify_add_watch`, `inotify_rm_watch`,
`poll`, `read`) — Core không bị đụng (test kiến trúc vẫn xanh). 1 inotify instance cho mọi thư mục gốc, 1 luồng đọc (poll 250 ms).
Host Linux đăng ký mặc định; `"Watch:Backend": "fsw"` quay lại `FileSystemWatcher` nếu cần. **Bản Windows vẫn dùng `FileSystemWatcherSource`.**

## 3. Từng việc của GĐ3

| # | Việc | Làm gì | File chính |
|---|---|---|---|
| 3.1 | **Xoá / đổi tên / di chuyển (R4)** | `FileDeleted` nay được xử lý: file đúng đuôi → qua debounce, tới lượt mà file không còn → xoá bản ghi (xoá rồi tạo lại ngay = 1 lần quét, không xoá nhầm); đường dẫn khác (có thể là thư mục) → xoá bản ghi cả cây con. `ResultStore.RemovePath` xoá ở mọi bảng (cả `llm_job`/`llm_result`), ghi `audit` chỉ số lượng. Hash trong RAM được dọn (`FileProcessor.Forget`) | `ResultStore.Removal.cs`, `FileWatcherService.cs`, `FileProcessor.cs` |
| 3.2 | **Bỏ thư mục ẩn / loại trừ + `max_user_watches`** | `WatchExclusion` (`Watch:SkipHidden`, `Watch:ExcludeDirectoryNames`) dùng CHUNG cho nguồn sự kiện và mọi lượt quét; không đi theo symlink. Cảnh báo lúc khởi động **và lúc đang chạy** (≥ 80 % → Warning, không gắn được → Error kèm lệnh `sysctl`) | `WatchExclusion.cs`, `InotifyFileChangeSource.cs` |
| 3.3 | **Tự theo dõi lại (R6) + đối soát định kỳ** | Watcher lỗi → chờ 5 s → 10 s → … ≤ 60 s tới khi thư mục dùng được → `Unwatch` + `Watch` + quét. Đối soát mỗi `Watch:ReconcileIntervalMinutes` (Linux: 60): quét lại (file không đổi chỉ tốn 1 lần đọc size/mtime) + xoá bản ghi file không còn. Khởi động cũng đối soát (file bị xoá lúc service tắt) | `FileWatcherService.cs` |
| 3.4 | **Quét khởi động song song (R5) + so size/mtime trước/sau** | `Parallel.ForEach` tối đa `MaxConcurrency` (vẫn qua trạm gác chung). Sau khi đọc so lại size/mtime: khác → `ChangedDuringRead`, không ghi DB, hẹn xử lý lại | `FileWatcherService.cs`, `FileProcessor.cs` |
| 3.5 | **Người dùng mới** | Mẫu `Watch:Folders` (`/home/*/Documents`) được mở rộng lại mỗi `Watch:PatternRefreshSeconds` (Linux: 60) → thư mục mới khớp được theo dõi + quét. Không có thư mục nào khớp lúc khởi động vẫn chạy tiếp | `FileWatcherService.cs`, `Worker.cs` |
| 3.6 | **Metric + đo** | Mỗi `Watch:MetricsIntervalSeconds` (Linux: 60) 1 dòng `[Metric]`: watch dùng/giới hạn/lỗi, tràn, sự kiện thô, sự kiện đổi/xoá → số lần xử lý, trễ p50/p95/max, đang chờ, ghi dở, bản ghi đã xoá. **Chỉ số, không đường dẫn** (R7) | `FileWatcherService.cs`, `IWatchStatistics.cs` |
| + | `ScheduleProcess` an toàn đa luồng | `AddOrUpdate` thay cặp `TryGetValue` + gán (tồn đọng #5 của báo cáo GĐ2) | `FileWatcherService.cs` |

Đã có từ trước: lọc file tạm Linux (GĐ2 `LinuxPathPolicy`), vá thư mục con bị lỡ sự kiện (Bước 7).

## 4. Số đo (`scripts/watcher-scenarios.py --unzip 10000 --dirs 5000 --idle 60`)

| Kịch bản | Kết quả |
|---|---|
| Tạo / sửa / đổi tên / xoá file | ✅ bản ghi DB khớp đĩa |
| Chuyển thư mục (có thư mục con) ra ngoài | ✅ xoá bản ghi cả cây |
| Ghi dở: ghi nửa file, chờ 1,5 s (file còn mở), ghi nốt | ✅ quét **đúng 1 lần**, sau khi ghi xong |
| Tải về: `hop-dong.txt.part` → đổi tên `hop-dong.txt` | ✅ `.part` không quét, file thật được quét |
| **Giải nén 10.000 file** (50 thư mục) | ✅ **0 sót, 0 quét trùng, 0 lần tràn**; xong trong ~4 s (~2.600 file/s) |
| Xoá cả thư mục giải nén | ✅ xoá hết 10.000 bản ghi |
| Xoá rồi tạo lại `Documents` | ✅ tự theo dõi lại (R6) |
| Người dùng mới `chi-moi/Documents` | ✅ được theo dõi + quét, file tạo sau cũng bắt |
| 5.000 thư mục + 2.000 thư mục ẩn | ✅ dùng **5.053/8.192** watch — thư mục ẩn không tốn watch |
| CPU lúc rảnh 60 s | ✅ **0,47 %** (mục tiêu < 2 %) |
| RAM (RSS) | 130 MB |
| Trễ sự kiện → bắt đầu xử lý, lúc bình thường | ✅ p95 **4 ms** (mục tiêu ≤ 2 s) |
| Trễ trong đợt dồn (> 1.000 file / chu kỳ 5 s) | ⚠️ p95 **2,6 s** — xếp hàng ở trạm gác 2 chỗ (D6) |

**Vượt giới hạn watch** (`--dirs 9000`): dùng được 7.301/8.192 rồi hết — `max_user_watches` tính **chung cho mọi chương trình** của tài
khoản (~900 watch đang bị chương trình khác dùng). 1.656 thư mục không gắn được → log `[Error]` ngay (kèm lệnh `sysctl`), thư mục đó chỉ
còn được phát hiện thay đổi khi đối soát.

**Sửa trong lúc đo:** sự kiện xoá từng xếp hàng chung trạm gác với việc quét → xoá 10.000 file đẩy p95 lên 2,8 s. Nay xoá bản ghi không
qua trạm gác (chỉ là 1 lệnh DB): p50 của đợt xoá 10 ms.

## 5. Cần quyết định

| # | Vấn đề | Đề xuất |
|---|---|---|
| 1 | Ngưỡng p95 ≤ 2 s trong **đợt dồn** (giải nén 10.000 file): đạt 2,6 s với `MaxConcurrency=2` | Giữ D6 = 2 (không làm giật máy người dùng). Đổi tiêu chí: p95 ≤ 2 s **lúc bình thường** (đạt 4 ms) + đợt dồn "0 sót, 0 trùng, xong trong ≤ N giây" (đạt: 10.000 file ~4 s). Nếu muốn giữ ngưỡng cũ: tăng `MaxConcurrency` cho riêng đợt dồn |
| 2 | `max_user_watches` = 8192 trên máy cũ | Gói cài đặt GĐ4 ghi `/etc/sysctl.d/60-dlp-service.conf` với `fs.inotify.max_user_watches=524288` |
| 3 | Chu kỳ đối soát 60 phút = độ trễ tối đa cho thư mục không gắn được watch | Giữ 60; máy hết watch thì nên tăng giới hạn thay vì giảm chu kỳ |

## 6. Lỗi của GĐ2 tìm ra trong GĐ3

**`/etc/dlp-service/appsettings.json` bị file đi kèm ghi đè** (chế độ System). .NET nạp biến môi trường 2 lần (`DOTNET_*` ở đầu danh sách
nguồn, nhóm không tiền tố ở cuối); GĐ2 chèn `/etc` trước nguồn biến môi trường **đầu tiên** → `/etc` có ưu tiên thấp nhất. GĐ2 không thấy
vì chỉ thử `Watch:Folders` (file đi kèm để mảng rỗng → không có khoá nào để ghi đè). Hệ quả: lần chạy canary "Debug" của GĐ2 thực ra
chạy ở Information. Đã sửa (chèn trước nguồn biến môi trường **cuối cùng**), thêm test `LinuxHostConfigOrderTests`, và `canary-log.py`
nay tự kiểm mức Debug có hiệu lực. Chạy lại canary: PASS ở cả 2 mức.

## 7. Ảnh hưởng tới bản Windows (cần chạy lại test trên Windows)

| Thay đổi trong Core | Trên Windows |
|---|---|
| Xoá / đổi tên / chuyển thư mục → **xoá bản ghi DB** | Mới — trước đây bản ghi giữ mãi |
| Khởi động đối soát: xoá bản ghi file không còn trong thư mục theo dõi | Mới |
| Quét khởi động song song (tối đa `MaxConcurrency` = 6) | Nhanh hơn; tổng số file xử lý cùng lúc vẫn ≤ 6 |
| Lượt quét **không đi theo junction/symlink**, bỏ thư mục không đọc được (trước: lỗi quyền làm dừng cả lượt) | Thay đổi nhỏ |
| Watcher lỗi → tự theo dõi lại (R6) | Mới |
| File đọc trúng lúc đang ghi → không ghi DB, xử lý lại | Mới |
| `FileDeleted` của `FileSystemWatcherSource` báo mọi đường dẫn (không lọc đuôi) | Mới |
| Loại trừ thư mục, đối soát định kỳ, làm mới mẫu, metric | **Tắt** khi không cấu hình (mặc định Windows) |

## 8. File thay đổi

| File | Loại |
|---|---|
| `DlpService.Platform.Linux/InotifyFileChangeSource.cs` | Mới |
| `DlpService.Core/Platform/WatchExclusion.cs`, `IWatchStatistics.cs` | Mới |
| `DlpService.Core/Watching/WatchOptions.cs`, `Storage/ResultStore.Removal.cs` | Mới |
| `DlpService.Core/Platform/IFileChangeSource.cs` (+ `exclusion`, + `Unwatch`), `FileSystemWatcherSource.cs` | Sửa |
| `DlpService.Core/Watching/FileWatcherService.cs`, `Pipeline/FileProcessor.cs`, `Worker.cs` | Sửa |
| `DlpService.Platform.Linux/LinuxHost.cs` (đăng ký inotify, sửa thứ tự `/etc`), `.csproj` (InternalsVisibleTo) | Sửa |
| `DlpService.Host.Linux/appsettings.json` (`Backend`, `SkipHidden`, `ExcludeDirectoryNames`, các chu kỳ) | Sửa |
| Test mới: `WatchExclusionTests`, `ResultStoreRemovalTests`, `FileWatcherLifecycleTests`, `InotifyFileChangeSourceTests` + 4 hợp đồng mới, `LinuxHostConfigOrderTests` | Mới / sửa |
| `scripts/watcher-scenarios.py` (mới), `scripts/canary-log.py` (kiểm mức Debug) | Mới / sửa |
