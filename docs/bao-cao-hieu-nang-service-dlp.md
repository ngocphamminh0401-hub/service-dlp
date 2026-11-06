# Báo cáo hiệu năng và hoạt động của service DLP (lớp regex)

- **Ngày đo:** 26/10/2026 (cùng các mốc so sánh 21–22/10/2026, ghi rõ ở từng bảng)
- **Đối tượng đánh giá:** `DlpService` chạy dưới Service Control Manager, **chỉ lớp regex**
- **Phạm vi:** 5 hạng mục đánh giá (thời gian, độ chính xác, kích thước/định dạng, tài nguyên, xử lý đồng thời) 

---

## 0. Tóm tắt

**Cấu hình đề xuất cho service: 
- Phương án 1 (P/A 1).** Bộ đọc hiện có xử lý 8 định dạng chính (txt, csv, docx, pptx, xlsx, xls,
pdf, eml), Tika native chỉ đọc thêm cac dinh dang chua co. 
- Phuong an 2 (P/A 2): trich text toàn bộ bằng Tika. 

### Kết quả 5 hạng mục (P/A 1)

| # | Hạng mục | Kết quả chính | Đánh giá |
|---|---|---|---|
| 1 | Thời gian xử lý | Mỗi file trong service: **80–85 ms** khi tuần tự, **308–337 ms** khi burst (khoảng 25 file song song). Từ lúc thả file tới khi có kết quả: **≈ 0,43 s** (gồm 300 ms chờ file ghi xong). 25.486 file xong trong **5,6–7,0 phút** | ✅ Đạt; thông lượng bị giới hạn bởi tốc độ thả file |
| 2 | Độ chính xác | Accuracy 4 mức **81,6 %**, Macro-F1 **81,7 %**. Phát hiện "nhạy cảm": P **91,7 %** / R **95,8 %** / F1 **93,7 %**. Lớp yếu nhất là `healthcare` (74,3 %) | ⚠️ Đạt mức nền regex; healthcare và lớp Internal cần cải thiện |
| 3 | Kích thước / định dạng | Định dạng và kích thước tới 1,5 triệu ký tự **không làm đổi mức** (0 % file đổi mức). 1,5 triệu ký tự mất < 1 s/file. **Điểm mù:** phần nằm sau ký tự thứ 2.000.000 không được quét, 70,5 % file nhạy cảm bị hạ mức | ⚠️ Cần xử lý file rất dài |
| 4 | Tài nguyên | CPU service TB **6–7 %** của máy khi burst (đỉnh 15 %), **≈ 13–15 ms CPU/file**. RAM **≈ 50–80 MB** lúc nền, đỉnh **185 MB** khi burst; rảnh 30 phút không thấy rò rỉ | ✅ Đạt (RAM nền của P/A 1 là ước tính, xem 6.5) |
| 5 | Xử lý đồng thời | 0 lỗi, 0 lệch kết quả từ 1 tới 64 luồng; thông lượng đỉnh ở **4–8 luồng**. Thả cùng lúc tới 5.000 file: **0 mất, 0 trùng, 0 tràn buffer**. **File bị khoá không được thử lại** | ⚠️ Đạt, trừ lỗi file bị khoá |

### So sánh nhanh hai phương án

| Chỉ số | P/A 1 (bộ đọc cũ + Tika cho định dạng cũ) | P/A 2 (toàn bộ Tika) |
|---|---|---|
| Accuracy / F1 "nhạy cảm" | 81,6 % / 93,7 % | 81,7 % / 93,7 % |
| Trích text TB/file, file đã nằm trong bộ đệm, 1 luồng | **2,1–2,8 ms** | 10,8 ms |
| Tổng thời gian TB/file trong service khi burst | **308–337 ms** | 446–452 ms |
| CPU / file trong service | **≈ 13–15 ms** | ≈ 41–42 ms |
| RAM nền / đỉnh khi burst (service) | **≈ 50–80 / 138–185 MB** | 129–134 / 242–262 MB |
| Lỗi riêng | không có | PDF > 2 triệu ký tự bị cắt **không cảnh báo**; file bị khoá báo nhầm là "hỏng"; mất file Windows-1258 (đo ở báo cáo trước) |

### Rủi ro cần xử lý (cả hai phương án)
1. **File đọc lỗi không để lại kết quả:** service chỉ ghi 1 dòng cảnh báo, không lưu bản ghi, không đánh dấu cần xem lại.
2. **File bị khoá lúc đọc không được thử lại:** bị bỏ sót tới lần khởi động lại sau.
3. **File dài hơn 2.000.000 ký tự chỉ được quét phần đầu.**
4. **Service không giới hạn số file xử lý cùng lúc:** khi tải cao, khoảng 19–32 file chạy song song, độ trễ tăng do tranh tài nguyên.

---

## 1. Bối cảnh và phạm vi

**Luồng xử lý của service:** watcher phát hiện file → chờ 300 ms cho file ghi xong (debounce) → tra cache → trích text → băm
nội dung → quét regex (222 pattern) → phân loại 4 mức (Public / Internal / Confidential / Restricted) + điểm tin cậy → ghi SQLite.

**Hai phương án trích text được so sánh:**

| | Cấu hình | Đọc 8 định dạng chính bằng | Đọc `.doc/.ppt/.rtf` bằng |
|---|---|---|---|
| **P/A 1** | `TikaNative: Enabled=true, Mode=Supplement` | Bộ đọc .NET hiện có (OpenXML, PdfPig, ExcelDataReader, MIME parser) | Tika native |
| **P/A 2** | `TikaNative: Enabled=true, Mode=Replace` | Tika native (Apache Tika biên dịch bằng GraalVM thành `tika_native.dll`) | Tika native |

**Ngoài phạm vi:** lớp LLM (`LlmSvc`), PDF scan cần OCR, file có mật khẩu.

---

## 2. Phương pháp

### 2.1 Môi trường
Intel i7-13700H (20 luồng logic), RAM 13,7 GB, Windows 11, .NET 10.0.12, cắm sạc, chế độ nguồn Balanced. Tika native: GraalVM CE
25.0.2, Serial GC, heap tối đa 256 MB, DLL SHA-256 `23b995a9…eb96949`.

### 2.2 Dữ liệu (toàn bộ là dữ liệu tổng hợp, không có dữ liệu thật)

| Bộ | Quy mô | Dùng cho |
|---|---|---|
| rawdata có nhãn (`D:\dlp-classifier\rawdata`) | 12.743 mẫu × (docx + pdf) = **25.486 file**, 1,7 GB, 16 lớp (4 domain × 4 nhãn) | Hạng mục 1, 2, 4, 5 |
| size-corpus (`tools/eval/size-corpus`) | **1.568 file** = 64 mẫu có nhãn × 7 định dạng × 5 bậc kích thước | Hạng mục 3 |
| Tập con 4.000 file + 16 PDF lớn | lấy đều 16 lớp × 2 định dạng, seed cố định | Hạng mục 5 (trong tiến trình) |
| 50 file `.doc/.ppt/.rtf` thật (`tools/tika-bench/results/real2-list.txt`) | 4 doc, 6 ppt, 40 rtf | Phần Tika của P/A 1 |

