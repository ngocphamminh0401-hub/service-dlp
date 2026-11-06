# Báo cáo đánh giá service DLP (lớp regex)

- **Ngày đo:** 26/10/2026
- **Đối tượng:** `DlpService`, Windows Service phát hiện và phân loại dữ liệu nhạy cảm, đánh giá ở **lớp regex** (dịch vụ LLM dừng trong lúc đo)
- **Cấu hình trích text:** Apache Tika native, `TikaNative: Mode=Replace, Isolates=2`, DLL SHA-256 `9be007f9…`
- **Phạm vi:** 5 hạng mục: thời gian xử lý, độ chính xác phân loại, ảnh hưởng của kích thước và định dạng, ảnh hưởng của tài nguyên hệ thống, xử lý đồng thời

---

## 0. Tóm tắt kết quả đánh giá

| # | Hạng mục | Kết quả chính | Đánh giá |
|---|---|---|---|
| 1 | **Thời gian xử lý** | Từ lúc thả 1 file tới khi có kết quả: **≈ 0,45 s** (gồm 300 ms chờ file ghi xong). Mỗi file trong service khoảng **100 ms** khi tuần tự, **≈ 450 ms** khi tải cao (khoảng 30 file cùng lúc). **25.486 file trong khoảng 6 phút**, năng lực khoảng **70 file/giây** | ✅ **Đạt** |
| 2 | **Độ chính xác phân loại** | Accuracy 4 mức **81,7 %**, Macro-F1 **81,7 %**. Phát hiện "nhạy cảm": Precision **91,6 %**, Recall **95,9 %**, F1 **93,7 %**. Chỉ **0,8 %** file nhạy cảm bị xếp xuống Public | ⚠️ **Đạt có điều kiện**: domain healthcare (74 %) và ca khó (71 %) còn yếu |
| 3 | **Kích thước và định dạng** | 7 định dạng và mọi kích thước tới 1,5 triệu ký tự cho **cùng kết quả phân loại** (0 % file đổi mức). File 1,5 triệu ký tự < 0,7 s (PDF khoảng 1,6 s). **Điểm mù:** phần nằm sau ký tự thứ 2.000.000 không được quét | ⚠️ **Đạt có điều kiện**: cần xử lý file rất dài |
| 4 | **Tài nguyên hệ thống** | Khi tải cao, service dùng **≤ 22 % CPU** của máy và **≤ 262 MB RAM**; rảnh thì CPU khoảng 0 %. Chạy rảnh 30 phút **không có dấu hiệu rò rỉ** | ✅ **Đạt** |
| 5 | **Xử lý đồng thời** | 1–64 luồng: **0 lỗi, 0 lệch kết quả**. Thả cùng lúc tới 5.000 file: **0 mất, 0 trùng, 0 tràn buffer**. Ghi đè liên tục cho kết quả đúng. **File bị khoá lúc đọc không được thử lại** | ⚠️ **Đạt có điều kiện**: cần thêm cơ chế thử lại |

**Kết luận:**
1. Service **hoạt động ổn định và đúng** dưới tải cao: không mất file, không crash, không rò rỉ, kết quả không phụ thuộc số luồng.
2. Độ chính xác của lớp regex đạt mức nền tốt cho bài toán "có nhạy cảm hay không" (F1 93,7 %, bỏ sót 4,1 %). Phân biệt 4 mức còn nhầm giữa các mức kề nhau; đây là phần lớp LLM được thiết kế để thẩm định.
3. Bộ trích text Tika native **đọc được nhiều dữ liệu hơn** (file đổi đuôi, file đính kèm, định dạng cũ), đổi lại tốn CPU hơn. Phần chi phí này đã được giảm đáng kể nhờ tối ưu (mục 2.3).
4. **Ưu tiên tiếp theo:** thử lại file bị khoá; lưu bản ghi cho file đọc lỗi; xử lý file vượt 2 triệu ký tự; giới hạn số file xử lý cùng lúc.

---

## 1. Tổng quan service DLP

### 1.1 Mục đích
`DlpService` chạy nền trên Windows, dưới Service Control Manager (tài khoản LocalSystem). Service theo dõi thư mục được cấu hình,
tự trích nội dung các file mới hoặc vừa sửa, phát hiện dữ liệu nhạy cảm (CCCD, số thẻ, tài khoản ngân hàng, thông tin sức khoẻ,
thông tin nhân sự, tài liệu mật nội bộ...) và **phân loại file theo 4 mức: Public, Internal, Confidential, Restricted**.

### 1.2 Kiến trúc và luồng xử lý

```
 File mới/sửa ─► Watcher ─► Trích text ─► Quét regex ─► Phân loại ─► Định tuyến ─► Lưu kết quả (SQLite)
                (debounce    (Tika native,   (222 pattern,   (4 mức +     (chắc chắn /         │
                 300 ms,      mọi định dạng)  validator,      điểm S1,     vùng xám)            └─► vùng xám → LLM
                 cache)                       ngữ cảnh)       FileConf.)                           (ngoài phạm vi)
```

| Thành phần | Vai trò | Giới hạn thiết kế |
|---|---|---|
| Watcher (`FileWatcherService`) | Phát hiện file mới/sửa; chờ 300 ms cho file ghi xong; bỏ qua file không đổi (cache theo kích thước + thời gian sửa, rồi theo hash nội dung) | Chưa giới hạn số file xử lý cùng lúc |
| Trích text (`FileReaderService` + Tika native) | Đọc nội dung mọi định dạng; chuẩn hoá Unicode (NFC) | Tối đa 2.000.000 ký tự; hạn 30 giây/file |
| Quét regex (`RegexScanner`) | 222 pattern theo 4 nhóm (PII, tài chính, nhân sự, tổ chức), kèm hàm kiểm tra (checksum CCCD, IBAN...) và luật ngữ cảnh | Có hạn thời gian cho mỗi pattern |
| Phân loại (`ClassificationEngine`) | Gán mức cao nhất theo module; tính điểm tin cậy (S1, FileConfidence) | |
| Định tuyến + lưu | Kết quả chắc chắn được chốt; vùng xám được đánh dấu cần xem lại hoặc chuyển LLM; lưu SQLite | |

