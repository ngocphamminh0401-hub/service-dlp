# Buổi 13 — Đánh giá ảnh hưởng của kích thước / định dạng file và của tài nguyên hệ thống

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái |
|---|---|
| 3. Ảnh hưởng của kích thước và định dạng (thời gian + độ chính xác) | ✅ Xong — `docs/danh-gia-kich-thuoc-dinh-dang.md` |
| 4. Ảnh hưởng của tài nguyên hệ thống (CPU, RAM, I/O khi service chạy) | ✅ Xong — lượt `20261022-101205`, `docs/danh-gia-tai-nguyen.md` (lượt đầu `085800` lộ lỗi watcher → đã sửa) |
| **Sửa lỗi: watcher bị GC thu hồi khi service rảnh** (service ngừng quét file mới, không báo lỗi) | ✅ Đã sửa + 3 test (87/87 pass) — mục 2b |

## 1. Hạng mục 3 — `tools/eval/DlpSizeBench` (mới)

- `gen`: 64 mẫu rawdata có nhãn (4 × 16 lớp) → chèn văn bản đệm trung tính (tự kiểm: 0 chỗ khớp, không chứa từ khoá rule) → 7 định
  dạng (txt, csv, docx, xlsx, pptx, pdf, eml) × 5 bậc (gốc, 20k, 200k, 1,5 triệu ký tự — nội dung gốc ở giữa; 3 triệu — vượt giới hạn,
  nội dung gốc ở cuối). 1.568 file, 563 MB, sinh trong 47 giây. Hàm ghi file rút gọn từ `DlpTestGen`.
- `eval`: pipeline của service, đo thời gian từng bước, cấp phát / working set, độ chính xác theo bậc × định dạng.
- **Kết quả chính:** định dạng và kích thước (≤ 2 triệu ký tự) **không đổi mức phân loại** (0 % cặp đổi mức); quét regex ~31 ms / 100k
  ký tự; file 1,5 triệu ký tự < 1,3 giây; working set ≤ ~200 MB nhưng PDF lớn cấp phát tạm ~490 MB/file. **Điểm mù:** nội dung sau ký tự
  thứ 2.000.000 không được quét → 70,5 % file nhạy cảm bị hạ mức (F1 nhạy cảm 0 %) dù có cảnh báo "chỉ quét 2,000,000 ký tự đầu".

## 2. Hạng mục 4 — mở rộng `tools/perf/Measure-DlpServiceE2E.ps1` + `Analyze-DlpServiceE2E.py`

- Lấy mẫu mỗi giây thêm: I/O đọc/ghi của service, CPU Windows Defender (`MsMpEng`), CPU PowerShell (công cụ đo), % đĩa bận, RAM trống.
- `-RateSteps 5,20,50 -StepSeconds 120`: tải đều theo từng mức, file khác nhau giữa các mức; đo độ trễ từng file + tài nguyên mỗi mức.
- `-NoBurst`; `-IdleAfterSec` dài để chạy rảnh (soak) — báo cáo độ dốc RAM/handle/thread theo giờ.
- Quan sát lúc kiểm tra bộ lấy mẫu: máy chỉ còn ~0,5 GB RAM trống khi `LlmSvc` chạy (mô hình phi-4 giữ tới 10–12 GB), service chỉ ~17 MB.

- Sửa lỗi nhỏ: `-RateSteps` đổi từ `[string]` sang `[int[]]` (PowerShell hiểu `5,20,50` là mảng).

## 2b. Sửa lỗi nghiêm trọng: watcher bị GC thu hồi khi service rảnh

**Triệu chứng (lượt `20261022-085800`):** 10 file làm nóng được xử lý lúc 08:58:19, sau đó 44 file tuần tự + 1 file thử **không được
quét**; log không có lỗi/cảnh báo, nhịp tim và vòng LLM vẫn chạy → service trông bình thường nhưng "câm".

**Chẩn đoán:**
1. `dotnet-stack report -p 45100` (`tools/perf/results/stack-45100.txt`): **không luồng nào kẹt** — không có ProcessFile nào đang chạy hay chờ
   khoá → watcher không còn tồn tại, không phải treo/deadlock.
2. `Worker.ExecuteAsync`: `var fileWatcher = new FileWatcherService(...)` là **biến cục bộ, không dùng sau `await Task.WhenAll`** → bản Release
   không giữ trong state machine async. `FileSystemWatcher` của .NET chỉ tự giữ mình bằng **tham chiếu yếu** khi chờ sự kiện → khi không còn
   file nào đang xử lý (task đang chạy giữ `this`), lần GC tiếp theo thu hồi cả `FileWatcherService` lẫn watcher. Comment "Nhóm D3" đã giữ
   watcher ở field của `FileWatcherService`, nhưng bản thân `FileWatcherService` lại không ai giữ.
