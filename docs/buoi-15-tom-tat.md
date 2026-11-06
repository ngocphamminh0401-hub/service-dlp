# Buổi 15: Benchmark đồng thời (CPU/RAM), phân tích cache trong luồng, magic bytes cho bộ đọc .NET

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái |
|---|---|
| `DlpConcurrencyBench`: đo CPU (TB, đỉnh, lõi dùng, CPU/file), RAM (TB, đỉnh, private), GC dừng; thêm mức 6/10/12 | ✅ Xong |
| `DlpConcurrencyBench`: chế độ đo cache trong luồng (`--cache-level`, 3 lượt lạnh / tầng 1 / tầng 2) | ✅ Xong |
| Sửa lỗi đo: Windows 11 hạ xung (EcoQoS) tiến trình đo chạy nền → tự tắt | ✅ Xong |
| Magic bytes cho bộ đọc .NET (`FileTypeSniffer` + `FileReaderService.ResolveByContent`) | ✅ Xong; 110/110 test |
| Chi phí thêm của magic bytes (A-B-B-A) | ✅ Nằm trong mức nhiễu |
| Deploy lên service | ⏳ Script `tools/perf/Run-ReaderComparisonE2E.ps1` (cần Admin) — xem mục 7; lưu ý mục 5 |
| `Watch:MaxConcurrency` (mặc định 6) | ✅ Xong (mục 7); 113/113 test |
| So sánh .NET vs Tika ở 6 luồng — trong tiến trình | ✅ `docs/so-sanh-bo-doc-6-luong.md` |
| So sánh .NET vs Tika ở 6 luồng — service thật | ⏳ Chờ chạy script (Admin) |

## 1. Thay đổi code

- **Mới** `Reading/FileTypeSniffer.cs`: `Detect(path)` trả về `DetectedFormat`.
  - zip: phân biệt docx / xlsx / pptx bằng phần chính bên trong (`word/document.xml`...), chỉ đọc danh mục zip, không giải nén.
  - OLE: phân biệt doc / xls / ppt bằng tên luồng trong thư mục OLE (`WordDocument`, `Workbook`/`Book`, `PowerPoint Document`).
  - pdf, rtf; văn bản (có BOM hoặc không có byte 0 ở 8 KB đầu); nhị phân lạ.
  - Mọi chữ ký đều so **đúng ở byte 0**: tìm `%PDF-` sâu hơn sẽ bắt nhầm file .txt có ghi chú "%PDF-1.4..." ở dòng đầu.
- `Reading/FileReaderService.cs`:
  - `TryReadFile`: nhận diện ngay sau bước tra đuôi, trước bước kiểm tra kích thước (vì giới hạn kích thước thuộc về bộ đọc được chọn cuối cùng).
  - `ResolveByContent` có 4 trường hợp:
    1. Định dạng thật ≠ đuôi → dùng bộ đọc của định dạng thật + cảnh báo. Chưa có bộ đọc cho định dạng đó (VD .doc khi chưa bật Tika) → báo lỗi rõ.
    2. Nội dung là văn bản, đuôi nhị phân → đọc như .txt.
    3. Đuôi văn bản (.txt/.csv/.eml), nội dung zip/OLE không rõ loại → báo lỗi, không đọc rác.
    4. Còn lại → giữ bộ đọc theo đuôi như cũ.
  - Cảnh báo đổi đuôi được ghép với cảnh báo khác, không đè lên nhau.
  - `FindExtractor` (Pass 2 LLM) chọn theo cùng logic, để text và hash khớp Pass 1.
- Test: `DlpService.Tests/Reading/FileTypeSnifferTests.cs`, 13 test (file docx/pdf/zip tạo ngay trong test; corpus `tools/tika-bench/corpus` kiểm thêm khi có). `TikaNativeTests` được đưa vào cùng collection `TikaNativeRuntime`.
- `tools/perf/DlpConcurrencyBench/Program.cs`:
  - Tách `Measure()` (chạy N luồng + lấy mẫu CPU/RAM/thread mỗi 50 ms, CPU đỉnh tính theo cửa sổ 250 ms).
  - `RunCachePasses()`; `Pipeline.Process(useCache, mtimeShiftTicks)`.
  - `DisablePowerThrottling()`; thêm `--tag`, `--cache-level`; ghi thêm file `-resources.csv`.

## 2. Benchmark số file xử lý đồng thời (bộ đọc .NET, bản có magic bytes)

Kết quả thô: `tools/perf/results/concurrency-20261029-*-legacy-B-full.*`.
- 4.000 file rawdata (docx/pdf), máy i7-13700H, 20 luồng logic, cắm sạc.
- DlpService + DlpLlmHost chạy nền nhưng rảnh.