### 1.3 Phạm vi đánh giá
- **Có:** lớp regex đầy đủ từ watcher tới DB, trên service thật và trên pipeline gọi trực tiếp.
- **Không:** lớp LLM (`LlmSvc` dừng), PDF scan cần OCR, file có mật khẩu.

---

## 2. Thành phần trích text: Apache Tika native

### 2.1 Vì sao dùng Tika native
Apache Tika là thư viện trích text mã nguồn mở của Apache, hỗ trợ hơn 1.000 định dạng. Service dùng bản **biên dịch native bằng
GraalVM** (`tika_native.dll`), chạy **ngay trong tiến trình của service**. Như vậy không cần cài Java và không cần chạy thêm tiến trình nào.

### 2.2 Ưu điểm (có số đo)

| Ưu điểm | Số đo | Ý nghĩa với DLP |
|---|---|---|
| **Đọc được nhiều dữ liệu hơn** | Tìm thấy **84,7 %** giá trị đánh dấu trên corpus 200 file (bộ đọc .NET trước đây: 75,0 %); **0 lỗi** trên file hợp lệ (trước: 3) | Ít bỏ sót nội dung nhạy cảm hơn |
| **Nhận dạng file bằng nội dung** | File đổi đuôi đọc đúng **6/6** (trước: 2/6; ví dụ docx đặt tên `.txt` bị đọc ra rác) | Chặn cách lọt dữ liệu đơn giản nhất là đổi đuôi file |
| **Đọc file đính kèm** | Email có đính kèm docx: **79,4 %** (trước: 38,2 %) | Không bỏ qua dữ liệu bên trong đính kèm |
| **Một engine cho mọi định dạng** | 11 đuôi đang dùng, kể cả `.doc/.ppt/.rtf`; đúng **50/50** file định dạng cũ thật | Một chỗ bảo trì thay cho 8 bộ đọc riêng |
| **Độ chính xác phân loại ngang hoặc nhỉnh hơn** | Accuracy 81,7 % (trước: 81,6 %); bỏ sót file nhạy cảm 529 (trước: 540) | Không đánh đổi chất lượng phát hiện |
| **Bộ nhớ có trần** | Heap tối đa 256 MB mỗi isolate; rảnh 30 phút không rò rỉ | RAM dự đoán được |

### 2.3 Đánh đổi và cách đã xử lý

| Đánh đổi | Mức độ (đo) | Đã xử lý / cách giảm thiểu |
|---|---|---|
| Tốn CPU hơn bộ đọc .NET | Trích 1 file docx/pdf: **6,1 ms** (bộ đọc .NET: 2,8 ms) | Đã tối ưu: từ 7,2–7,6 xuống 6,1 ms (−18 %). Hướng tiếp: PGO (Phụ lục A) |
| Bộ dọn rác (GC) của Java dừng mọi luồng | Ở 16 luồng, GC chiếm khoảng 46 % thời gian thực | Đã xử lý bằng **2 isolate**: thông lượng 16 luồng **+30–40 %** |
| RAM nền cao hơn | Khoảng 130 MB khi rảnh; 2 isolate thêm khoảng 145 MB khi tải cao | Chấp nhận được; giới hạn số file đồng thời sẽ giảm đỉnh |
| PDF rất lớn chậm hơn | 1,5 triệu ký tự: **1,6 s** (bộ đọc .NET: 0,8 s) | Hiếm gặp; giới hạn đồng thời tránh nhiều PDF lớn chạy cùng lúc |
| Email chưa lấy tiêu đề / người gửi vào text | Tiêu đề và người gửi nằm ở metadata | Việc tiếp theo: ghép metadata eml vào text |
| Lỗi khi tích hợp | Cắt nội dung không cảnh báo; file bị khoá bị báo là "hỏng"; txt/csv Windows-1258 trả rỗng hoặc lỗi dấu | **Đã sửa tất cả** và đo xác nhận (mục 6.5, 8.3, Phụ lục A.4) |

---

## 3. Phương pháp đánh giá

### 3.1 Môi trường
Intel i7-13700H (20 luồng logic), RAM 13,7 GB, Windows 11, .NET 10.0.12, cắm sạc, chế độ nguồn Balanced. GraalVM CE 25.0.2 (Serial
GC, heap 256 MB mỗi isolate).

### 3.2 Dữ liệu (toàn bộ là dữ liệu tổng hợp hoặc file công khai, không có dữ liệu thật của người dùng)

| Bộ dữ liệu | Quy mô | Dùng cho |
|---|---|---|
| rawdata có nhãn | 12.743 mẫu × (docx + pdf) = **25.486 file**, 1,7 GB; 16 lớp (4 domain × 4 nhãn) | Hạng mục 1, 2, 4, 5 |
| size-corpus | **1.568 file** = 64 mẫu có nhãn × 7 định dạng × 5 bậc kích thước | Hạng mục 3 |
| Tập con 4.000 file + 16 PDF lớn | Lấy đều 16 lớp, seed cố định | Hạng mục 5 (trong tiến trình) |
| Corpus tổng hợp có đáp án | 200 file, 8 định dạng, có file đổi đuôi, file lỗi, Windows-1258 | Độ đầy đủ của text |
| File thật | 386 pdf/docx + 98 csv/rtf/doc/ppt/xls/xlsx | Kiểm tra chất lượng sau mỗi thay đổi |

### 3.3 Hai lớp đo

| Lớp đo | Cách làm | Ký hiệu trong báo cáo |
|---|---|---|
| **Trong tiến trình** | Gọi trực tiếp đúng các class của service (`FileReaderService` → `RegexScanner` → `ClassificationEngine` → `ResultStore`), không có watcher. Kiểm soát chính xác số luồng và bộ đệm file. Công cụ: `DlpBaseline`, `DlpSizeBench`, `DlpConcurrencyBench`, `DlpTikaBench tika-profile` | **[TT]**: dùng DLL tối ưu, 2 isolate |
| **Service thật** | Service chạy dưới SCM; script `Measure-DlpServiceE2E.ps1` đóng vai người dùng thả file vào thư mục theo dõi, đọc kết quả từ log, lấy mẫu CPU/RAM/I-O mỗi khoảng 1 giây | **[SV]**: lượt `20261026-094454` (A) và `20261026-095603` (B), **đo bằng Tika trước khi tối ưu và trước khi sửa 3 lỗi** |

