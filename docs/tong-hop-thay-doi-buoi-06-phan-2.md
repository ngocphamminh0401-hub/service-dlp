# Tổng hợp thay đổi — Buổi 6, phần 2 (28/09/2026): lưu trữ SQLite + hàng đợi LLM

Bản tóm tắt ngắn, nối tiếp `docs/tong-hop-thay-doi-buoi-06.md`. Chi tiết, lý do thiết kế và số liệu đầy đủ:
`docs/buoi-06-tom-tat.md` (mục được ghi trong ngoặc).

## 1. Tổng quan

| # | Hạng mục | Đổi hành vi service? | Trạng thái |
|---|---|---|---|
| 1 | Đủ 7 bảng lược đồ mục 3.1, kiểm tra lại WAL / busy_timeout, khoá ACL thư mục dữ liệu (4i) | **Có** | ✅ Xong |
| 2 | Cache `file_state`: bỏ qua file không đổi theo (size, mtime) rồi `file_hash` (4j) | **Có** (khởi động nhanh hơn) | ✅ Xong, đã chạy trên service đã cài |
| 3 | Hàng đợi `llm_job` + máy trạng thái 6 trạng thái (4k) | **Có** (thêm job) | ✅ Xong |
| 4 | Eligibility filter + phân nhóm P1/P2/P3 + hạn ngạch (4l) | **Có** | ✅ Xong |
| 5 | `llm_cache` + `llm_result`: upsert theo khoá, cache hai tầng (4m) | Không (chưa có worker gọi) | ✅ Xong |
| 6 | Dọn dẹp theo lịch (job, cache, audit cũ) (4n) | **Có** | ✅ Xong |
| 7 | Công cụ test kill service / worker giữa chừng (4n) | Không | ✅ Tất cả PASS |

Mọi thay đổi code: build 0 warning, 0 error. **Service đã cài mới có mục 1–2** — các mục 3–6 cần publish lại (mục 6 dưới đây).

## 2. Kết quả đo chính

### Cache `file_state` (753 file `watch-folder`)

| Lần khởi động | Thời gian quét | Ghi chú |
|---|---|---|
| Lần đầu / DB cũ lưu mtime theo giây | 54–93 s | Trích text + quét toàn bộ |
| **Lần sau (file không đổi)** | **1,1 s** | Bỏ qua cả 753 file, không mở file nào |

Service đã cài (bản publish): khởi động lại → `753 file — quét 0, bỏ qua 753 không đổi`.

### Phân nhóm gửi LLM (753 file `watch-folder`)

| Vùng Router | Số file | Job tạo ra |
|---|---|---|
| Chắc chắn (S1 ≥ 0,80) | 510 | 0 — không bao giờ gửi LLM |
| Vùng xám | 32 | 31 **P1** (1 file chỉ 29 ký tự → `final` + `needs_review`) |
| Không khớp + tín hiệu yếu | 55 + 156 | 4 **P2** (tên có "nhan-su"), 1 **P3** (lấy mẫu 2 %) |

Tổng 36 / 753 file (4,8 %) — nằm trong mục tiêu gửi LLM 2–10 % của kiến trúc.

### Test chịu lỗi (`tools/resilience`, kill thật = như "End task" / mất điện)

| Kịch bản | Kết quả |
|---|---|
| Kill service 3 lần giữa lúc quét, rồi quét nốt | DB không hỏng; so từng file với lần chạy sạch: **0 khác biệt** |
| Kill 2 worker 3 lần giữa lúc job đang chạy, khởi động lại service | **36/36 job done**, mỗi job đúng 1 `llm_result`; 6 job bị bỏ dở được khôi phục, không job nào xử lý thừa |
| Hoàn thành trùng 1 job (at-least-once) | 9 lần, vẫn 1 `llm_result` / job |

## 3. Thay đổi trong service (`DlpService/`)