### 2.3 Hai lớp đo

| Lớp đo | Công cụ | Trả lời câu hỏi |
|---|---|---|
| **Trong tiến trình**: gọi trực tiếp đúng các class của service, không có watcher | `DlpBaseline`, `DlpSizeBench`, `DlpConcurrencyBench` | Độ chính xác; so sánh tương đối giữa các bộ đọc; khả năng mở rộng khi đặt chính xác N luồng |
| **Service thật**: chạy dưới SCM (LocalSystem), script đóng vai người dùng thả file vào `watch-folder` | `tools/perf/Measure-DlpServiceE2E.ps1` + `Analyze-DlpServiceE2E.py` | Thời gian đầu cuối, CPU/RAM thật, tải đều, lô lớn, khởi động lại, độ ổn định |

Hai lớp đo cho **cùng kết quả phân loại**: service thật trùng mức với `DlpBaseline` trên 25.486/25.486 file (P/A 1, lượt 21/10).
Với P/A 2 hôm nay, ma trận nhầm lẫn của service giống hệt của `DlpBaseline`.

### 2.4 Đảm bảo công bằng
- Cùng bản code; chỉ đổi cấu hình bộ đọc (tham số `--extractor legacy|supplement|tika` của công cụ đo, `TikaNative:Mode` của service).
- Các lượt chạy lần lượt, không chạy chồng lên nhau.
- **A-B-B-A cho bộ đệm file:** lượt nào chạy sau sẽ đọc file từ RAM (bộ đệm của Windows) nên nhanh hơn rõ rệt. Ví dụ cùng bộ đọc
  Tika: 807 giây khi đọc từ đĩa so với 449 giây khi file đã có trong bộ đệm. Vì vậy mỗi bộ đọc được đo cả lượt **lạnh** lẫn lượt
  **nóng**, và chỉ so lạnh với lạnh, nóng với nóng. `DlpConcurrencyBench` tự đọc trước mọi file, nên luôn là "nóng".

### 2.5 Giới hạn của phép đo (đọc trước khi dùng số liệu)
1. **Mỗi cấu hình chạy một lần, trên một máy.** Nhiễu giữa các lượt **tới khoảng 25 %**: P/A 1 và bộ đọc cũ chạy cùng code đọc
   docx/pdf, nhưng thông lượng đỉnh đo được là 910 so với 726 file/giây.
2. **Số đo trên service của P/A 1 lấy từ lượt 21–22/10, khi Tika đang tắt.** Phần đọc 8 định dạng chính giống hệt, nên thời gian,
   CPU và độ chính xác đại diện được. **RAM nền thì không**: P/A 1 nạp `tika_native.dll` khi khởi động, nên RAM nền thực tế cao hơn
   (ước tính ở 6.5). Lượt 21–22/10 chạy trong điều kiện máy khác hôm nay (tốc độ copy, RAM trống), nên chênh lệch nhỏ hơn khoảng
   20 % giữa P/A 1 và P/A 2 trên service chưa nên coi là có ý nghĩa.
3. Bộ đếm cấp phát bộ nhớ của .NET **không tính heap native của Tika**. Muốn so RAM thì dùng working set, không dùng số "cấp phát".
4. Bộ rule được tinh chỉnh theo cách bộ đọc cũ trình bày text (ví dụ ô bảng nối bằng ` | `). Đây là một điểm có lợi nhẹ cho P/A 1.
5. Công cụ đo tính **file đọc lỗi là Public**. Trong các lượt này không có file nào đọc lỗi, nên số liệu không bị ảnh hưởng (xem mục 9).

---

## 3. Hạng mục 1: Thời gian xử lý

### 3.1 Từng file, trong tiến trình (25.486 file, 1 luồng)

| Cấu hình | Điều kiện | Trích text TB / p50 / p95 (ms), docx | pdf | Tổng cả vòng |
|---|---|---|---|---|
| **P/A 1** (bộ đọc cũ) | nóng | **10,4 / 2,6 / 35,9** | **10,8 / 3,0 / 36,9** | **301 s** |
| P/A 2 (Tika) | nóng | 19,5 / 11,7 / 49,7 | 13,4 / 6,2 / 42,2 | 449 s |
| **P/A 1** (bộ đọc cũ) | lạnh | 23,4 / 27,8 / 38,4 | 23,1 / 25,0 / 41,8 | 636 s |
| P/A 1 (có nạp DLL) | lạnh | 25,8 / 28,8 / 38,7 | 25,3 / 26,9 / 41,3 | 693–697 s |
| P/A 2 (Tika) | lạnh | 33,3 / 35,9 / 50,8 | 27,6 / 29,4 / 48,2 | 807 s |

Quét regex mất khoảng 1,2–1,7 ms/file, phân loại < 0,1 ms. **Trích text chiếm khoảng 93 % thời gian.** Khi đọc từ đĩa, thời gian bị
chi phối bởi đĩa (khoảng 25 ms/file với cả hai bộ đọc).

### 3.2 Từng file, bên trong service (dòng `[Perf]`, không gồm debounce)

| Bước (ms, TB) | P/A 1 tuần tự | P/A 1 burst | P/A 2 tuần tự | P/A 2 burst |
|---|---|---|---|---|
| Tra cache DB | 0,3–0,5 | 2,8–14,2 | 0,4–0,5 | 2,2–2,3 |
| **Trích text** | **67,0–71,8** | **215–235** | 88,4–90,1 | 402–404 |
| Quét regex | 7,9–8,8 | 19,9–28,7 | 7,8–8,4 | 19,9–20,2 |
| Phân loại + S1 + độ tin cậy | 1,6–1,8 | 4,1–6,0 | 1,6 | 3,5 |
| Ghi DB | 2,4 | 53,9–64,1 | 2,4–2,7 | 17,7–20,9 |
| **Tổng TB** | **79,6–85,1** | **308–337** | 101,3–102,7 | 446–452 |
| **Tổng p95** | 122–150 | 628–695 | 149–162 | 755–783 |

*Nguồn: P/A 1 = lượt `20261021-150346`, `20261022-143106`; P/A 2 = lượt `20261026-094454`, `20261026-095603`.*

Bước trích text trong service (67 ms) chậm hơn nhiều so với trong tiến trình (2,6 ms khi nóng). Một phần nguyên nhân là Windows
Defender quét file khi service mở file (CPU của Defender tăng theo tải, xem 6.1).

