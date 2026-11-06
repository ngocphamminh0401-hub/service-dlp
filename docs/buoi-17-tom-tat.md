# Buổi 17 (06/11/2026): Bước 7 tách lõi — build + test trên Linux

Máy: Ubuntu 18.04.6, kernel 4.15, glibc 2.27, nhánh `feature/linux` (kéo về tới commit `19c0d82`, Bước 6).
Kế hoạch: `docs/ke-hoach-tach-loi-dung-chung.md` mục "Bước 7".

| Hạng mục | Trạng thái |
|---|---|
| Cài .NET SDK vào `~/.dotnet` (không cần sudo): 10.0.403 (dùng), 8.0.427 (thử, không cần — có thể xoá `~/.dotnet/sdk/8.0.427`) | ✅ Xong |
| `dotnet build DlpService.slnx` trên Linux | ✅ 0 lỗi, 0 cảnh báo |
| Sửa lỗi SQLite trên glibc 2.27: tự biên dịch `libe_sqlite3.so` + `Directory.Build.targets` | ✅ Xong (máy cũ) |
| Bước 7 việc 1: test kiến trúc `CoreDependencyTests` (Core không phụ thuộc Windows/LLM) | ✅ Xong |
| Bước 7 việc 3: xử lý test fail do khác OS — mất sự kiện thư mục con trên Linux | ✅ Xong (phương án A) |
| `dotnet test DlpService.Tests` | ✅ **148/148** (đầu buổi: 130/143) |
| Commit | ❌ Chưa — chờ xác nhận |

**Tiêu chí "Đạt" Bước 7:** `dotnet build` + `dotnet test` chạy được trên Linux; mọi fail đều được giải thích → **Đạt**
(không còn fail). Còn tồn đọng: chưa chạy lại test trên Windows sau thay đổi ở mục 3.

## 1. .NET 10 trên Ubuntu 18.04

- Không được Microsoft hỗ trợ chính thức (yêu cầu 22.04+), nhưng **chạy được**: `dotnet --info` OK, không lỗi glibc.
- .NET 8 không build được code hiện tại nếu không sửa: `.slnx` (cần SDK 9.0.200+), `net10.0`, kiểu `Lock` (.NET 9+).
- Lưu ý: `dotnet` chưa có trong PATH — thêm vào `~/.bashrc`:
  `export DOTNET_ROOT=$HOME/.dotnet` và `export PATH=$PATH:$HOME/.dotnet`.
- `DlpService.Tests` **không nằm trong** `DlpService.slnx` → build solution không build test; phải chạy riêng.

## 2. SQLite: 12 test `ResultStore*` fail

- **Lỗi:** `libe_sqlite3.so: version 'GLIBC_2.34' not found` — bản SQLite trong NuGet (SQLitePCLRaw 2.1.12) biên dịch
  trên Linux mới. Không chỉ test: service chạy thật cũng không ghi DB được trên máy này.
- **Không dùng được SQLite của Ubuntu** (3.22): `ResultStore.cs` dùng `ON CONFLICT … DO UPDATE` (cần 3.24) và
  `RETURNING` (cần 3.35).
- **Cách vá (phương án B):**
  - `sudo apt-get install gcc libc6-dev`; tải SQLite **3.53.3** (đúng bản NuGet đang dùng) từ sqlite.org; biên dịch với
    **đúng các tuỳ chọn** của bản gốc (đã so khớp 100% danh sách compile option, gồm `DEFAULT_FOREIGN_KEYS`, `DQS=0`,
    FTS3/4/5, RTREE, GEOPOLY, SESSION, `MAX_VARIABLE_NUMBER=32766`…) → `~/.local/lib/dlp-sqlite/libe_sqlite3.so`.
  - `Directory.Build.targets` (mới, gốc repo): sau mỗi build, chép file trên đè vào `runtimes/linux-x64/native/` của mọi
    project. Chỉ chạy khi là Linux **và** file tồn tại → Windows/máy khác không ảnh hưởng.