Hai lớp đo cho **cùng kết quả phân loại**: ma trận nhầm lẫn của service giống hệt của `DlpBaseline`.

### 3.4 Đo công bằng và giới hạn
- **Bộ đệm file:** lượt nào chạy sau sẽ đọc file từ RAM nên nhanh hơn rõ rệt. Vì vậy mọi so sánh tốc độ đều là lạnh với lạnh hoặc nóng với nóng, các biến thể được chạy xen kẽ, và công cụ đo đồng thời đọc trước mọi file.
- **Nhiễu giữa các lượt:** khoảng 12 % (16 luồng) đến 25 %. Chênh lệch nhỏ hơn mức này không được coi là có ý nghĩa.
- **Số đo trên service [SV] dùng Tika trước tối ưu.** Sau tối ưu, phần trích text nhanh hơn khoảng 18 % (1 luồng) và thông lượng 16 luồng cao hơn khoảng 30–40 % (đo trong tiến trình). Nên xem số [SV] về thời gian và CPU là **mức cận trên**. Lượt đo E2E với bản tối ưu nằm trong việc tiếp theo.
- Mỗi cấu hình chạy 1–2 lượt, trên 1 máy.

---

## 4. Hạng mục 1: Thời gian xử lý

### 4.1 Từng file, trong tiến trình [TT]

| Điều kiện | Trích text TB | Tổng/file TB (trích + quét + phân loại + ghi DB) | p95 |
|---|---|---|---|
| 1 luồng, file đã nằm trong bộ đệm (4.000 file) | **6,1–7,0 ms** | **≈ 9,4 ms** (trung vị) | 12,2 ms |
| 1 luồng, đọc từ đĩa (25.486 file) | 23,8 ms (docx 26,6 / pdf 21,1) | 25,0 ms | 48,6 ms |

Quét regex mất khoảng **1,2 ms/file**, phân loại < 0,1 ms. **Trích text chiếm khoảng 75–95 %** thời gian xử lý mỗi file.

### 4.2 Từng file, bên trong service [SV] (đọc từ log `[Perf]`, không gồm debounce)

| Bước (ms, TB) | Tuần tự (1 file mỗi lần) | Burst (khoảng 30 file cùng lúc) |
|---|---|---|
| Tra cache DB | 0,4–0,5 | 2,2–2,3 |
| Trích text | 88–90 | 402–404 |
| Quét regex | 7,8–8,4 | ≈ 20 |
| Phân loại + điểm tin cậy | 1,6 | 3,5 |
| Ghi DB | 2,4–2,7 | 18–21 |
| **Tổng TB / p95** | **101–103 / 149–162** | **446–452 / 755–783** |

Trích text trong service chậm hơn nhiều so với trong tiến trình (88 so với 6–7 ms). Nguyên nhân có khả năng là file vừa được copy
vào bị **Windows Defender quét khi service mở**: CPU của Defender tăng theo tải, khoảng 8–9 % khi burst. Phỏng đoán này chưa được
kiểm chứng riêng. Khi burst, thời gian mỗi file tăng chủ yếu do **hàng đợi**: service chưa giới hạn số file xử lý cùng lúc, nên khoảng 30 file tranh CPU.

### 4.3 Độ trễ đầu cuối (từ lúc thả file tới khi có kết quả), tuần tự [SV]

| | TB | p50 | p95 | Max |
|---|---|---|---|---|
| 64 file (docx + pdf) | **444–449 ms** | 440–441 ms | 501–516 ms | 519–607 ms |

Gồm 300 ms chờ file ghi xong (cố ý), khoảng 100 ms xử lý, và thời gian copy.

### 4.4 Toàn bộ pipeline: 25.486 file (1,7 GB) [SV]

| Lượt | Thời gian thả file | **Tới khi file cuối có kết quả** | Thông lượng | Số file xử lý cùng lúc TB |
|---|---|---|---|---|
| A: copy thường | 357,5 s | **375,5 s (6,3 phút)** | 67,9 file/s | 30,3 |
| B: thả nhanh | 169,1 s | **364,2 s (6,1 phút)** | **70,0 file/s** | 31,6 |

Ở lượt B, file được thả xong trong 169 giây nhưng service cần 364 giây. Đây là **năng lực thật của service: khoảng 70 file/giây**
(Tika trước tối ưu). Thả cả lô 5.000 file cùng lúc cũng cho 66,7 file/giây (mục 8.2).

### 4.5 Đánh giá hạng mục 1: ✅ Đạt
- Người dùng nhận kết quả sau khoảng **0,45 giây** mỗi file khi tải bình thường. Tải đều tới 50 file/giây, p95 vẫn dưới 0,6 giây (mục 7.3).
- Nút thắt là **trích text**. Khi tải cao, hàng đợi không giới hạn làm thời gian mỗi file tăng khoảng 4 lần. Khuyến nghị: giới hạn số file xử lý cùng lúc (mục 10).

---

## 5. Hạng mục 2: Độ chính xác phân loại

Nhãn → mức: public → Public, internal → Internal, confidential → Confidential, top_secret → Restricted. "Nhạy cảm" = Confidential + Restricted.
Kết quả [TT] và [SV] **giống hệt nhau**, và giống nhau giữa bản Tika trước và sau tối ưu (0/25.486 file đổi mức).

### 5.1 Tổng thể (25.486 file)

| Chỉ số | Giá trị |
|---|---|
| **Accuracy (4 mức)** | **81,7 %** |
| Macro Precision / Recall / F1 | 82,0 % / 81,7 % / **81,7 %** |
| Weighted Precision / Recall / F1 | 82,0 % / 81,7 % / 81,8 % |
| **"Nhạy cảm": Precision / Recall / F1** | **91,6 % / 95,9 % / 93,7 %** |
| TP / FP (báo nhầm) / FN (bỏ sót) / TN | 12.269 / 1.120 / 529 / 11.568 |
| Phát hiện PII: Precision / Recall / F1 | 76,0 % / 93,4 % / 83,8 % |
| File đọc lỗi / quét chưa trọn | 0 / 0 |

### 5.2 Theo 4 mức