*Cập nhật 27/10/2026, **đã kiểm chứng** bằng recording của Defender (`Get-MpPerformanceReport`, 100 file docx/pdf mỗi kịch bản):*

- Khi file được **copy** vào, Defender **bỏ qua** việc quét lúc ghi (`OnClose`, lý do `Optimization`, khoảng 0,5 ms). Lần quét thật
  diễn ra khi **service mở file lần đầu** (`OnOpen`): trung bình 23–31 ms/file (pdf khoảng 29 ms, docx khoảng 17 ms). Mỗi bản copy bị
  quét **đúng 1 lần**, kể cả khi file gốc đã được quét trước đó, vì kết quả quét không đi theo bản copy.
- Điều này **chỉ đúng với file copy**. File được ứng dụng ghi hoặc lưu thì Defender quét ngay lúc ghi, nên lúc service mở thì không
  bị quét nữa. Script E2E thả file bằng copy, nên chi phí Defender trong báo cáo này là **trường hợp xấu nhất**.
- Copy ra thư mục tạm, chờ rồi move vào watch-folder **không** tránh được lần quét này, vì lúc copy Defender chưa quét thật.
- Mở lại file chưa thay đổi thì Defender dùng cache (đọc lại 1.000 file: 28,1 s xuống 0,1 s).
- Defender giải thích khoảng **20–30 ms** trong phần chênh lệch nói trên. Phần còn lại thuộc chính bước trích xuất.
- Đo lại: `.\tools\perf\Measure-DefenderOverhead.ps1 -Record -StopLlmSvc` (PowerShell quyền Administrator).
- Chi tiết đầy đủ (các tình huống ghi file, CPU và RAM tính cả Defender, phương án xử lý): [bao-cao-anh-huong-defender.md](bao-cao-anh-huong-defender.md).

### 3.3 Độ trễ đầu cuối khi tuần tự (thả 1 file, chờ có kết quả)

| | TB | p50 | p95 | Max |
|---|---|---|---|---|
| **P/A 1** | **425–443 ms** | 422–436 | 470–489 | 526–594 |
| P/A 2 | 444–449 ms | 440–441 | 501–516 | 519–607 |

Gồm 300 ms debounce cố ý, khoảng 80–100 ms xử lý, và thời gian copy.

### 3.4 Toàn bộ pipeline: 25.486 file (1,7 GB)

| Lượt | Thời gian thả file | **Tới khi file cuối có kết quả** | Thông lượng | Số file xử lý song song TB |
|---|---|---|---|---|
| P/A 1, copy (21/10) | 415,7 s | **419,5 s** | 60,8 file/s | 18,7 |
| P/A 1, thả nhanh (22/10) | 333,3 s | **337,9 s** | 75,4 file/s | 25,4 |
| P/A 2, copy (26/10) | 357,5 s | 375,5 s | 67,9 file/s | 30,3 |
| P/A 2, thả nhanh (26/10) | **169,1 s** | 364,2 s | **70,0 file/s** | 31,6 |

Ở 3 lượt đầu, file cuối có kết quả chỉ 4–18 giây sau khi thả xong. Tức là **tốc độ thả file là giới hạn**, chưa phải năng lực của
service. Lượt cuối thả xong trong 169 giây nhưng service cần 364 giây, cho thấy **năng lực của P/A 2 khoảng 70 file/giây**. Năng
lực của P/A 1 ít nhất là 75 file/giây (chưa chạm trần). Khi thả cả lô 5.000 file cùng lúc (mục 7.3): P/A 1 đạt 71,5 file/giây,
P/A 2 đạt 66,7 file/giây.

---

## 4. Hạng mục 2: Độ chính xác phân loại

Nhãn → mức: public → Public, internal → Internal, confidential → Confidential, top_secret → Restricted. "Nhạy cảm" = Confidential + Restricted.

### 4.1 Tổng thể (25.486 file)

| Chỉ số | **P/A 1** | P/A 2 |
|---|---|---|
| **Accuracy (4 mức)** | **81,6 %** | 81,7 % |
| Macro Precision / Recall / **F1** | 81,9 / 81,6 / **81,7 %** | 82,0 / 81,7 / 81,7 % |
| Weighted Precision / Recall / F1 | 82,0 / 81,6 / 81,7 % | 82,0 / 81,7 / 81,8 % |
| **"Nhạy cảm" Precision / Recall / F1** | **91,7 / 95,8 / 93,7 %** | 91,6 / 95,9 / 93,7 % |
| TP / FP (báo nhầm) / FN (bỏ sót) / TN | 12.258 / 1.116 / 540 / 11.572 | 12.269 / 1.120 / 529 / 11.568 |
| Phát hiện PII: Precision / Recall / F1 | 76,0 / 93,4 / 83,8 % | 76,0 / 93,4 / 83,8 % |
| File đọc lỗi / quét chưa trọn | 0 / 0 | 0 / 0 |

### 4.2 Theo 4 mức (P/A 1)

| Mức | Số file thật | Precision | Recall | F1 |
|---|---|---|---|---|
| Public | 6.400 | 87,0 % | 78,8 % | 82,7 % |
| Internal | 6.288 | 78,2 % | 78,5 % | 78,4 % |
| Confidential | 6.400 | 75,6 % | 84,3 % | 79,7 % |
| Restricted | 6.398 | 87,0 % | 84,8 % | 85,9 % |

Ma trận nhầm lẫn (hàng = nhãn thật, cột = dự đoán):

| Thật \ Dự đoán | Public | Internal | Confidential | Restricted |
|---|---|---|---|---|
| Public | **5.041** | 952 | 389 | 18 |
| Internal | 641 | **4.938** | 541 | 168 |
| Confidential | 53 | 328 | **5.394** | 625 |
| Restricted | 61 | 98 | 811 | **5.428** |

Nhầm lẫn chủ yếu xảy ra giữa **hai mức kề nhau**: Public ↔ Internal, và Confidential ↔ Restricted (811 file Restricted bị đoán là
Confidential). Chỉ 114 file nhạy cảm (0,9 %) bị đoán thấp xuống Public.

### 4.3 Theo 16 lớp dữ liệu

