# So sánh bộ đọc .NET (có magic bytes) và Tika native (đã tối ưu) ở giới hạn 6 file đồng thời

- **Ngày đo:** 2026-10-29. Máy i7-13700H (20 luồng logic), cắm sạc; tiến trình đo đã tắt hạ xung EcoQoS (xem `docs/buoi-15-tom-tat.md`).
- **Bộ đọc .NET:** OpenXml / PdfPig / ExcelDataReader + `FileTypeSniffer` (magic bytes).
- **Tika native:** chế độ Replace, DLL `9be007f9…` (bản tối ưu buổi 14: C1–C3, 2a), 2 isolate.
- **Giới hạn đồng thời:** `Watch:MaxConcurrency = 6`. Đây là mức chọn từ benchmark 1–64 luồng: thông lượng bão hoà từ 6, độ trễ thấp nhất trong vùng bão hoà.
- **Phân loại nguồn số liệu:**
  - [TT] = đo trong tiến trình (pipeline thật của service, không qua watcher). ✅ Đã đo.
  - [SV] = đo trên service thật bằng `tools/perf/Run-ReaderComparisonE2E.ps1` (cần quyền Admin). ⏳ Chờ chạy.

## 1. Tóm tắt

| Chỉ số (6 luồng) | Bộ đọc .NET | Tika native | Bên tốt hơn |
|---|---|---|---|
| Thông lượng [TT] | **835 file/s** (865 / 805) | 532 file/s (529 / 534) | .NET, nhanh gấp **1,57 lần** |
| Độ trễ 1 file p50 / p95 [TT] | **5,9 / 19,2 ms** | 11,4 / 20,4 ms | .NET (p50); p95 ngang nhau |
| CPU TB (% toàn máy) [TT] | **18 %** | 27 % | .NET |
| CPU cho 1 file [TT] | **4,3 ms** | 10,0 ms | .NET, ít hơn **2,3 lần** |
| RAM TB / đỉnh [TT] | **124 / 130 MB** | 237 / 271 MB | .NET, ít hơn khoảng một nửa |
| 16 PDF lớn (1,5 triệu ký tự) [TT] | 8,9 s, RAM đỉnh **190 MB** | **8,5 s**, RAM đỉnh 392 MB | Ngang thời gian; .NET ít RAM hơn |
| Accuracy 4 mức (25.486 file) | 81,6 % | 81,7 % | Ngang (lệch 0,1 điểm) |
| F1 "nhạy cảm" / bỏ sót (FN) | 93,7 % / 540 | 93,7 % / 529 | Ngang (Tika bỏ sót ít hơn 11 / 12.798 file) |
| Đọc lỗi | 0 | 0 | — |
| Định dạng cũ .doc / .ppt / .rtf | ❌ Không đọc được | ✅ | Tika |
| File đổi đuôi (corpus 6 file) | ✅ 6/6 (magic bytes) | ✅ 6/6 (tự nhận dạng) | Ngang |

## 2. Thời gian xử lý [TT]

**Cách đo:** `DlpConcurrencyBench --levels 6 --bigpdf-levels 6`.
- 4.000 file rawdata (16 lớp × docx/pdf, chọn cố định). Mỗi file đi qua tra cache → trích → băm → quét → phân loại → ghi DB.
- Chạy xen kẽ L1 → T1 → T2 → L2 để trung hoà ảnh hưởng của thứ tự chạy. Mọi file được đọc trước vào bộ đệm Windows.
- Kết quả thô: `tools/perf/results/concurrency-20261029-*-cmp6-{L1,T1,T2,L2}.*`.

| Lượt | Cả lô 4.000 file | File/s | Tổng/file p50 / p95 / p99 / max (ms) | Trích TB | Quét TB | Ghi DB TB |
|---|---|---|---|---|---|---|
| .NET L1 | 4,6 s | 865 | 5,7 / 19,2 / 25,7 / 61 | 4,4 | 1,6 | 0,7 |
| .NET L2 | 5,0 s | 805 | 6,1 / 19,2 / 27,7 / 91 | 4,7 | 1,7 | 0,8 |
| Tika T1 | 7,6 s | 529 | 11,4 / 20,1 / 30,1 / 53 | 9,4 | 1,3 | 0,5 |
| Tika T2 | 7,5 s | 534 | 11,3 / 20,6 / 30,4 / 73 | 9,4 | 1,2 | 0,5 |

**Theo định dạng** (tổng thời gian/file p50 / p95, ms):

| Định dạng | .NET (L1 / L2) | Tika (T1 / T2) |
|---|---|---|
| docx | **5,4 / 13,8** ; 5,7 / 12,4 | 13,7 / 22,9 ; 13,4 / 23,2 |
| pdf | 6,0 / 21,1 ; 6,6 / 21,8 | 7,1 / **13,0** ; 7,0 / **14,9** |

- Tika chậm chủ yếu ở **docx** (trích 12,7 ms so với 3,8 ms); với pdf, Tika có p95 tốt hơn (13–15 so với 21–22 ms).
- Quét regex và ghi DB như nhau ở 2 bộ đọc (cùng code).
- Hai lượt Tika rất ổn định (lệch 1 %); hai lượt .NET lệch 7 %, vẫn trong mức nhiễu đã biết của máy (12–25 %).

## 3. Tài nguyên hệ thống [TT]