| Mức | Số file thật | Precision | Recall | F1 |
|---|---|---|---|---|
| Public | 6.400 | 86,7 % | 78,9 % | 82,7 % |
| Internal | 6.288 | 78,3 % | 78,1 % | 78,2 % |
| Confidential | 6.400 | 75,8 % | 84,4 % | 79,9 % |
| Restricted | 6.398 | 87,1 % | 85,3 % | 86,2 % |

Ma trận nhầm lẫn (hàng = nhãn thật, cột = dự đoán):

| Thật \ Dự đoán | Public | Internal | Confidential | Restricted |
|---|---|---|---|---|
| Public | **5.052** | 936 | 394 | 18 |
| Internal | 667 | **4.913** | 540 | 168 |
| Confidential | 52 | 324 | **5.399** | 625 |
| Restricted | 54 | 99 | 786 | **5.459** |

Nhầm lẫn chủ yếu xảy ra **giữa hai mức kề nhau**. Chỉ 106 file nhạy cảm (0,8 %) bị xếp xuống Public; 412 file Public (6,4 %) bị
xếp lên mức nhạy cảm. Với DLP, báo nhầm ít nguy hiểm hơn bỏ sót.

### 5.3 Theo 16 lớp dữ liệu

| Lớp | Số file | Precision | Recall | F1 |
|---|---|---|---|---|
| finance_banking__public | 1.600 | 92,3 % | 73,4 % | 81,8 % |
| finance_banking__internal | 1.600 | 78,7 % | 86,1 % | 82,2 % |
| finance_banking__confidential | 1.600 | 80,5 % | 94,5 % | 86,9 % |
| finance_banking__top_secret | 1.600 | 96,0 % | 90,1 % | 92,9 % |
| healthcare__public | 1.600 | 84,8 % | 88,5 % | 86,6 % |
| **healthcare__internal** | 1.600 | 81,4 % | **60,6 %** | **69,5 %** |
| **healthcare__confidential** | 1.600 | 66,1 % | **64,0 %** | **65,0 %** |
| healthcare__top_secret | 1.600 | 67,2 % | 83,6 % | 74,5 % |
| hr__public | 1.600 | 81,6 % | 76,2 % | 78,8 % |
| hr__internal | 1.488 | 76,4 % | 84,0 % | 80,0 % |
| hr__confidential | 1.600 | 77,1 % | 87,0 % | 81,7 % |
| hr__top_secret | 1.598 | 97,9 % | 82,7 % | 89,6 % |
| insurance__public | 1.600 | 89,5 % | 77,7 % | 83,2 % |
| insurance__internal | 1.600 | 77,6 % | 82,3 % | 79,9 % |
| insurance__confidential | 1.600 | 78,0 % | 91,9 % | 84,4 % |
| insurance__top_secret | 1.600 | 95,2 % | 85,0 % | 89,8 % |

| Nhóm | Số file | Accuracy | F1 "nhạy cảm" |
|---|---|---|---|
| Domain finance_banking | 6.400 | 86,0 % | 96,2 % |
| **Domain healthcare** | 6.400 | **74,2 %** | 90,6 % |
| Domain hr | 6.286 | 82,4 % | 93,5 % |
| Domain insurance | 6.400 | 84,2 % | 94,7 % |
| Định dạng docx / pdf | 12.743 / 12.743 | 82,1 % / 81,3 % | 93,8 % / 93,6 % |
| **Ca khó (borderline)** | 3.060 | **71,3 %** | 89,3 % |

### 5.4 Đánh giá hạng mục 2: ⚠️ Đạt có điều kiện
- **Đạt** với mục tiêu chính "có nhạy cảm hay không": F1 93,7 %, Recall 95,9 %.
- **Chưa tốt** khi phân 4 mức ở domain healthcare (Internal và Confidential khoảng 61–64 % recall) và ở ca khó (71 %). Đây là giới hạn của regex: phân biệt mức cần hiểu ngữ cảnh. Hướng xử lý: bổ sung rule healthcare, và để lớp LLM thẩm định vùng xám.

---

## 6. Hạng mục 3: Ảnh hưởng của kích thước và định dạng file

### 6.1 Bộ dữ liệu
64 mẫu có nhãn được chèn thêm văn bản trung tính (đã kiểm tra: 0 chỗ khớp rule) để đạt 5 bậc kích thước, mỗi mẫu ghi ra 7 định dạng.
Ở bậc `3M-cuoi`, nội dung gốc nằm **sau ký tự thứ 2.000.000**.

### 6.2 Thời gian xử lý (ms/file TB, trích + quét + phân loại, lượt nóng) [TT]

| Bậc (ký tự) | txt | csv | docx | xlsx | pptx | pdf | eml | Tất cả TB (p95) |
|---|---|---|---|---|---|---|---|---|
| gốc (≈ 1.900) | 5,9 | 4,4 | 4,5 | 3,4 | 4,0 | 5,8 | 3,4 | **4,5** (6,9) |
| 20 nghìn | 13,9 | 12,1 | 12,1 | 11,6 | 12,6 | 28,7 | 14,5 | **15,1** (27,7) |
| 200 nghìn | 78,0 | 78,6 | 77,3 | 78,4 | 88,9 | 217,0 | 85,8 | **100,6** (218,6) |
| 1,5 triệu | 551 | 599 | 596 | 587 | 638 | **1.588** | 566 | **732** (1.593) |
| 3 triệu (bị cắt ở 2 triệu) | 758 | 747 | 729 | 774 | 863 | **2.036** | 769 | **954** (2.044) |

- Thời gian **tăng gần tuyến tính theo số ký tự**: quét regex khoảng **34 ms** và trích text khoảng **15 ms** cho mỗi 100 nghìn ký tự.
- **PDF là định dạng chậm nhất** với file lớn (1,6 s ở 1,5 triệu ký tự, khoảng 500 trang). Các định dạng khác tương đương nhau.

### 6.3 Độ chính xác theo kích thước và định dạng

| Bậc | Accuracy (giống nhau ở cả 7 định dạng) | % file đổi mức so với bậc gốc |
|---|---|---|
| gốc / 20 nghìn / 200 nghìn / 1,5 triệu | **87,5 %** | **0 %** |
| **3 triệu (nội dung ở cuối)** | **25,0 %** | **70,5 % bị hạ mức** |