| Lớp | Số file | P/A 1: Precision / Recall / F1 | P/A 2: Recall (= accuracy của lớp) |
|---|---|---|---|
| finance_banking__public | 1.600 | 92,2 / 73,2 / 81,6 % | 73,4 % |
| finance_banking__internal | 1.600 | 78,3 / 86,1 / 82,0 % | 86,1 % |
| finance_banking__confidential | 1.600 | 79,8 / 94,4 / 86,5 % | 94,5 % |
| finance_banking__top_secret | 1.600 | 95,9 / 88,7 / 92,2 % | 90,1 % |
| healthcare__public | 1.600 | 85,6 / 88,2 / 86,9 % | 88,5 % |
| **healthcare__internal** | 1.600 | 81,5 / **61,9** / **70,3 %** | 60,6 % |
| **healthcare__confidential** | 1.600 | 66,0 / **63,9** / **64,9 %** | 64,0 % |
| healthcare__top_secret | 1.600 | 67,2 / 83,3 / 74,4 % | 83,6 % |
| hr__public | 1.600 | 81,6 / 76,1 / 78,7 % | 76,2 % |
| hr__internal | 1.488 | 76,2 / 84,1 / 80,0 % | 84,0 % |
| hr__confidential | 1.600 | 77,1 / 86,9 / 81,7 % | 87,0 % |
| hr__top_secret | 1.598 | 97,9 / 82,6 / 89,6 % | 82,7 % |
| insurance__public | 1.600 | 89,6 / 77,6 / 83,1 % | 77,7 % |
| insurance__internal | 1.600 | 77,5 / 82,4 / 79,9 % | 82,3 % |
| insurance__confidential | 1.600 | 77,9 / 91,9 / 84,3 % | 91,9 % |
| insurance__top_secret | 1.600 | 95,1 / 84,8 / 89,6 % | 85,0 % |

| Nhóm (P/A 1) | Số file | Accuracy | Macro-F1 |
|---|---|---|---|
| Domain finance_banking | 6.400 | 85,6 % | 85,6 % |
| **Domain healthcare** | 6.400 | **74,3 %** | 74,1 % |
| Domain hr | 6.286 | 82,4 % | 82,5 % |
| Domain insurance | 6.400 | 84,2 % | 84,2 % |
| Định dạng docx / pdf | 12.743 / 12.743 | 81,9 / 81,3 % | 82,0 / 81,3 % |
| Ca khó (borderline) / ca thường | 3.060 / 22.426 | 71,0 / 83,1 % | 71,1 / 83,1 % |

**Điểm yếu cần ưu tiên:** domain healthcare (Internal và Confidential chỉ khoảng 62–64 % recall) và ca khó (71 %). Đây là giới hạn
của bộ rule regex, không phụ thuộc vào bộ đọc. Đây cũng là nhóm file mà lớp LLM được thiết kế để thẩm định.

### 4.4 Service thật so với đo trong tiến trình
- P/A 1 (lượt 21/10): service cho cùng mức với `DlpBaseline` trên **25.486/25.486 file (100 %)**.
- P/A 2 (lượt 26/10): ma trận nhầm lẫn của service **giống hệt** `DlpBaseline` chạy Tika.
- Kết quả phân loại có tính tất định: chạy lại cùng cấu hình cho **0 file khác mức**.

### 4.5 So sánh từng file giữa hai phương án
- Cùng mức: **25.381 / 25.486 file (99,6 %)**. Khác mức: 94 file docx và 11 file pdf.
- Trong số file khác mức: **chỉ P/A 1 đúng 38 file, chỉ P/A 2 đúng 60 file**. Chênh lệch nhỏ, không có ý nghĩa thống kê. Nguyên nhân
  là **cách trình bày text**: bộ đọc cũ nối ô bảng trên cùng một dòng, Tika đặt mỗi ô một dòng; bộ đọc cũ cho PDF chèn nhãn trang
  vào giữa câu.
- P/A 1 có nạp DLL so với bộ đọc cũ không có DLL: **0 file khác mức, 0 file khác số chỗ khớp**.

---

## 5. Hạng mục 3: Ảnh hưởng của kích thước và định dạng file

### 5.1 Bộ dữ liệu
64 mẫu có nhãn (4 mẫu mỗi lớp), được chèn thêm văn bản trung tính (đã kiểm tra: 0 chỗ khớp rule, không chứa từ khoá) để đạt các bậc
kích thước, ghi ra 7 định dạng. Ở bậc `3M-cuoi`, nội dung gốc nằm **ở cuối file**, sau ký tự thứ 2.000.000.

| Bậc | Số file | Ký tự trích được TB | Kích thước TB: txt / docx / pdf / eml |
|---|---|---|---|
| goc (nguyên bản) | 448 | ≈ 1.900 | 2,3 / 2,2 / 46 / 4,5 KB |
| 20k | 448 | 20.400 | 26 / 4,5 / 64 / 50 KB |
| 200k | 448 | 201.600 | 260 / 11 / 221 / 495 KB |
| 1.5M | 112 | 1.510.000 | 1,9 MB / 57 KB / 1,3 MB / 3,7 MB |
| 3M-cuoi | 112 | 2.000.000 (bị cắt) | 3,9 MB / 109 KB / 2,7 MB / 7,4 MB |

### 5.2 Thời gian xử lý (ms/file TB, trích + quét + phân loại, lượt nóng)

| Bậc | txt | csv | docx | xlsx | pptx | pdf | eml | P/A 2: tất cả TB |
|---|---|---|---|---|---|---|---|---|
| goc | 4,1 | 2,9 | 28,8 | 7,5 | 6,5 | 46,1 | 2,5 | 5,5 |
| 20k | 8,3 | 8,9 | 11,3 | 9,3 | 12,2 | 14,5 | 9,7 | 15,6 |
| 200k | 67,4 | 71,3 | 72,0 | 70,0 | 75,1 | 114,8 | 87,9 | 125,4 |
| 1.5M | 509 | 521 | 508 | 532 | 535 | **778** | 525 | 743 (pdf **1.679**) |
| 3M-cuoi | 663 | 677 | 680 | 667 | 732 | **1.035** | 724 | 1.049 (pdf **2.317**) |

*Các cột định dạng là của P/A 1 (bộ đọc cũ, lượt nóng `094240`). Cột cuối là của P/A 2 (lượt nóng `090858`).*

- Thời gian **tăng gần tuyến tính theo số ký tự**. Với file lớn, thời gian chủ yếu nằm ở **bước quét regex: khoảng 33–39 ms trên 100
  nghìn ký tự**, như nhau ở mọi định dạng và cả hai phương án. Bước trích text với file lớn: P/A 1 khoảng 8–10 ms trên 100 nghìn ký
  tự, P/A 2 khoảng 16–17 ms.
- PDF là định dạng chậm nhất. File 1,5 triệu ký tự (khoảng 500 trang) mất 0,78 s với P/A 1 và 1,68 s với P/A 2 (**chậm hơn khoảng 2,2 lần**).
- Ở bậc `goc`, P/A 2 nhanh hơn với file nhỏ (5,5 so với khoảng 15 ms TB), do bộ đọc cũ tốn chi phí khởi tạo cho mỗi file docx/pdf.

### 5.3 Độ chính xác theo kích thước × định dạng (giống nhau ở cả hai phương án)