- Khi nâng máy lên Ubuntu 22.04+: xoá `Directory.Build.targets`. Khi NuGet nâng bản SQLite: nên biên dịch lại cùng bản.

## 3. Mất sự kiện file trong thư mục con mới (Linux)

Chi tiết + so sánh 6 phương án: **`docs/fsw-thu-muc-con-linux.md`**.

- **Nguyên nhân (đã đo):** inotify không theo dõi đệ quy; .NET tự đăng ký watch cho thư mục con mới, khoảng hở < 50 ms.
  File ghi trong khoảng hở bị mất vĩnh viễn. Phát hiện thêm: `mv` một thư mục có sẵn file vào → **mất toàn bộ** file.
- **Đã sửa (phương án A)** trong `DlpService.Core/Platform/FileSystemWatcherSource.cs`:
  bỏ `watcher.Filters`, tự lọc đuôi (không phân biệt hoa/thường); khi có thư mục mới / chuyển vào → quét bù nền các file
  có sẵn; báo sự kiện lần lượt qua `_raiseLock`; không báo sau `Dispose`.
- **Test mới** (`FileChangeSourceContractTests`): `Chep_ca_cay_thu_muc_thi_bao_du_moi_file`,
  `Chuyen_thu_muc_tu_ngoai_vao_thi_bao_file_ben_trong`. Code cũ: 3 fail; code mới: 8/8 pass × 5 lần.
- **Khuyến nghị lâu dài:** A + quét đối soát định kỳ (phương án C) ở GĐ2.

## 4. Test kiến trúc Core

`DlpService.Tests/Architecture/CoreDependencyTests.cs` (mới), 3 test:
- `Dung_assembly_Core` — chốt đúng `DlpService.Core.dll` được kiểm.
- `Core_khong_tham_chieu_assembly_rieng_cua_Windows_hay_LLM` — cấm `System.Security.AccessControl`, `Grpc.*`,
  `System.Diagnostics.PerformanceCounter`, `Microsoft.Extensions.Hosting.WindowsServices`.
- `Core_khong_goi_thang_vao_thu_vien_Windows` — cấm P/Invoke tới `kernel32` / `advapi32`.
- Đã thử ngược: chèn tạm 1 `[DllImport("kernel32.dll")]` vào Core → test đỏ đúng chỗ; đã gỡ.
- Giới hạn: không thấy gói NuGet bắc cầu mà Core không gọi tới → kiểm thêm bằng
  `dotnet list DlpService.Core package --include-transitive` (hôm nay: sạch).

## 5. Các file thay đổi

| File | Loại |
|---|---|
| `Directory.Build.targets` | Mới |
| `DlpService.Core/Platform/FileSystemWatcherSource.cs` | Sửa |
| `DlpService.Tests/Architecture/CoreDependencyTests.cs` | Mới |
| `DlpService.Tests/Platform/FileChangeSourceContractTests.cs` | Sửa (+2 test) |
| `docs/fsw-thu-muc-con-linux.md`, `docs/buoi-17-tom-tat.md` | Mới |

## Ghi chú / bước sau

- Chạy lại toàn bộ test trên **Windows** để xác nhận thay đổi ở mục 3 (đặc biệt: đuôi viết hoa, `mv` thư mục).
- `ScheduleProcess` trong `FileWatcherService` không an toàn đa luồng (có từ trước) → nên đổi sang `AddOrUpdate` (Change Brief riêng).
- Cân nhắc thêm `DlpService.Tests` vào `DlpService.slnx`.
- Tăng `fs.inotify.max_user_watches` (máy này 8192) khi triển khai Linux.
- Cảnh báo có sẵn `xUnit2017` ở `DlpService.Tests/Reading/TikaNativeTests.cs:45,66` (không thuộc buổi này).