- **Định dạng không ảnh hưởng** tới kết quả phân loại.
- **Kích thước không ảnh hưởng**, miễn là ≤ 2.000.000 ký tự.
- **Điểm mù:** nội dung sau ký tự thứ 2.000.000 không được quét. Service có ghi cảnh báo "chỉ quét 2.000.000 ký tự đầu" (**112/112** file), nhưng vẫn kết luận theo phần đầu.

### 6.4 Bộ nhớ theo kích thước [TT]
Working set đỉnh của tiến trình: **203 MB** (bậc gốc) → **302 MB** (bậc 3 triệu). Bộ nhớ ít phình theo kích thước file, vì heap Java có trần.

### 6.5 Độ đầy đủ của text theo định dạng (corpus 200 file có đáp án) [TT]

| Định dạng | txt | csv | docx | pptx | xlsx | xls | pdf | eml | File đổi đuôi |
|---|---|---|---|---|---|---|---|---|---|
| % giá trị tìm thấy | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 79,4 % | **6/6** |

Gồm cả txt/csv bảng mã Windows-1258 (trước khi sửa lỗi: 75 % / 70 %). Định dạng cũ `.doc/.ppt/.rtf`: **50/50** file thật đọc đúng.

### 6.6 Đánh giá hạng mục 3: ⚠️ Đạt có điều kiện
- Đạt về độ chính xác và độ ổn định trên mọi định dạng và kích thước thông thường.
- Cần xử lý **file vượt 2 triệu ký tự**: quét theo khúc, hoặc không kết luận Public cho file bị cắt. Email cần bổ sung tiêu đề và người gửi vào nội dung quét.

---

## 7. Hạng mục 4: Ảnh hưởng của tài nguyên hệ thống

### 7.1 Theo giai đoạn [SV] (lấy mẫu mỗi khoảng 1 giây; CPU tính theo % cả máy, 100 % = 20 luồng logic)

| Giai đoạn | CPU service TB / max | RAM đầu → max → cuối | I/O đọc | CPU Defender | Handle max / Thread max |
|---|---|---|---|---|---|
| Nền (rảnh) | 0,0 % / 0,2 % | 135–142 → 125–128 MB | 0 | 0,3–0,7 % | 697–718 / 35 |
| Tuần tự | 1,1–1,2 % / 2,3–2,9 % | 126–130 → 155–158 MB | 0,35 MB/s | 0,5–0,8 % | 700–708 / 30–35 |
| Tải đều 5 file/s | 1,1 % / 2,3 % | 158 → 164 MB | 0,73 MB/s | 0,9 % | 619 / 28 |
| Tải đều 20 file/s | 4,8 % / 6,4 % | 144 MB | 2,8 MB/s | 3,7 % | 643 / 28 |
| Tải đều 50 file/s | 12,3 % / 14,7 % | 103 → 189 MB | 6,7 MB/s | 8,1 % | 697 / 38 |
| **Burst 25.486 file** | **16,8–17,7 % / 21,8–22,1 %** | 154–233 → **242–262** → 175–205 MB | 9,7–10,1 MB/s | 7,8–9,0 % | 932–1.022 / 59–60 |
| Sau tải | 0,0 % | → 172–174 MB | 0 | 0,8–1,8 % | 686–699 / 43–44 |

### 7.2 Theo mức tải đều [SV]

| Tải | Hoàn thành | Độ trễ đầu cuối p50 / p95 / max | Xử lý trong service TB | CPU service |
|---|---|---|---|---|
| 5 file/s (600 file) | 600/600 | 473 / 540 / 591 ms | 90,5 ms | 1,1 % |
| 20 file/s (2.400 file) | 2.400/2.400 | 469 / 538 / 640 ms | 99,7 ms | 4,8 % |
| 50 file/s (6.000 file) | 6.000/6.000 | 501 / **594** / 852 ms | 123,7 ms | 12,3 % |

Service **theo kịp mọi mức tải đều đã thử** và độ trễ gần như không đổi. Ở 50 file/giây, service chỉ dùng khoảng 12 % CPU của máy.

### 7.3 Ổn định dài hạn: chạy rảnh 30 phút sau tải [SV]
Độ dốc RAM **−47,6 MB/giờ** (bộ nhớ được trả lại), handle +52/giờ, thread −4/giờ. **Không có dấu hiệu rò rỉ.** Sau 30 phút rảnh,
service vẫn xử lý **20/20** file mới.

### 7.4 CPU và RAM trên mỗi file
- **CPU:** khoảng 41–42 ms CPU/file khi burst [SV, trước tối ưu]. Sau tối ưu, CPU/file trong tiến trình giảm khoảng 15 % (9,0 → 7,7 ms ở 1 luồng) [TT].
- **RAM với 2 isolate** [TT, 4.000 file]: đỉnh **247 MB** (1 luồng) → **275 MB** (16 luồng) → 318 MB (64 luồng). 16 PDF lớn chạy cùng lúc: 530 MB.

### 7.5 Đánh giá hạng mục 4: ✅ Đạt
Mức tiêu thụ thấp so với máy (≤ 22 % CPU, khoảng 260 MB RAM ở tải cao nhất), trả lại bộ nhớ sau tải, không rò rỉ. Lưu ý: 2 isolate
làm RAM đỉnh tăng khoảng 100–150 MB so với 1 isolate; nhiều PDF lớn cùng lúc đẩy RAM lên khoảng 0,5 GB. Giới hạn số file đồng thời sẽ khống chế được trường hợp này.

---

## 8. Hạng mục 5: Hiệu năng khi xử lý đồng thời

### 8.1 Trong tiến trình: N luồng dùng chung một pipeline (4.000 file) [TT]

| Luồng | Thời gian | File/s | Tăng tốc | Độ trễ p50 / p95 / p99 | Lỗi | Lệch kết quả | Bản ghi DB | RAM đỉnh |
|---|---|---|---|---|---|---|---|---|
| 1 | 34,1 s | 117 | ×1,0 | 9,4 / 12,2 / 22,1 ms | 0 | 0 | 4000/4000 | 247 MB |
| 2 | 16,1 s | 248 | ×2,1 | 9,0 / 11,6 / 18,5 ms | 0 | 0 | 4000/4000 | 244 MB |
| 4 | 9,4 s | 425 | ×3,6 | 10,0 / 15,0 / 24,7 ms | 0 | 0 | 4000/4000 | 259 MB |
| **8** | **6,8 s** | **588** | **×5,0** | 13,0 / 27,0 / 34,4 ms | 0 | 0 | 4000/4000 | 261 MB |
| 16 | 6,8 s | 591 | ×5,0 | 24,8 / 56,5 / 65,4 ms | 0 | 0 | 4000/4000 | 275 MB |
| 32 | 7,4 s | 542 | ×4,6 | 54 / 115 / 151 ms | 0 | 0 | 4000/4000 | 304 MB |
| 64 | 8,6 s | 465 | ×4,0 | 59 / 457 / 655 ms | 0 | 0 | 4000/4000 | 318 MB |