| Bậc | Accuracy ở mọi định dạng | F1 "nhạy cảm" | % file đổi mức so với `goc` |
|---|---|---|---|
| goc | 87,5 % | 92,6 % | — |
| 20k / 200k | 87,5 % | 92,6 % | 0 % |
| 1.5M | 87,5 % | 95,3 % | 0 % |
| **3M-cuoi** | **25,0 %** | **0 %** | **70,5 % (toàn bộ là hạ mức)** |

- **Định dạng không ảnh hưởng tới độ chính xác:** 7 định dạng cho cùng accuracy ở mọi bậc.
- **Kích thước không ảnh hưởng, miễn là ≤ 2.000.000 ký tự.**
- **Điểm mù:** khi nội dung nhạy cảm nằm sau ký tự thứ 2.000.000, service không quét tới và kết luận sai, dù có ghi cảnh báo "chỉ quét
  2,000,000 ký tự đầu". Lỗi này xảy ra với cả hai phương án.

### 5.4 Bộ nhớ theo kích thước (trong tiến trình, 1 luồng)

| Bậc | P/A 1: working set đỉnh | P/A 1: cấp phát TB / max mỗi file | P/A 2: working set đỉnh |
|---|---|---|---|
| goc | 107 MB | 0,6 / 33 MB | 173 MB |
| 200k | 120 MB | 15 / 67 MB | 207 MB |
| 1.5M | 229 MB | 113 / **490 MB** (pdf) | 216 MB |
| 3M-cuoi | 337 MB | 173 / **656 MB** (pdf) | 224 MB |

Với P/A 1, bộ đọc PDF (PdfPig) **cấp phát tạm tới khoảng 650 MB** cho mỗi PDF lớn. Bộ nhớ này được thu hồi sau mỗi file, nhưng làm
working set tăng lên khoảng 340 MB. P/A 2 có RAM nền cao hơn nhưng ít phình theo kích thước file. P/A 1 có nạp DLL: đỉnh 268–312 MB.

### 5.5 Lỗi đọc và cắt nội dung
- **0 file đọc lỗi** ở cả 1.568 file, cả hai phương án.
- Cảnh báo "nội dung bị cắt": P/A 1 có **112/112** file ở bậc 3M. P/A 2 chỉ có **96/112**: **16 file PDF bị cắt mà không có cảnh
  báo**. Tika tự dừng ở 1.999.999 ký tự (giới hạn bên trong DLL), nên phía C# không biết file đã bị cắt, và service sẽ coi file là
  đã quét trọn.

### 5.6 Định dạng cũ `.doc/.ppt/.rtf` (phần Tika của P/A 1)
50 file thật: **50/50 giống hệt text của Tika chạy trên JVM**. Chạy song song 8 luồng × 150 lượt đọc: 150/150 giống hệt, 0 lỗi.
Thời gian trung vị 1,5 ms (doc, ppt) và 4,8 ms (rtf) (đo ở báo cáo `bao-cao-so-sanh-tika.md`, mục 9.7).

### 5.7 Chưa kiểm tra được ở corpus này
Corpus chỉ có txt/csv UTF-8 và eml có thân dạng plain. Theo `bao-cao-so-sanh-tika.md` (mục 5.2), nếu dùng P/A 2:
- txt/csv lưu bằng **Windows-1258** bị Tika trả về rỗng (mất toàn bộ nội dung);
- **eml** mất tiêu đề, người gửi và thân plain.

Hai lỗi này **không ảnh hưởng P/A 1**, vì bộ đọc cũ xử lý các định dạng đó.

---

## 6. Hạng mục 4: Ảnh hưởng của tài nguyên hệ thống

### 6.1 Trên service, theo giai đoạn (lấy mẫu mỗi khoảng 1 giây; CPU tính theo % của cả máy, 100 % = 20 luồng)

| Giai đoạn | P/A 1: CPU TB / max | P/A 1: RAM đầu → max → cuối | P/A 2: CPU TB / max | P/A 2: RAM đầu → max → cuối |
|---|---|---|---|---|
| Nền (rảnh) | 0,1–0,4 / 0,9–4,5 % | 59–67 → 59–67 → 47–54 MB | 0,0 / 0,1–0,2 % | 135–142 → 135–142 → 125–128 MB |
| Tuần tự | 2,2–2,3 / 4,6–5,2 % | 49–54 → 62–65 MB | 1,1–1,2 / 2,3–2,9 % | 126–130 → 155–158 MB |
| Tải đều 5 file/s | 0,6 / 4,4 % | 57 → 65 MB | 1,1 / 2,3 % | 158 → 164 MB |
| Tải đều 20 file/s | 1,9 / 2,8 % | 64 → 64 MB | 4,8 / 6,4 % | 144 → 144 MB |
| Tải đều 50 file/s | 5,1 / 6,4 % | 62 → 76 MB | 12,3 / 14,7 % | 103 → 189 MB |
| **Burst 25.486 file** | **6,1–7,3 / 12,6–15,1 %** | **62–79 → 138–185 MB** | 16,8–17,7 / 21,8–22,1 % | 154–233 → 242–262 MB |
| Sau tải | 0,0 % | → 67–76 MB | 0,0 % | → 172–174 MB |

*Lượt `150346` (21/10) chưa có các cột Defender, I/O, đĩa (mới thêm từ buổi 13). Ở lượt `143106`, CPU của Defender khi burst là 11,4 %, I/O đọc của service 11,3 MB/s, đĩa bận 7 %.*

### 6.2 CPU trên mỗi file (burst)

| | P/A 1 | P/A 2 |
|---|---|---|
| Thời gian CPU / file | **≈ 12,8–14,6 ms** | ≈ 41–42 ms (**gấp ≈ 2,8–3,3**) |

Cách tính: % CPU TB × 20 luồng × thời gian burst ÷ 25.486 file.

### 6.3 Tải đều (thả đúng N file/giây trong 120 giây)

| Mức | P/A 1: độ trễ đầu cuối p50 / p95 | P/A 1: xử lý trong service TB | P/A 2: độ trễ p50 / p95 | P/A 2: xử lý TB |
|---|---|---|---|---|
| 5 file/s | 464 / 532 ms | 73,5 ms | 473 / 540 ms | 90,5 ms |
| 20 file/s | 451 / 517 ms | 66,0 ms | 469 / 538 ms | 99,7 ms |
| 50 file/s | 507 / 641 ms | 84,1 ms | 501 / 594 ms | 123,7 ms |

Cả hai phương án đều **theo kịp tải tới 50 file/giây** (xử lý xong 100 % file, độ trễ ổn định). Ở 50 file/s, P/A 1 chỉ dùng khoảng
5 % CPU của máy.