| Thư mục / file | Thay đổi |
|---|---|
| `Storage/ResultStore.cs` | Tạo đủ 7 bảng + `user_version=1`; đọc lại `journal_mode` / `busy_timeout`, sai thì cảnh báo; khoá ACL thư mục dữ liệu; cache `TryGetFileState` / `MarkUnchanged`; `mtime_utc` lưu đơn vị 100 ns (trước: giây); `SaveScan` tạo job trong cùng transaction và trả kết quả hàng đợi |
| `Storage/DataFolderSecurity.cs` (mới) | ACL: tắt kế thừa, chỉ SYSTEM + Administrators + tài khoản đang chạy |
| `Storage/ResultStore.LlmJobs.cs` (mới) | Máy trạng thái `pending/running/done/failed/timeout/stale`; lấy job nguyên tử; thử lại 3 lần (chờ 1/5/20 phút); khôi phục job `running` khi khởi động; hạn ngạch + hàng đợi đầy; dọn job/audit cũ; mỗi bước chuyển ghi `audit` |
| `Storage/ResultStore.LlmCache.cs` (mới) | Cache hai tầng: theo `file_hash` + phiên bản (từ `llm_result`), theo `pack_hash` (`llm_cache`); kiểm tra JSON hợp lệ trước khi lưu |
| `Classification/EscalationPolicy.cs` (mới) | Điều kiện cần (≥ 200 ký tự, đường dẫn loại trừ) + phân nhóm P1/P2/P3 |
| `Watching/FileWatcherService.cs` | Cache 2 tầng trước khi trích text; chụp size/mtime TRƯỚC khi đọc file; gọi phân nhóm ở mọi nhánh; log `[Job]`; log tổng kết mỗi lượt quét thư mục |
| `Worker.cs` | Khôi phục job + tạo bù job vùng xám lúc khởi động; dọn dẹp lúc khởi động và mỗi 24 giờ; đọc cấu hình `Llm` |

## 4. Công cụ mới (`tools/`)

| Công cụ | Lệnh | Dùng để |
|---|---|---|
| `tools/resilience/DlpResilience` | `dotnet run -c Release --project tools/resilience/DlpResilience` (≈ 3 phút; `-- --kills 5 --keep` để kill nhiều hơn / giữ DB tạm) | Chạy `DlpService.exe` thật với DB tạm, kill giữa chừng, kiểm tra không mất / không trùng / DB không hỏng. Báo cáo ở `tools/resilience/results/` |

Nên chạy lại sau mỗi thay đổi ở `Storage/` hoặc luồng quét, và khi có worker LLM thật.

## 5. Cấu hình, log

**`appsettings.json`** (mới thêm mục `Llm`):

```json
"Llm": {
  "MinTextChars": 200,
  "ExcludedPaths": [],
  "SuspiciousKeywords": [ "luong", "bangluong", "hopdong", "nhansu", "password", "matkhau", "backup", "cccd", "salary" ],
  "SampleRatePct": 2,
  "CacheDays": 90,
  "JobRetentionDays": 30,
  "AuditRetentionDays": 180,
  "CleanupIntervalHours": 24,
  "Quota": { "P1PerHour": 200, "P2PerDay": 300, "P3PerDay": 50, "MaxPendingJobs": 1000 }
}
```

**Dòng log mới** (chỉ đường dẫn, mã, số — không có nội dung file):

```
info: [DB] Đã khoá quyền thư mục dữ liệu <thư mục> (SYSTEM, Administrators, tài khoản hiện tại)
warn: [DB] Không bật được WAL, journal_mode đang là '<mode>'
info: [FileWatcher] Quét <thư mục>: 753 file — quét 0, bỏ qua 753 không đổi, lỗi đọc 0
info: [Job] Khởi động: 0 running → pending, tạo bù 0 job vùng xám. Hiện có: pending=36
info: [Job] <file>: tạo job P2 (tên đáng nghi: nhansu)
info: [Job] <file>: tạo job P1 nhưng HOÃN sang giờ sau — vượt hạn ngạch P1/giờ
warn: [Job] <file>: bỏ job P3 (lấy mẫu 2%) — hàng đợi đầy
warn: [DLP] <file>: vùng xám nhưng không gửi LLM (quá ít text (29 < 200 ký tự)) — final, needs_review
info: [Dọn dẹp] Xoá 0 job stale/failed > 30 ngày, 0 mục llm_cache > 90 ngày (còn 0), 0 dòng audit > 180 ngày. Lần sau: 22:53 29/09
```

## 6. Việc cần làm để áp dụng

Chạy bằng PowerShell **Administrator** (giống lần trước):

```powershell
sc.exe stop DlpService
dotnet publish D:\Service-DLP\DlpService\DlpService.csproj -c Release -r win-x64 --self-contained false -o D:\Service-DLP\publish
sc.exe start DlpService
```