- **An toàn đa luồng: đạt.** Từ 1 tới 64 luồng: 0 lỗi, 0 file đổi mức so với chạy 1 luồng, DB đủ bản ghi.
- Thông lượng **đạt đỉnh ở 8–16 luồng** (khoảng 590 file/s), rồi giảm vì tranh CPU và tranh khoá ghi DB. Trên 16 luồng, độ trễ p95 tăng mạnh mà thông lượng không tăng.
- **16 PDF lớn** (1,5 triệu ký tự mỗi file): 1 luồng 25,8 s, **4 luồng 9,4 s**, 16 luồng 12,4 s (chậm hơn 4 luồng; RAM 530 MB).

### 8.2 Trên service thật: thả cùng lúc một lô N file [SV]

| Lô | Thời gian cả lô | Thông lượng | Độ trễ p50 / p95 | Mất | Trùng | Đọc lỗi | Tràn buffer |
|---|---|---|---|---|---|---|---|
| 1 | 0,5 s | — | 0,50 s | 0 | 0 | 0 | 0 |
| 10 | 0,5 s | 18,5 file/s | 0,53 / 0,54 s | 0 | 0 | 0 | 0 |
| 100 | 1,7 s | 57,8 file/s | 1,07 / 1,31 s | 0 | 0 | 0 | 0 |
| 1.000 | 16,6 s | 60,4 file/s | 8,3 / 15,4 s | 0 | 0 | 0 | 0 |
| 5.000 | 74,9 s | 66,7 file/s | 30,9 / 56,1 s | 0 | 0 | 0 | 0 |

Không mất hay trùng file ở mọi kích thước lô. Lô lớn chỉ làm **file cuối phải chờ lâu hơn**: lô 5.000 file thì file cuối chờ khoảng 1 phút.

### 8.3 Các tình huống đặc biệt [SV]

| Kiểm tra | Kết quả |
|---|---|
| Ghi đè 1 file 20 lần liên tiếp (cách nhau 100 ms) | ✅ Xử lý **1 lần**, mức cuối **đúng** theo nội dung cuối |
| File bị khoá độc quyền 5 giây ngay sau khi thả | ❌ Đọc lỗi, **0/3 được xử lý lại** sau khi mở khoá (bỏ sót tới lần khởi động lại sau). Thông điệp lỗi báo nhầm là "file hỏng", **lỗi này đã được sửa** và kiểm chứng: nay báo đúng "File đang bị khóa" |
| Thả 20 file sau 30 phút rảnh | ✅ 20/20 |
| Khởi động lại service | ✅ Tới Running sau 0,7–6,6 s; tới khi đang theo dõi sau 2,8–24,2 s (phụ thuộc số file trong thư mục, khoảng 41.000); **0 file bị quét lại** nhờ cache |
| Burst 25.486 file | ✅ 0 mất, 0 trùng, 0 tràn buffer, 0 lỗi đọc |
| Crash, sự kiện SCM bất thường, lỗi Event Log | ✅ Không có |

### 8.4 Đánh giá hạng mục 5: ⚠️ Đạt có điều kiện
- **Đạt:** an toàn đa luồng, không mất hay trùng file, kết quả đúng khi ghi đè, ổn định sau khi rảnh và sau khi khởi động lại.
- **Chưa đạt:** **file bị khoá lúc đọc không được thử lại**. Việc này ảnh hưởng thật khi người dùng đang mở file trong Word hoặc Excel.
- **Khuyến nghị:** giới hạn số file xử lý cùng lúc ở **8**. Đây là mức thông lượng tốt nhất; vượt mức này chỉ làm tăng độ trễ và RAM.

---

## 9. Tổng hợp đánh giá

| Hạng mục | Đánh giá | Điểm mạnh | Vấn đề cần xử lý |
|---|---|---|---|
| 1. Thời gian | ✅ Đạt | 0,45 s/file; 25.486 file trong khoảng 6 phút; tải đều 50 file/s ổn định | Hàng đợi không giới hạn khi burst |
| 2. Độ chính xác | ⚠️ Có điều kiện | F1 "nhạy cảm" 93,7 %, bỏ sót 4,1 %; kết quả tất định, không phụ thuộc số luồng | healthcare 74 %, ca khó 71 %, nhầm giữa các mức kề nhau |
| 3. Kích thước / định dạng | ⚠️ Có điều kiện | Không đổi kết quả theo định dạng và kích thước ≤ 2 triệu ký tự; đọc đủ 8 định dạng + định dạng cũ | Điểm mù > 2 triệu ký tự; eml thiếu tiêu đề |
| 4. Tài nguyên | ✅ Đạt | ≤ 22 % CPU, khoảng 260 MB RAM, không rò rỉ | RAM tăng khi nhiều PDF lớn cùng lúc |
| 5. Đồng thời | ⚠️ Có điều kiện | 0 mất / trùng / lỗi tới 5.000 file/lô và 64 luồng | File bị khoá không được thử lại |

---

## 10. Rủi ro, vấn đề phát hiện và khuyến nghị

| # | Vấn đề | Mức độ | Khuyến nghị |
|---|---|---|---|
| 1 | **File bị khoá lúc đọc không được thử lại** | Cao (bỏ sót file đang mở) | Thử lại có giãn cách (5 s, 30 s, 2 phút); thông điệp lỗi đã được sửa nên phân biệt được "bị khoá" với "hỏng" |
| 2 | **File đọc lỗi không có bản ghi** (chỉ có log) | Cao (file hỏng hoặc có mật khẩu biến mất khỏi kết quả) | Lưu trạng thái `read_failed` + `needs_review`; không coi "không đọc được" là Public |
| 3 | **Nội dung sau 2.000.000 ký tự không được quét** | Trung bình | Quét theo khúc, hoặc không kết luận Public cho file bị cắt |
| 4 | **Chưa giới hạn số file xử lý cùng lúc** | Trung bình | `Watch:MaxConcurrency = 8` |
| 5 | Độ chính xác healthcare / ca khó | Trung bình | Bổ sung rule healthcare; LLM thẩm định vùng xám |
| 6 | Email thiếu tiêu đề / người gửi trong nội dung quét | Thấp | Ghép metadata eml vào text |