### 6.4 Rảnh 30 phút sau tải (kiểm tra rò rỉ)

| | Độ dốc RAM | Độ dốc handle | Độ dốc thread | Xử lý file mới sau khi rảnh |
|---|---|---|---|---|
| P/A 1 | −97,5 MB/giờ | −177/giờ | −5/giờ | 20/20 ✅ |
| P/A 2 | −47,6 MB/giờ | +52/giờ | −4/giờ | 20/20 ✅ |

Độ dốc âm hoặc gần 0 nghĩa là bộ nhớ được trả lại, **không thấy dấu hiệu rò rỉ**. Mức +52 handle/giờ của P/A 2 quá nhỏ, chưa đủ để
kết luận.

### 6.5 RAM nền thực tế của P/A 1 (ước tính)
Lượt service của P/A 1 đo khi Tika tắt. Khi bật Supplement, DLL được nạp lúc khởi động. Đo trong tiến trình, RAM tăng **khoảng 28
MB** (1 luồng: 144 so với 116 MB; kiểm tra `.doc/.ppt/.rtf`: 125 MB). Vì vậy RAM nền của P/A 1 trên service **ước tính khoảng 80–90
MB**, so với 50–60 MB khi tắt Tika và 129–134 MB với P/A 2. **Cần một lượt đo E2E với `Mode=Supplement` để xác nhận** (mục 9).

### 6.6 Trong tiến trình (`DlpConcurrencyBench`, 4.000 file)

| Song song | P/A 1 (bộ đọc cũ): RAM đỉnh | P/A 1 (có DLL): RAM đỉnh | P/A 2: RAM đỉnh |
|---|---|---|---|
| 1 luồng | 116 MB | 144 MB | 176 MB |
| 8 luồng | 135 MB | 164 MB | 252 MB |
| 64 luồng | 175 MB | 200 MB | 323 MB |

---

## 7. Hạng mục 5: Hiệu năng khi xử lý đồng thời

### 7.1 Trong tiến trình, N luồng dùng chung một pipeline (4.000 file, đã đọc trước vào bộ đệm)

| Luồng | P/A 1: file/s (tăng tốc) | P/A 1: độ trễ p50 / p95 | P/A 2: file/s (tăng tốc) | P/A 2: độ trễ p50 / p95 | Lỗi / lệch kết quả / bản ghi DB (cả 3 cấu hình) |
|---|---|---|---|---|---|
| 1 | 216–282 (×1,0) | 3,3–3,9 / 4,9–8,7 ms | 78 (×1,0) | 12,4 / 24,4 ms | 0 / 0 / 4000 |
| 4 | 617–797 (×2,8–2,9) | 4,3–5,7 / 10–12 ms | 280 (×3,6) | 14,7 / 24,5 ms | 0 / 0 / 4000 |
| **8** | **726–910 (×3,2–3,4)** | 7,4–9,2 / 21–25 ms | 396 (×5,1) | 18,3 / 39,3 ms | 0 / 0 / 4000 |
| 16 | 587–787 | 18–25 / 38–50 ms | **447 (×5,8)** | 38,2 / 69,8 ms | 0 / 0 / 4000 |
| 64 | 559–696 | 89–112 / 158–192 ms | 411 | 129 / 370 ms | 0 / 0 / 4000 |

*Khoảng giá trị của P/A 1 = 2 lượt đo (bộ đọc cũ không DLL và có DLL) có cùng code đọc. Độ rộng của khoảng chính là mức nhiễu giữa các lượt.*

- **An toàn đa luồng: đạt.** Từ 1 tới 64 luồng, cả ba cấu hình đều 0 lỗi, 0 file đổi mức so với chạy 1 luồng, DB đủ bản ghi.
- **P/A 1 đạt thông lượng cao nhất ở 4–8 luồng**, rồi giảm khi tăng thêm luồng: ghi DB phải chờ khoá, từ 0,4 ms lên 70–90 ms ở 64
  luồng. P/A 2 tăng tốc tốt hơn theo tỉ lệ (×5,8) nhưng mức tuyệt đối vẫn thấp hơn P/A 1 ở mọi mức.

### 7.2 PDF lớn (16 file × 1,5 triệu ký tự) chạy song song

| Song song | P/A 1: thời gian / RAM đỉnh | P/A 2: thời gian / RAM đỉnh |
|---|---|---|
| 1 | 13,0–13,4 s / 145–172 MB | 23,6 s / 323 MB |
| **4** | **6,6–6,9 s** / 193–230 MB | 9,9 s / 385 MB |
| 16 | 9,7–10,8 s / **552–629 MB** | 10,0 s / 493 MB |

Chạy 16 file PDF lớn cùng lúc **chậm hơn chạy 4 file** và đẩy RAM của P/A 1 lên khoảng 600 MB. Đây là thêm một lý do để giới hạn số
file xử lý đồng thời.

### 7.3 Trên service thật

**Thả cùng lúc một lô N file:**

| Lô | P/A 1: thời gian cả lô | P/A 1: file/s | P/A 1: độ trễ p95 | P/A 2: thời gian cả lô | P/A 2: file/s | P/A 2: độ trễ p95 | Mất / trùng / đọc lỗi / tràn buffer (cả hai) |
|---|---|---|---|---|---|---|---|
| 1 | 0,4 s | — | 0,41 s | 0,5 s | — | 0,50 s | 0 / 0 / 0 / 0 |
| 10 | 0,5 s | 18,7 | 0,53 s | 0,5 s | 18,5 | 0,54 s | 0 / 0 / 0 / 0 |
| 100 | 1,7 s | 59,1 | 1,30 s | 1,7 s | 57,8 | 1,31 s | 0 / 0 / 0 / 0 |
| 1.000 | 14,8 s | 67,8 | 11,8 s | 16,6 s | 60,4 | 15,4 s | 0 / 0 / 0 / 0 |
| 5.000 | 69,9 s | **71,5** | 42,7 s | 74,9 s | 66,7 | 56,1 s | 0 / 0 / 0 / 0 |

**Các tình huống đặc biệt:**

| Kiểm tra | P/A 1 | P/A 2 |
|---|---|---|
| Ghi đè 1 file 20 lần (cách nhau 100 ms) | ✅ Xử lý 1 lần, mức cuối đúng (Restricted) | ✅ Xử lý 1 lần, mức cuối đúng |
| File bị khoá độc quyền 5 giây ngay sau khi thả | ❌ Đọc lỗi "File đang bị khóa", **0/3 được xử lý lại** | ❌ **0/3 được xử lý lại**, lại còn **báo nhầm** là "TikaNativeException — file hỏng, có mật khẩu hoặc sai định dạng" |
| Thả 20 file sau 30 phút rảnh | ✅ 20/20 | ✅ 20/20 |
| Burst 25.486 file | ✅ 0 mất / 0 trùng / 0 tràn buffer | ✅ 0 mất / 0 trùng / 0 tràn buffer |