| Song song | File/s | Độ trễ p50 / p95 (ms) | Ghi DB TB (ms) | CPU TB (đỉnh) | Lõi dùng | CPU/file (ms) | RAM TB / đỉnh (MB) | GC dừng |
|---|---|---|---|---|---|---|---|---|
| 1 | 262 | 3,5 / 5,4 | 0,3 | 5 % (6 %) | 0,9 | 3,5 | 114 / 117 | 2 % |
| 4 | 770 | 4,4 / 9,9 | 0,5 | 15 % (19 %) | 3,1 | 4,0 | 124 / 129 | 14 % |
| **6** | **895** | **5,8 / 14,5** | 0,8 | 21 % (23 %) | 4,2 | 4,7 | 126 / 134 | 22 % |
| 8 | 897 | 7,4 / 21,0 | 1,2 | 21 % (30 %) | 4,2 | 4,6 | 131 / 140 | 28 % |
| 10 | 898 | 9,2 / 25,9 | 1,9 | 23 % (30 %) | 4,6 | 5,2 | 137 / 148 | 34 % |
| 12 | 841 | 11,8 / 33,0 | 2,7 | 22 % (28 %) | 4,5 | 5,3 | 139 / 148 | 38 % |
| 16 | 842 | 16,1 / 39,1 | 5,7 | 25 % (36 %) | 4,9 | 5,8 | 145 / 158 | 42 % |
| 32 | 748 | 39,6 / 72,1 | 24,9 | 26 % (38 %) | 5,1 | 6,8 | 158 / 176 | 51 % |
| 64 | 704 | 87,8 / 158,5 | 71,0 | 23 % (33 %) | 4,5 | 6,5 | 167 / 189 | 52 % |

16 PDF lớn (1,5 triệu ký tự): 1 luồng 13,1 s / 156 MB; **4 luồng 6,0 s / 207 MB**; 16 luồng 10,6 s / **653 MB** (GC dừng 70 %).

**Nhận xét:**
1. Thông lượng **bão hoà từ 6 luồng** (895–898 file/s ở 6–10 luồng) và giảm từ 12 luồng.
2. Dù chạy 64 luồng, CPU không vượt 26 % toàn máy (khoảng 5 lõi). Điểm nghẽn **không phải CPU** mà là:
   - **GC**: dừng các luồng 22 % thời gian ở 6 luồng và 52 % ở 64 luồng; pipeline cấp phát khoảng 1,6 MB/file.
   - **Khoá ghi SQLite**: từ 16 luồng trở lên, ghi DB TB tăng từ 0,8 lên 71 ms.
3. CPU/file tăng từ 3,5 ms (1 luồng) lên 6,8 ms (32 luồng): CPU bị phí vào GC và chờ khoá.
4. **Đề xuất `Watch:MaxConcurrency = 6`**: cùng thông lượng với 8–10, p95 thấp hơn (14,5 so với 21–26 ms), RAM thấp hơn. File lớn (> 5 MB) nên giới hạn riêng ở 2–4.
5. Hướng tối ưu tiếp theo, cần đo: **Server GC** (GC nhiều luồng, đổi lại tốn RAM hơn) cho vấn đề GC dừng 22–52 %.

### Sửa lỗi đo: hạ xung EcoQoS
- Lượt đo đầu chạy nền: 1 luồng chỉ đạt 76 file/s, CPU đúng bằng 1 lõi chạy kín.
- Sau khi tiến trình tự tắt hạ xung (`SetProcessInformation(ProcessPowerThrottling)`): 262–286 file/s.
- **Cần kiểm tra thêm:** service thật (chạy dưới SCM, không có cửa sổ) có bị Windows hạ xung không? (Task Manager → cột "Efficiency mode").

### Chi phí thêm của magic bytes (A = trước, B = sau; chạy A1-B1-B2-A2)

| Lượt | 1 luồng (file/s) | 8 luồng (file/s) | CPU/file 8 luồng (ms) |
|---|---|---|---|
| A1 | 280 | 982 | 4,4 |
| B1 | 286 | 840 | 5,5 |
| B2 | 207 | 842 | 5,4 |
| A2 | 276 | 801 | 6,0 |

- Hai lượt cùng bản A lệch nhau 20 % (982 so với 801), lớn hơn chênh lệch trung bình giữa A và B (892 so với 841, khoảng 6 %).
- Ở 1 luồng, B1 và A bằng nhau (trích TB 2,1 ms cả hai).
- **Kết luận:** chi phí thêm nằm trong mức nhiễu (mỗi file chỉ đọc thêm 8 KB đầu + danh mục zip).

## 3. Phân tích cache trong luồng xử lý

Luồng 1 file: sự kiện watcher → debounce 300 ms → `ProcessFile`.