**Lộ trình đề xuất:**
1. Deploy bản tối ưu lên service và chạy lại đo E2E (thay số [SV] trong báo cáo bằng số của bản tối ưu).
2. Xử lý rủi ro 1, 2, 4.
3. Xử lý rủi ro 3, 6; bổ sung rule healthcare.
4. Tối ưu Tika tiếp: đo điểm nóng bằng Java Flight Recorder, giảm rác, PGO (Phụ lục A.5).

---

## Phụ lục A. Tối ưu bộ trích text Tika native

### A.1 Kiểm chứng nguyên nhân Tika tốn CPU (4.000 file đã nằm trong bộ đệm)

| # | Giả thuyết | Kết luận | Số đo |
|---|---|---|---|
| H1 | Nhận dạng file bằng nội dung | ✅ Đúng | Khoảng 27 % thời gian trích docx, 14 % pdf |
| H2 | docx/pptx đọc theo kiểu DOM | 🟡 Một phần | SAX: −8 % TB, −30 % p95 docx ở 1 luồng |
| H3 | Phân tích tài liệu nhúng (ảnh) | ❓ Chưa kết luận | Bộ lọc thử nghiệm làm hỏng eml/pdf, nên đã bỏ |
| H4 | NFC hai lần + copy text | ❌ phía C# (< 0,2 %); 🟡 phía Java (−6 %) | |
| H5 | Mã biên dịch trước (AOT `-O2`, không PGO) | ✅ **Đúng, lớn nhất ở 1 luồng** | Cùng code trên JVM HotSpot nhanh hơn khoảng 1,6–1,75 lần |
| H6 | Serial GC dừng mọi luồng | ✅ **Đúng, lớn nhất khi tải cao** | Ở 16 luồng, GC chiếm khoảng 46 % thời gian thực; chỉ dùng được khoảng 50 % CPU |

**Giải thích H5:** Java thông thường biên dịch **trong lúc chạy** (JIT), quan sát chương trình thực tế rồi tối ưu theo đúng hành vi đã thấy.
Bản native biên dịch **trước khi chạy** (AOT), lúc đó chưa có dữ liệu thật nên không tối ưu mạnh được. Tika gọi qua interface rất nhiều,
đúng loại mã mà JIT tối ưu tốt còn AOT thì không.

**Giải thích H6:** Serial GC dọn bộ nhớ bằng một luồng và **dừng mọi luồng** đang dùng cùng isolate. Càng nhiều file chạy song song thì
càng sinh nhiều rác, GC chạy càng dày, và tất cả cùng phải chờ.

### A.2 Tối ưu đã áp dụng (đo xen kẽ, cùng điều kiện)