| Lượt | CPU TB / đỉnh (250 ms) | Lõi dùng | CPU/file | RAM TB / đỉnh | Private đỉnh | Thread đỉnh | GC .NET dừng |
|---|---|---|---|---|---|---|---|
| .NET L1 | 18 % / 25 % | 3,7 | 4,2 ms | 125 / 131 MB | 72 MB | 18 | 20,9 % |
| .NET L2 | 17 % / 22 % | 3,5 | 4,3 ms | 122 / 128 MB | 72 MB | 20 | 21,9 % |
| Tika T1 | 27 % / 29 % | 5,4 | 10,2 ms | 240 / 274 MB | 283 MB | 23 | 1,3 % |
| Tika T2 | 26 % / 30 % | 5,3 | 9,8 ms | 234 / 267 MB | 277 MB | 23 | 1,3 % |

16 PDF lớn ở 6 luồng:

| Bộ đọc | Thời gian | Độ trễ TB / max | CPU TB | RAM đỉnh | Private đỉnh |
|---|---|---|---|---|---|
| .NET | 8,4 / 9,5 s | 2,9 / 3,6–3,9 s | 12 % | 188–192 MB | 139–140 MB |
| Tika | 8,0 / 9,0 s | 2,7–3,1 s / 3,1–3,7 s | 22 % | 390–394 MB | 404–407 MB |

- **Tika tốn CPU gấp khoảng 2,3 lần cho mỗi file.** Lý do: mã biên dịch AOT của GraalVM chậm hơn JIT (H5) và có Serial GC riêng của Java (H6), xem buổi 14.
- **RAM:** Tika cần thêm khoảng 110–140 MB cho heap của 2 isolate, khoảng 200 MB khi đọc PDF lớn. "GC .NET dừng" của Tika thấp chỉ vì phần lớn bộ nhớ nằm ngoài .NET (heap Java), không có nghĩa là Tika không tốn GC.
- Với giới hạn 6, đỉnh RAM của cả 2 bộ đọc bị chặn: .NET dưới 200 MB, Tika dưới 410 MB, kể cả khi gặp nhiều PDF lớn cùng lúc. Trước khi có giới hạn, 16 PDF lớn cùng lúc làm RAM lên 653 MB với .NET.

## 4. Độ chính xác (25.486 file rawdata, `DlpBaseline`)

Kết quả thô: `tools/eval/results/baseline-20261029-152222-legacy.*`, `baseline-20261029-160426-tika.*`.

| Bộ đọc | Accuracy | Macro-F1 | Precision / Recall / F1 "nhạy cảm" | FP / FN | docx / pdf accuracy | Đọc lỗi |
|---|---|---|---|---|---|---|
| .NET + magic bytes | 81,6 % | 81,7 % | 91,7 / 95,8 / 93,7 % | 1.116 / 540 | 81,9 / 81,3 % | 0 |
| Tika native | 81,7 % | 81,7 % | 91,6 / 95,9 / 93,7 % | 1.120 / 529 | 82,1 / 81,3 % | 0 |

- Hai bộ đọc cho kết quả khác nhau ở **105 / 25.486 file (0,4 %), toàn bộ là docx**. Kiểu khác biệt nhiều nhất: Internal → Public (42 file), Confidential → Restricted (25 file).
- **Magic bytes không làm đổi kết quả:** so bộ đọc .NET trước và sau khi thêm magic bytes, 0 / 25.486 file đổi mức hoặc đổi trạng thái đọc.
- Độ chính xác không phụ thuộc số luồng: bench kiểm 0 file lệch kết quả ở mọi mức 1–64.

## 5. Trên service thật [SV] — ⏳ chờ chạy

Lệnh (PowerShell **quyền Administrator**, đứng ở `D:\Service-DLP`, khoảng 40 phút):

```powershell
.\tools\perf\Run-ReaderComparisonE2E.ps1
```

Script sẽ làm các bước sau:
1. Publish bản hiện tại (magic bytes + giới hạn 6).
2. Với từng bộ đọc: đổi cấu hình, khởi động lại service, chờ "Đang theo dõi", rồi chạy `Measure-DlpServiceE2E.ps1 -FastDrop -RateSteps 5,20,50 -IdleAfterSec 300`. Kịch bản gồm: làm nóng, 64 file tuần tự, tải đều 5/20/50 file/s, burst 25.486 file, rảnh 5 phút. Kết quả gồm thời gian từng file (`[Perf]`), độ trễ đầu-cuối, độ chính xác từ log, CPU/RAM của tiến trình DlpService mỗi giây.
3. Phân tích bằng `Analyze-DlpServiceE2E.py`.
4. Cuối cùng khôi phục cấu hình gốc (+ `Watch:MaxConcurrency = 6`) và trạng thái LlmSvc.

Danh sách lượt đo được ghi vào `tools/perf/results/readercmp-<giờ>.txt`.

## 6. Nhận xét và đề xuất

1. **Hiệu năng và tài nguyên: bộ đọc .NET tốt hơn rõ rệt** ở 6 luồng: nhanh gấp 1,57 lần, CPU mỗi file ít hơn 2,3 lần, RAM khoảng một nửa.
2. **Độ chính xác: ngang nhau** (chênh 0,1 điểm, 11 file bỏ sót trên 12.798 file nhạy cảm, nằm trong mức không phân biệt được).
3. **Tika chỉ hơn ở chỗ đọc được .doc / .ppt / .rtf** và có độ trễ p95 với pdf tốt hơn.
4. Đề xuất cần quyết định (sau khi có số [SV]): dùng **bộ đọc .NET cho 8 định dạng chính + Tika chế độ Supplement cho .doc / .ppt / .rtf**. Cách này giữ khả năng đọc định dạng cũ, đổi lại vẫn phải nạp DLL (khoảng +100 MB RAM nền). Nếu không cần định dạng cũ thì tắt hẳn Tika.