| Bước | Cache | Chi phí đo được (8 luồng) | Ghi chú |
|---|---|---|---|
| 1. `FileInfo` size/mtime | — | rất nhỏ | Chụp TRƯỚC khi đọc file |
| 2. `TryGetFileState` | đọc DB | 0,21–0,24 ms | Kết nối chỉ-đọc riêng, không chờ khoá ghi |
| 3. **Tầng 1** size+mtime+đã kết luận | trúng → return | **0,17 ms/file, 32.435 file/s, CPU 0,04 ms/file** | Nhanh gấp khoảng 39 lần quét đủ |
| 4. Trích text | — | **5,9–7,3 ms** (khoảng 70 % thời gian) | Bước đắt nhất |
| 5. SHA-256 text | — | nhỏ | |
| 6. **Tầng 2** hash text trùng | trúng → `MarkUnchanged` | **1.032 file/s, CPU 3,66 ms/file** | Chỉ nhanh hơn quét đủ (838 file/s, 5,33 ms) **khoảng 23 % thông lượng / 31 % CPU** |
| 7. `_lastContentHash` (RAM) | chống quét trùng sự kiện | — | Không bao giờ được dọn |
| 8. Quét → phân loại → `SaveScan` | — | khoảng 2–3 ms | |

**Kết luận phân tích:**
1. Tầng 1 rất hiệu quả: lúc khởi động, khoảng 41.000 file không đổi gần như không tốn gì ngoài truy vấn DB tuần tự.
2. **Tầng 2 yếu**, vì nó nằm SAU bước trích text (bước đắt nhất). Gợi ý: thêm tầng "1,5" là băm byte thô của file (đọc file rẻ hơn nhiều so với parse docx/pdf) để bỏ qua cả bước trích text.
3. 🔴 **Cache không xét `rules_ver`, cũng không xét phiên bản bộ đọc**: deploy rule mới hoặc bộ đọc mới (như magic bytes buổi này) thì file đã cache **không được quét lại**.
4. 🔴 Có thể né bằng cách sửa nội dung giữ nguyên độ dài rồi đặt lại mtime (`SetFileTime`). Gợi ý: so thêm ChangeTime của NTFS (API thông thường không đặt lại được).
5. 🟠 Khoá theo đường dẫn: đổi tên hoặc di chuyển = quét lại; không nghe `Deleted`, nên dòng `file_state` cũ còn mãi. 🟠 `_lastContentHash` chỉ tăng.

## 4. Kiểm chứng magic bytes trên corpus đổi đuôi (`tools/tika-bench/corpus/renamed`)

| File | Trước | Sau |
|---|---|---|
| docx_as.pdf / pdf_as.docx / txt_as.docx | Đọc lỗi, không có bản ghi | Đọc đúng parser |
| **docx_as.txt** | "Thành công" với 4.013 ký tự **rác** (20,6 % ký tự lỗi), kết luận **sạch** | 1.448 ký tự đúng qua parser docx |
| xls_as.xlsx / xlsx_as.xls | Đọc được (ExcelDataReader tự nhận) | Đọc được + cảnh báo đổi đuôi |

## 5. Lưu ý khi deploy

- File đổi đuôi **đã được quét trước khi deploy** vẫn bị tầng 1 bỏ qua (size/mtime không đổi), nên kết quả sai cũ ("sạch") vẫn giữ nguyên.
- Cần chọn cách quét lại 1 lần, VD xoá `file_state` của các file .txt/.csv/.eml, hoặc giải quyết luôn vấn đề 3 ở mục 3.

## 6. Việc tiếp
Giới hạn riêng cho file lớn; thử Server GC; đưa `rules_ver` / phiên bản bộ đọc vào điều kiện cache; kiểm tra service có bị hạ xung EcoQoS;
thử lại file bị khoá (lỗi còn mở từ buổi 13); chạy E2E so sánh 2 bộ đọc rồi chọn cấu hình bộ đọc.

## 7. Phần 2: giới hạn 6 file đồng thời + so sánh 2 bộ đọc

### Thay đổi code
- `Watching/FileWatcherService.cs`:
  - `SemaphoreSlim _gate` ("trạm gác"); `DefaultMaxConcurrency = 6`; thuộc tính `MaxConcurrency` (≥ 1).
  - `ProcessAfterDebounceAsync`: sau debounce thì `await _gate.WaitAsync(token)` rồi mới `ProcessFile`, `finally Release`. Đang xếp hàng mà có sự kiện mới hơn thì huỷ được. Catch đổi thành `OperationCanceledException`.
  - `ScanExistingFiles` cũng xin chỗ, để tổng số file chạy cùng lúc ≤ giới hạn.
  - Log "[FileWatcher] Xử lý tối đa N file cùng lúc".