| Tối ưu | Trước → sau | Kiểm tra chất lượng |
|---|---|---|
| **C1** bỏ NFC phía Java (C# đã làm) + **C2** nhận dạng nhanh khi đuôi file khớp chữ ký byte đầu (vẫn giữ chống zip bomb và đọc tài liệu nhúng; không khớp thì dùng nhận dạng gốc) + **C3** đọc docx/pptx kiểu SAX | 1 luồng: trích 7,2–7,6 → **6,0–6,1 ms (−18 %)**, 106–113 → **128–131 file/s**. 16 luồng: 412–419 → **483–515 file/s** | Text **giống hệt** trên 684 file (200 tổng hợp + 484 thật); **0/25.486** file đổi mức; file đổi đuôi vẫn 6/6 |
| **1a. Pool isolate** (mỗi isolate có heap + GC riêng), cấu hình `Isolates=2` | 16 luồng: 448–526 → **577–715 file/s** (2 isolate), 809–855 (4 isolate) | Kết quả giống hệt; RAM +145 MB (2) / +350 MB (4) |
| 2a. Kiểm tra hạn mỗi 64 sự kiện | Không đo được khác biệt | Text giống hệt |

**Đã thử nhưng không giữ:** nâng young gen (heap 384/512 MB, young 25–40 %), vì số lần GC gần như không đổi và RAM tăng; bộ lọc ảnh nhúng (H3), vì làm mất dữ liệu.

### A.3 Kết quả tổng (1 luồng, file đã nằm trong bộ đệm)
Trích docx/pdf: **7,2–7,6 ms → 6,1 ms**. Thông lượng đỉnh 5A: **447 → 591 file/s**. Bộ đọc .NET trước đây: 2,8 ms và 726–910 file/s.

### A.4 Ba lỗi đã sửa khi tích hợp

| Lỗi | Trước | Sau |
|---|---|---|
| PDF > 2 triệu ký tự bị cắt **không cảnh báo** | 96/112 cảnh báo | **112/112** (Tika đọc dư 4.096 ký tự để C# phát hiện phần bị cắt) |
| File bị khoá bị báo là "file hỏng" | "TikaNativeException — file hỏng..." | **"File đang bị khóa"** (C# thử mở lại file khi nhận mã lỗi) |
| txt/csv bảng mã Windows-1258 trả rỗng | txt 75 %, csv 70 % | **100 % / 100 %** (giải mã lại bằng bộ giải mã văn bản khi Tika trả rỗng) |
| txt/csv Windows-1258 **có nhiều chữ không dấu** bị Tika đoán nhầm bảng mã (Windows-1252), ra chữ lỗi dấu "Nguyá»…n" (phát hiện sau, sửa ngày 26/10) | 0/40, 0/10, 0/3 cụm tiếng Việt đúng ở 3 file thử | **Đúng 100 %, 0 ký tự lỗi dấu.** txt/csv không phải UTF-8 (không BOM, không phải nhị phân) được giải mã bằng bộ giải mã văn bản của service (UTF-8, rồi Windows-1258) thay cho Tika. UTF-8 và file nhị phân đặt đuôi `.txt` vẫn qua Tika, file đổi đuôi vẫn 6/6. Đã kiểm trên service thật: 14/14 file Windows-1258 được phát hiện đúng |

### A.5 Hướng tối ưu tiếp
Java Flight Recorder để tìm điểm nóng và giảm rác; PGO bằng Oracle GraalVM (cần xem điều khoản giấy phép GFTC) cho H5; giới hạn số file đồng thời cho H6.

## Phụ lục B. So sánh nhanh với bộ đọc .NET trước đây

| Chỉ số | Bộ đọc .NET | Tika native (tối ưu) |
|---|---|---|
| Accuracy / F1 "nhạy cảm" | 81,6 % / 93,7 % | 81,7 % / 93,7 % |
| Giá trị tìm thấy (corpus 200 file) / file đổi đuôi | 75,0 % / 2/6 | **84,7 % / 6/6** |
| Trích 1 file (1 luồng, nóng) | **2,8 ms** | 6,1 ms |
| Thông lượng đỉnh trong tiến trình | **726–910 file/s** | 591 file/s |
| RAM đỉnh trong tiến trình (8 luồng) | **135–164 MB** | 261 MB |
| Định dạng cũ `.doc/.ppt/.rtf` | không | có |

Chi tiết: `docs/bao-cao-hieu-nang-service-dlp.md`, `docs/bao-cao-so-sanh-tika.md`.

## Phụ lục C. Lệnh chạy lại
```powershell
# Trong tiến trình (Tika native, 2 isolate)
tools\eval\DlpBaseline\bin\Release\net10.0\DlpBaseline.exe --extractor tika --tika-isolates 2 --out tools\eval\results
tools\eval\DlpSizeBench\bin\Release\net10.0\DlpSizeBench.exe eval tools\eval\size-corpus --extractor tika --tika-isolates 2 --out tools\eval\results
tools\perf\DlpConcurrencyBench\bin\Release\net10.0\DlpConcurrencyBench.exe --manifest tools\eval\results\baseline-20261021-110840.csv `
    --extractor tika --tika-isolates 2 --bigpdf tools\eval\size-corpus\1.5M\pdf --out tools\perf\results
tools\tika-bench\DlpTikaBench\bin\Release\net10.0\DlpTikaBench.exe tika-profile --manifest tools\eval\results\baseline-20261021-110840.csv `
    --dll tools\tika-native\build\tika_native.dll --threads 1,16 --isolates 2

# Service thật (PowerShell quyền Administrator; LlmSvc dừng)
.\tools\perf\Measure-DlpServiceE2E.ps1 -ManifestCsv tools\eval\results\baseline-20261021-110840.csv -RestartTest
.\tools\perf\Measure-DlpServiceE2E.ps1 -ManifestCsv tools\eval\results\baseline-20261021-110840.csv -RestartTest -FastDrop `
    -RateSteps 5,20,50 -StepSeconds 120 -BatchSizes 1,10,100,1000,5000 -EditStorm -LockedFile -PostIdleProbe -IdleAfterSec 1800
python -I tools\perf\Analyze-DlpServiceE2E.py tools\perf\results\e2e-<RunId>
```

## Phụ lục D. File kết quả thô
- [TT]: `tools/eval/results/baseline-20261026-163335-tika.*`, `sizebench-20261026-{162920,163334}-tika.*`, `tools/perf/results/concurrency-20261026-162124-tika.*` (2 isolate), `concurrency-20261026-162444-tika.*` (1 isolate), `tikaprofile-*`.
- [SV]: `tools/perf/results/e2e-20261026-094454-*`, `e2e-20261026-095603-*`.

## Phụ lục E. Thay đổi code và cấu hình
| Nơi | Thay đổi |
|---|---|
| `DlpService/Reading/Native/TikaNativeOptions.cs` | `Mode` (Supplement/Replace), `Isolates` (mặc định 1) |
| `DlpService/Reading/Native/TikaNativeLibrary.cs` | Pool K isolate (vòng tròn); Tika đọc dư `TruncationMargin` = 4.096 ký tự |
| `DlpService/Reading/Extractors/TikaNativeExtractor.cs` | Replace: nhận 11 đuôi, `Priority = 100`; txt/csv không phải UTF-8 (`IsLegacyEncodedText`) giải mã bằng `TextFileReader`, cộng lớp bảo vệ khi Tika trả rỗng; phân biệt file bị khoá |
| `DlpService/Reading/IContentExtractor.cs`, `FileReaderService.cs` | `Priority` để chọn extractor khi trùng đuôi |
| `tools/tika-native/src/.../TikaExtractor.java`, `NativeApi.java` | C1, C2, C3, 2a; công tắc đo `DLP_TIKA_EXP_*` (mặc định tắt) |
| `tools/tika-native/build\tika_native.dll` | SHA-256 `9be007f9…` (bản trước lưu ở `build-backup-23b995a9\`) |
| `DlpService/appsettings.json` | `TikaNative: Mode, Isolates = 2` (cấu hình deploy: `Enabled=true, Mode=Replace, Sha256=9be007f9…`) |
| Công cụ đo | `--extractor legacy|supplement|tika`, `--tika-dll`, `--tika-isolates`; lệnh `tika-profile` |

Build sạch; 97/97 test đạt, kể cả test đọc thật qua DLL với 2 isolate.

## Phụ lục F. Thuật ngữ
- **p50 / p95 / p99:** 50 % / 95 % / 99 % số file xử lý xong trong thời gian này.
- **Precision:** trong các file bị báo là X, bao nhiêu % đúng là X. **Recall:** trong các file thật sự là X, bắt được bao nhiêu %. **F1:** trung bình điều hoà của hai chỉ số trên.
- **Burst:** thả liên tục một lượng lớn file. **Debounce:** chờ 300 ms sau thay đổi cuối cùng để file ghi xong mới đọc.
- **Isolate:** một "máy ảo Java thu nhỏ" bên trong DLL, có bộ nhớ và GC riêng. **GC:** bộ dọn bộ nhớ tự động của Java.
- **AOT / JIT:** biên dịch trước khi chạy / biên dịch trong lúc chạy. **PGO:** tối ưu biên dịch dựa trên dữ liệu chạy thật.