3. Thí nghiệm đối chứng (scratchpad): watcher không ai giữ nhận **0/5** sự kiện sau GC; watcher có tham chiếu nhận 5/5.
4. Vì sao các lượt đo trước không lộ: tải liên tục → luôn có task giữ tham chiếu. Lỗi chỉ xuất hiện sau khi **rảnh + 1 lần GC**. Máy ngủ qua
   đêm (17:03 → 08:08) không phải nguyên nhân.

**Sửa:**
- `Worker.cs`: trường `_fileWatcher` thay cho biến cục bộ (Worker là hosted service, sống suốt tiến trình); `finally` gọi
  `_fileWatcher.Dispose()` khi service dừng (trước khi đóng DB).
- `FileWatcherService.cs`: `: IDisposable` — tắt watcher, huỷ các file đang chờ debounce; ghi chú yêu cầu "nơi tạo phải giữ tham chiếu mạnh".
- `DlpService.Tests/Watching/FileWatcherServiceGcTests.cs` (mới, 3 test): có giữ tham chiếu → sau rảnh + GC vẫn xử lý file mới; không giữ →
  sau GC không xử lý (ghi lại cơ chế lỗi cũ); `Dispose` dừng theo dõi, gọi 2 lần không lỗi. 87/87 pass, chạy lặp 3 lần ổn định.

## 2c. Đánh giá xử lý đồng thời — `docs/danh-gia-xu-ly-dong-thoi.md`

- **Phần A (xong)** — `tools/perf/DlpConcurrencyBench` (mới): 4.000 file rawdata qua đúng pipeline ProcessFile + ghi DB, N luồng riêng dùng
  chung pipeline, mức 1–64; làm nóng bộ đệm file + 1 lượt trọn (sửa 2 lỗi đo: đọc đĩa lần đầu, tiered JIT). Kết quả: **0 lỗi, 0 lệch kết quả,
  DB đủ ở mọi mức** (an toàn đa luồng); thông lượng đỉnh **737 file/giây ở 8 luồng (×2,9)** rồi giảm (64 luồng 464); p95 6 ms → 256 ms; ghi DB
  0,4 → 111 ms (chờ khoá). 16 PDF lớn song song: RAM 452 MB, chậm hơn 4 luồng. Khuyến nghị: giới hạn số file xử lý cùng lúc (4–8).
- **Phần B (xong, lượt `20261022-143106`)** — `Measure-DlpServiceE2E.ps1`: `-BatchSizes`, `-EditStorm`, `-LockedFile`, `-PostIdleProbe`.
  Lô 1–5.000 file: 0 mất / trùng / tràn buffer, thông lượng chững ~70 file/giây (lô 5.000 → file cuối chờ ~43 giây). Ghi đè 20 lần → xử lý
  1 lần, mức cuối đúng. Sau 30 phút rảnh: 20/20 ✅ (bản sửa watcher đã kiểm chứng). **❌ File bị khoá lúc đọc không được thử lại** — bị bỏ sót
  38 phút, tới khi service khởi động lại. Script đo có lỗi đọc dòng log (`Poll-Into` trả cả List) nên in sai "0 lần" ở 2 kịch bản này; số đúng
  lấy từ log service.

## 3. Việc còn mở

1. ~~Publish bản sửa rồi đo lại hạng mục 4~~ → xong (lượt `101205`); ~~phần B đồng thời~~ → xong (lượt `143106`).
   **Mới:** sửa lỗi service bỏ sót file bị khoá (thử lại khi đọc lỗi). ~~Sửa lỗi `Poll-Into` trong script đo~~ → xong (`Poll-Matching`).
   Khởi động lại mất 19 giây vì quét toàn thư mục (41.468 file) trước khi bật watcher → kiểm xem file mới trong khoảng này có bị lỡ không.
2. Cân nhắc lớp bảo vệ thêm: quét bù định kỳ (VD mỗi 10 phút) để service tự phục hồi nếu watcher hỏng vì lý do khác (ổ mạng, mất quyền...).
3. Quyết định cách xử lý file vượt 2.000.000 ký tự (quét theo khúc, hoặc không kết luận Public cho file bị cắt).
4. Giới hạn số file xử lý cùng lúc (`Watch:MaxConcurrency`, 4–8) — theo `docs/danh-gia-xu-ly-dong-thoi.md`.