*Ô ghi đè và file bị khoá của P/A 1 lấy từ `danh-gia-xu-ly-dong-thoi.md` (B2, B3), vì script đo của lượt `143106` có lỗi đọc log, đã sửa sau đó.*

### 7.4 Tỷ lệ lỗi và độ ổn định tổng hợp

| Chỉ số | P/A 1 | P/A 2 |
|---|---|---|
| Lỗi xử lý trên 25.486 file burst | **0 %** | **0 %** |
| Service crash / khởi động lại ngoài ý muốn / sự kiện SCM bất thường | không | không |
| Sau khi khởi động lại: file đã xử lý bị quét lại | 0 | 0 |
| Thời gian từ khởi động lại tới khi đang theo dõi | 8,1–19,0 s | 2,8–24,2 s |

Thời gian khởi động lại phụ thuộc chủ yếu vào việc **quét lại toàn bộ `watch-folder` (khoảng 41.468 file)** trước khi bật watcher, không phụ thuộc vào bộ đọc.

---

## 8. So sánh hai phương án và lựa chọn

### 8.1 Bảng tổng hợp

| Tiêu chí (theo thứ tự ưu tiên) | P/A 1 | P/A 2 | Bên tốt hơn |
|---|---|---|---|
| 1. Độ chính xác (accuracy, F1 nhạy cảm, 16 lớp) | 81,6 % / 93,7 % | 81,7 % / 93,7 % | Ngang nhau (99,6 % file cùng mức) |
| 1b. Độ đầy đủ của text (Windows-1258, eml, cắt âm thầm) | Không có lỗi đã biết | 3 lỗi | **P/A 1** |
| 2. Thời gian / file (service, burst) | 308–337 ms | 446–452 ms | **P/A 1** |
| 3. CPU / file | ≈ 13–15 ms | ≈ 41–42 ms | **P/A 1** (≈ 3 lần) |
| 4. RAM nền / đỉnh | ≈ 80–90 (ước tính) / 138–185 MB | 129–134 / 242–262 MB | **P/A 1** |
| 5. Ổn định (mất file, crash, rò rỉ) | Tốt | Tốt | Ngang nhau |
| Định dạng cũ `.doc/.ppt/.rtf` | Có (qua Tika) | Có | Ngang nhau |
| Nhận dạng file đổi đuôi | Kém (đọc theo đuôi) | Tốt | P/A 2 |

**Kết luận: chọn P/A 1.** Độ chính xác ngang nhau, nên tiêu chí quyết định là chi phí tài nguyên và độ đầy đủ của text, và P/A 1 tốt
hơn ở cả hai. Lợi thế duy nhất của P/A 2 (nhận dạng file đổi đuôi) có thể bổ sung cho P/A 1 bằng cách kiểm tra vài byte đầu của file
(`PK`, `%PDF`, `D0 CF 11 E0`), xem mục 9.

### 8.2 Vì sao P/A 2 tốn tài nguyên hơn
1. **Mỗi file tốn CPU khoảng 3 lần** (mục 6.2). Trong tiến trình, khi file đã có trong bộ đệm, Tika trích mất 10,8 ms/file so với
   2,1–2,8 ms của bộ đọc cũ.
2. **Hàng đợi tự khuếch đại:** service không giới hạn số file chạy cùng lúc. Mỗi file chạy lâu hơn thì số file dở dang tăng (từ khoảng
   19–25 lên khoảng 30–32), và thời gian mỗi file càng tăng (định luật Little). Vì vậy khi chạy tuần tự P/A 2 chỉ chậm hơn khoảng 25 %,
   nhưng khi burst thì chậm hơn khoảng 35–45 %.
3. **RAM nền:** DLL chứa sẵn 63 MB dữ liệu khởi tạo của Tika (image heap) và 36 MB mã máy. Thêm vào đó là heap Java (tối đa 256 MB)
   phình ra khi nhiều file được phân tích cùng lúc.
4. **Giả thuyết chưa đo** (từ cấu hình `TikaExtractor.java`): nhận dạng loại file bằng nội dung cho mọi file; docx/pptx đọc theo kiểu
   DOM; phân tích cả tài liệu nhúng (ảnh); chuẩn hoá NFC hai lần và copy text nhiều lần; GraalVM Community biên dịch ở mức `-O2`
   không có PGO; Serial GC dừng mọi luồng khi dọn rác.

Nếu sau này cần P/A 2, các hướng tối ưu (đọc SAX cho docx/pptx, bỏ qua tài liệu nhúng loại ảnh, gợi ý loại file theo đuôi, bỏ NFC
phía Java, chỉnh GC, cắt bớt parser không dùng, PGO) cần **đo tách thời gian trước** rồi thử từng thay đổi. Mình không kỳ vọng P/A 2
sẽ đuổi kịp P/A 1 ở 8 định dạng chính.

---

## 9. Rủi ro, lỗi phát hiện và khuyến nghị

| # | Vấn đề | Ảnh hưởng tới | Mức độ | Khuyến nghị |
|---|---|---|---|---|
| 1 | **File đọc lỗi không có bản ghi** (chỉ một dòng cảnh báo trong log) | Cả hai | Cao: file nhạy cảm bị hỏng, có mật khẩu, quá lớn... biến mất khỏi kết quả | Lưu bản ghi `read_failed` + `needs_review`. Không gán Public cho file "không đọc được". Công cụ đo cũng tách riêng trạng thái "chưa kết luận" |
| 2 | **File bị khoá không được thử lại** | Cả hai (P/A 2 còn báo nhầm nguyên nhân) | Cao: bỏ sót tới lần khởi động lại sau | Thử lại có giãn cách (5 s, 30 s, 2 phút). Phía Tika phải phân biệt "không mở được file" với "file hỏng" |
| 3 | **Phần sau ký tự thứ 2.000.000 không được quét** | Cả hai | Trung bình: 70,5 % file nhạy cảm ở bậc 3M bị hạ mức | Quét theo khúc, hoặc không kết luận Public cho file bị cắt |
| 4 | **Tika cắt âm thầm** ở 1.999.999 ký tự | P/A 2; ở P/A 1 chỉ với `.doc/.ppt/.rtf` | Trung bình | Đặt giới hạn ký tự của Tika lớn hơn giới hạn của C# 1 ký tự, hoặc DLL trả cờ "đã cắt" |
| 5 | **Không giới hạn số file xử lý đồng thời** | Cả hai | Trung bình: p95 và RAM tăng khi tải cao (16 PDF lớn đẩy RAM lên khoảng 600 MB) | `Watch:MaxConcurrency` = 4–8 |
| 6 | File đổi đuôi đọc ra rác (docx đặt tên `.txt`) | P/A 1 | Trung bình | Nhận dạng bằng vài byte đầu; không khớp đuôi thì chuyển sang Tika |
| 7 | Domain healthcare và ca khó có accuracy thấp (74 % và 71 %) | Lớp regex | Trung bình | Bổ sung rule healthcare; dựa vào lớp LLM cho vùng xám |