- `Worker.cs`: đọc `Watch:MaxConcurrency` (sai → mặc định + cảnh báo). `appsettings.json`: thêm `"Watch": { "MaxConcurrency": 6 }`.
- Test mới `Watching/FileWatcherConcurrencyTests.cs`: thả 12 file, dựng lại khoảng xử lý từ dòng `[Perf]`, kiểm tra số file chạy cùng lúc ≤ giới hạn (1 và 3); cấu hình 0 → 1.
  - Bài học: lần đầu test báo "4 > 3" là **lỗi đo**. "tổng X ms" làm tròn 0,1 ms, nên khoảng dựng lại bị chồng giả 0,002–0,016 ms. Đã bù 0,06 ms.
- Công cụ đo:
  - `tools/shared/PowerThrottling.cs` (tắt EcoQoS, dùng chung cho `DlpBaseline` và `DlpConcurrencyBench`).
  - Bench thêm `--bigpdf-levels`.
  - Script mới `tools/perf/Run-ReaderComparisonE2E.ps1` (publish + đo service thật lần lượt 2 bộ đọc + khôi phục cấu hình).

### Đánh giá: nếu cho service quét toàn máy (chưa sửa code, chỉ phân tích + kiểm chứng)

**Kiểm kê C:\ + D:\** (script Python chỉ đọc metadata, 88 giây):
- 1.454.765 file, 336.233 thư mục, 642 thư mục bị cấm (khi chạy bằng user).
- **95.203 file đúng đuôi hỗ trợ (7,65 GB)**, trong đó khoảng 40.000 là dữ liệu thử (`dlp-classifier`, `Service-DLP`).
- Theo đuôi: txt 33.673, docx 29.387, pdf 28.125, csv 1.277, …; .doc/.ppt/.rtf 859 file (chỉ Tika đọc được).
- Theo kích thước: 10 file vượt giới hạn kích thước, 150 file từ 5 MB tới giới hạn.
- Theo vị trí: 16.909 file trong AppData (cache, log ứng dụng, thay đổi liên tục).

**Kết luận:**
- Tài nguyên **chịu được**. Giới hạn 6 file chặn CPU ≤ khoảng 30 % máy và RAM ≤ khoảng 130 MB (.NET) / 400 MB (Tika); máy còn trống 2,4 GB RAM.
- Nhưng ở dạng hiện tại, service **sẽ dừng ngay lúc khởi động**:
  - `ScanExistingFiles` dùng `Directory.EnumerateFiles(..., SearchOption.AllDirectories)`. Đã thử trên `C:\Users`: **ném `UnauthorizedAccessException` ngay ở `C:\Users\Default User`, sau 0 file**. Dùng `IgnoreInaccessible = true` thì duyệt đủ 143.221 file.
  - Lỗi này không được bắt trong `Worker.ExecuteAsync`, nên host dừng (mặc định StopHost). Theo code: SCM sẽ khởi động lại mỗi 5 giây rồi lại lỗi y như vậy.
  - Thư mục bị cấm có quyền cấm cho Everyone, nên áp dụng cả với LocalSystem.

**Rủi ro khác:**
- Thư mục theo dõi bị cố định trong `Worker`.
- Lượt quét đầu chạy tuần tự 1 luồng: ước lượng khoảng 6 phút (bộ đệm nóng) tới khoảng 2,6 giờ (đọc lạnh khoảng 10 file/s như lượt `DlpBaseline` lạnh).
- Watcher trên cả ổ C:\ có nhiều thay đổi từ AppData; tràn buffer thì phải quét lại 1,45 triệu mục, và lượt quét lại cũng dừng ở thư mục bị cấm đầu tiên.
- File "độc" làm crash tiến trình (lỗi native trong Tika, tràn ngăn xếp) sẽ gây vòng lặp crash, vì `file_state` chưa được ghi nên lần khởi động sau lại đọc đúng file đó.
- Parser bị treo bên trong 1 block sẽ giữ chỗ mãi (timeout chỉ kiểm tra giữa các block).

### Kết quả chính (trong tiến trình, 6 luồng) — chi tiết `docs/so-sanh-bo-doc-6-luong.md`
- .NET: 835 file/s, CPU 18 % (4,3 ms/file), RAM khoảng 125 MB.
- Tika: 532 file/s, CPU 27 % (10 ms/file), RAM khoảng 240 MB.
- Accuracy 81,6 % so với 81,7 %, F1 nhạy cảm 93,7 % cả hai. Khác nhau 105 file, đều là docx.
- Magic bytes: 0 / 25.486 file đổi mức.
- Ghi chú đo: `DlpBaseline` lượt .NET đọc file lạnh từ đĩa (khoảng 90 ms/file do I/O, có thể do Defender), nên thời gian của lượt đó **không** dùng để so; thời gian lấy từ bench (đã đọc trước file).