Sau khi chạy: log khởi động có dòng `[Job] Khởi động: ... tạo bù N job vùng xám` và `[Dọn dẹp] ...`. DB ở
`D:\Service-DLP\publish\data\dlp.db`, chỉ mở được bằng quyền Administrator (ACL).

## 7. Điểm cần biết / hạn chế

- **Chưa có worker LLM** → mọi job nằm ở `pending`; các hàm cache (`TryGetResultForContent`, `TryGetLlmCache`) chưa được gọi.
- **Cache không biết bộ rule đã đổi:** sửa / thêm rule thì file cũ vẫn giữ kết quả theo rule cũ cho tới khi file bị sửa.
- **File cũ chỉ được xét P2/P3 khi bị sửa** (quyết định của người dùng) — muốn xét lại toàn bộ thì xoá DB để quét lại.
- **Job tạo bù lúc khởi động không qua bộ lọc độ dài text** (không còn text để đo) — chỉ ảnh hưởng dữ liệu quét trước khi có bộ lọc.
- **Từ khoá P2 có thể khớp nhầm:** `Chất lượng.docx` khớp `luong` (tách được từ riêng). Hạn ngạch P2 chặn số lượng.
- **File bị xoá / đổi tên:** dòng cũ vẫn còn trong DB; đổi tên → quét lại như file mới (cache theo đường dẫn).
- **`dlp.db` mở bằng DB Browser cần quyền Administrator** (tài khoản thường không đọc được — đúng thiết kế).
- **Chưa kiểm trực tiếp lần dọn định kỳ thứ 2** (chu kỳ tối thiểu 1 giờ); đã kiểm lần dọn lúc khởi động và `PurgeOldAudit` riêng.

## 8. Cải thiện sẽ làm sau

| Việc | Vì sao / ghi chú |
|---|---|
| **Worker LLM** (kiến trúc mục 4.12) | Lấy job → tra cache tầng 1 → đóng gói + mask → tra tầng 2 → gọi LLM (deadline 60 s) → `CompleteJob(..., packHash)`. Chỉ chạy khi máy rảnh; circuit breaker. `RecoverRunningJobs` phải chạy XONG trước khi worker bắt đầu. Chạy lại `tools/resilience` với worker thật |
| **Merge S1 + S2** (kiến trúc mục 7) | Cập nhật `scan_result` từ `provisional` → `final` sau khi có kết quả LLM; `source = merged` |
| **Đưa hằng số hàng đợi vào cấu hình** | 3 lần thử lại, backoff 1/5/20 phút đang viết trong code |
| **Xoá cache khi bộ rule đổi** | Lưu hash bộ rule (VD trong `audit` hoặc bảng meta); khác lần trước → quét lại toàn bộ |
| **Dọn dòng của file đã bị xoá** | Lúc dọn định kỳ: `file_state` có đường dẫn không còn tồn tại → xoá (cả `scan_result`, `finding`); job `pending` của file đó → `stale` |
| **Tín hiệu P2 theo tiêu đề cột + mật độ chuỗi số** | Cần extractor giữ cấu trúc cột (csv, xlsx) |
| **Điều kiện cần số 3–4 của mục 4.8** | `llm_cache` theo `pack_hash`, `file_hash` đã xử lý ở cùng `model_ver`/`prompt_ver` — hàm đã có, nối vào worker |
| **Thu nhỏ file DB sau khi dọn** | Xoá dòng không làm file `.db` nhỏ lại; cân nhắc `PRAGMA wal_checkpoint(TRUNCATE)` + `incremental_vacuum` sau lần dọn |
| **Đường dẫn không phân biệt hoa thường** | `file_state.path` đang so phân biệt hoa thường; Windows thì không → cùng 1 file có thể thành 2 dòng nếu sự kiện trả đường dẫn khác kiểu chữ. Chuẩn hoá đường dẫn hoặc dùng `COLLATE NOCASE` |
| **Thư mục theo dõi đang viết cứng** | `D:\Service-DLP\watch-folder` trong `Worker.cs` → đưa vào `appsettings.json` (công cụ test cũng đọc theo) |
| **Script xem DB** (`scripts/show-db.ps1`) | In số dòng mỗi bảng, file cần xem lại, job theo trạng thái — khỏi cài thêm công cụ |
| **Cân nhắc cho chính xác hơn** | Từ khoá P2 theo danh sách cho phép / chặn theo cụm (giảm khớp nhầm kiểu "chất lượng"); điều chỉnh tỉ lệ P3 theo số liệu shadow mode |