**Việc tiếp theo, theo thứ tự ưu tiên:**
1. Triển khai P/A 1 làm cấu hình chính của service (`Mode=Supplement`, ghim SHA-256).
2. Chạy E2E lượt A và B với P/A 1 đúng cấu hình, để thay các số ước tính ở mục 6.5 và các ô lấy từ lượt 21–22/10.
3. Sửa rủi ro 1 và 2 (file đọc lỗi, file bị khoá).
4. Sửa rủi ro 4 và 5 (cắt âm thầm, giới hạn số file đồng thời).
5. Xử lý rủi ro 3 và 6 (file rất dài, file đổi đuôi).

---

## Phụ lục

### A. Lệnh chạy lại
```powershell
# Trong tiến trình — --extractor legacy | supplement (P/A 1) | tika (P/A 2)
tools\eval\DlpBaseline\bin\Release\net10.0\DlpBaseline.exe --extractor supplement --out tools\eval\results
tools\eval\DlpSizeBench\bin\Release\net10.0\DlpSizeBench.exe eval tools\eval\size-corpus --extractor supplement --out tools\eval\results
tools\perf\DlpConcurrencyBench\bin\Release\net10.0\DlpConcurrencyBench.exe --manifest tools\eval\results\baseline-20261021-110840.csv `
    --extractor supplement --bigpdf tools\eval\size-corpus\1.5M\pdf --out tools\perf\results
dotnet run --project tools\tika-bench\DlpTikaBench -c Release -- reader-native --scenario on --dll <tika_native.dll> --sha256 <mã> `
    --list tools\tika-bench\results\real2-list.txt --jvm tools\tika-bench\results\r2-jvm

# Service thật (PowerShell quyền Administrator; LlmSvc dừng)
#   Lượt A: tuần tự + burst + khởi động lại
.\tools\perf\Measure-DlpServiceE2E.ps1 -ManifestCsv tools\eval\results\baseline-20261021-110840.csv -RestartTest
#   Lượt B: + tải đều, lô, ghi đè, file khoá, rảnh 30 phút
.\tools\perf\Measure-DlpServiceE2E.ps1 -ManifestCsv tools\eval\results\baseline-20261021-110840.csv -RestartTest -FastDrop `
    -RateSteps 5,20,50 -StepSeconds 120 -BatchSizes 1,10,100,1000,5000 -EditStorm -LockedFile -PostIdleProbe -IdleAfterSec 1800
python -I tools\perf\Analyze-DlpServiceE2E.py tools\perf\results\e2e-<RunId>
```
Lưu ý khi đo: chạy lần lượt, đo cả lượt lạnh lẫn lượt nóng, cắm sạc, không để máy ngủ.

### B. File kết quả thô

| Hạng mục | P/A 1 | P/A 2 |
|---|---|---|
| Độ chính xác + thời gian trong tiến trình | `tools/eval/results/baseline-20261026-{084125,092928}-legacy.*`, `baseline-20261026-{104525,105709}-supplement.*` | `baseline-20261026-{085210,091555}-tika.*` |
| Kích thước / định dạng | `sizebench-20261026-{090418,094240}-legacy.*`, `sizebench-20261026-{111242,111559}-supplement.*` | `sizebench-20261026-{090858,093916}-tika.*` |
| Đồng thời trong tiến trình | `tools/perf/results/concurrency-20261026-091110-legacy.*`, `concurrency-20261026-111750-supplement.*` | `concurrency-20261026-091440-tika.*` |
| Service thật | `tools/perf/results/e2e-20261021-150346-*`, `e2e-20261022-143106-*` | `e2e-20261026-094454-*`, `e2e-20261026-095603-*` |

### C. Thay đổi code để thực hiện phép đo
| File | Thay đổi | Ảnh hưởng tới hành vi mặc định |
|---|---|---|
| `DlpService/Reading/Native/TikaNativeOptions.cs` | Thêm `enum TikaNativeMode { Supplement, Replace }` và thuộc tính `Mode` | Không: mặc định là `Supplement` |
| `DlpService/Reading/Native/TikaNativeRuntime.cs` | Thêm `ReplacesAll` | Không |
| `DlpService/Reading/IContentExtractor.cs` | Thêm `int Priority => 0`: extractor có ưu tiên cao hơn thắng khi trùng đuôi file | Không: mọi extractor cũ là 0 |
| `DlpService/Reading/FileReaderService.cs` | Sắp xếp extractor theo `Priority` rồi tới tên class | Không đổi thứ tự khi mọi Priority = 0 |
| `DlpService/Reading/Extractors/TikaNativeExtractor.cs` | Ở chế độ Replace: nhận 11 đuôi file, `Priority = 100` | Không |
| `DlpService/appsettings.json` | `"Mode": "Supplement"` | Không (`Enabled` vẫn là `false`) |
| `tools/shared/ExtractorSwitch.cs` (mới) + 3 công cụ đo | Tham số `--extractor legacy|supplement|tika`; tên file kết quả có hậu tố tên bộ đọc | Chỉ ảnh hưởng công cụ đo |

Build sạch, 87/87 test đạt.

### D. Thuật ngữ
- **p50 / p95 / p99:** 50 % / 95 % / 99 % số file xử lý xong trong thời gian này. p95 thể hiện "trường hợp chậm nhưng vẫn thường gặp".
- **Precision:** trong các file bị báo là X, bao nhiêu % đúng là X (thấp = báo nhầm nhiều). **Recall:** trong các file thật sự là X, bắt được bao nhiêu % (thấp = bỏ sót nhiều). **F1:** trung bình điều hoà của hai chỉ số trên.
- **Macro / Weighted:** trung bình đều giữa các mức, hoặc trung bình có trọng số theo số file.
- **Burst:** thả liên tục một lượng lớn file. **Debounce:** service chờ 300 ms sau sự kiện file cuối cùng để file ghi xong mới đọc.
- **Lạnh / nóng:** file phải đọc từ đĩa, hoặc file đã nằm sẵn trong bộ đệm RAM của Windows.
- **Image heap:** dữ liệu khởi tạo sẵn của Tika được GraalVM đóng vào DLL, chiếm RAM ngay khi nạp DLL.
- **Định luật Little:** số việc đang dở dang = tốc độ đến × thời gian mỗi việc. Thông lượng không đổi mà thời gian mỗi việc tăng thì hàng đợi dài ra.
