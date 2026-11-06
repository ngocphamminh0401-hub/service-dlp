# Đánh giá ảnh hưởng của kích thước và định dạng file (lớp Regex)

- **Ngày đo:** 2026-10-22. **Công cụ:** `tools/eval/DlpSizeBench` (lệnh `gen` sinh bộ dữ liệu, `eval` đo). Kết quả thô:
  `tools/eval/results/sizebench-20261022-085414.md` + `.csv`.
- **Pipeline đo:** giống service — `FileReaderService` → `RegexScanner` (MinConfidence 0,60) → `ClassificationEngine`; 1 luồng, tuần tự,
  có làm nóng; không gồm debounce/băm/ghi DB (các bước này không phụ thuộc kích thước — xem `docs/danh-gia-service-e2e.md`).

## 1. Cách dựng bộ dữ liệu

rawdata chỉ có docx/pdf và kích thước gần như cố định (docx ~37 KB, pdf ~98 KB, 0,4–4,5 nghìn ký tự) → không đủ để đo ảnh hưởng của kích
thước. `DlpSizeBench gen` dựng bộ mới **có nhãn**:

1. Chọn 64 mẫu rawdata (4 mẫu × 16 lớp domain × mức), trích text.
2. Chèn **văn bản đệm trung tính** (câu tả phong cảnh, thời tiết, ẩm thực...) để đạt các mốc số ký tự. Câu đệm được lọc tự động: câu nào
   khớp rule hoặc chứa từ khoá của rule bị loại (3/40 câu bị loại); khối đệm 60.000 ký tự kiểm tra lại có **0 chỗ khớp** → phần thêm
   vào không đổi nhãn.
3. Ghi ra **7 định dạng**: txt, csv, docx, xlsx, pptx, pdf (font Arial nhúng, tiếng Việt), eml (quoted-printable).

| Bậc | Số ký tự | Vị trí nội dung gốc | Mẫu | File | Kích thước file TB (txt / docx / pdf / eml) |
|---|---|---|---|---|---|
| `goc` | 0,4–4,5 nghìn (TB 1.930) | — | 64 | 448 | 2 KB / 2 KB / 46 KB / 5 KB |
| `20k` | 20.000 | giữa | 64 | 448 | 26 KB / 5 KB / 64 KB / 50 KB |
| `200k` | 200.000 | giữa | 64 | 448 | 260 KB / 11 KB / 221 KB / 495 KB |
| `1.5M` | 1.500.000 | giữa | 16 | 112 | 1,9 MB / 57 KB / 1,4 MB / 3,7 MB |
| `3M-cuoi` | 3.000.000 (**vượt giới hạn 2 triệu**) | **cuối** | 16 | 112 | 3,9 MB / 109 KB / 2,7 MB / 7,4 MB |

Tổng 1.568 file, 563 MB. Đo toàn bộ trong 4,4 phút.

## 2. Thời gian xử lý (ms/file, trung bình: trích text + quét + phân loại)

| Bậc | txt | csv | docx | xlsx | pptx | pdf | eml | Tất cả (p95) |
|---|---|---|---|---|---|---|---|---|
| `goc` | 15 | 13 | 36 | 45 | 81 | 27 | 12 | 33 (109) |
| `20k` | 22 | 19 | 42 | 52 | 113 | 35 | 21 | 43 (161) |
| `200k` | 79 | 80 | 94 | 110 | 373 | 131 | 86 | 136 (686) |
| `1.5M` | 496 | 517 | 526 | 536 | **1.281** | 821 | 525 | 672 (851) |
| `3M-cuoi` | 663 | 680 | 680 | 670 | 740 | **1.110** | 744 | 755 (1.112) |

| Bậc | Trích text TB | Quét regex TB | Phân loại TB | Trích: ms / 100k ký tự | Quét: ms / 100k ký tự |
|---|---|---|---|---|---|
| `goc` | 30 | 2 | 0,0 | — (chi phí cố định) | — |
| `20k` | 34 | 9 | 0,0 | 168 | 44 |
| `200k` | 71 | 65 | 0,0 | 35 | 32 |
| `1.5M` | 198 | 474 | 0,1 | 13 | **31** |
| `3M-cuoi` | 126 | 629 | 0,0 | 6 | 31 |

- **Quét regex tăng tuyến tính theo số ký tự: ~31 ms / 100.000 ký tự** (222 pattern), ổn định từ 200k trở lên. Với file lớn, quét regex
  chiếm phần lớn thời gian (1.5M: 474/672 ms).
- **Trích text có chi phí cố định theo định dạng** (mở zip, dựng cây XML, phân tích trang PDF) rồi tăng chậm hơn: với file nhỏ, docx/xlsx/
  pptx chậm hơn txt/csv/eml 2–6 lần; với file lớn các định dạng gần nhau trừ **pptx** (nhiều slide = nhiều phần XML, 1.281 ms ở 1.5M) và
  **pdf** (821–1.110 ms).
- **Phân loại không phụ thuộc kích thước** (< 0,1 ms) — chỉ xét các chỗ khớp.
- Bậc `3M-cuoi` chỉ chậm hơn `1.5M` một chút vì service **chỉ quét 2.000.000 ký tự đầu**.
- File lớn nhất trong giới hạn (1,5 triệu ký tự) vẫn xử lý **dưới ~1,3 giây** ở mọi định dạng.

## 3. Độ chính xác

| Bậc | txt | csv | docx | xlsx | pptx | pdf | eml | Tất cả | F1 "nhạy cảm" | Đoán thấp hơn | Đoán cao hơn |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `goc` | 87,5 % | 87,5 % | 87,5 % | 87,5 % | 87,5 % | 87,5 % | 87,5 % | **87,5 %** | 92,6 % | 6,3 % | 6,3 % |
| `20k` | 87,5 % | ← giống hệt mọi định dạng | | | | | | **87,5 %** | 92,6 % | 6,3 % | 6,3 % |
| `200k` | 87,5 % | | | | | | | **87,5 %** | 92,6 % | 6,3 % | 6,3 % |
| `1.5M` | 87,5 % | | | | | | | **87,5 %** | 95,3 %* | 6,3 % | 6,3 % |
| `3M-cuoi` | **25,0 %** | ← giống hệt mọi định dạng | | | | | | **25,0 %** | **0,0 %** | **75,0 %** | 0,0 % |

\* Bậc 1.5M chỉ có 16 mẫu (1 mẫu/lớp) nên F1 khác 64 mẫu của các bậc nhỏ; so theo cặp (dưới) thì không mẫu nào đổi mức.

So từng cặp (cùng mẫu, cùng định dạng) với bậc `goc`:

| Bậc | Số cặp | Đổi mức | Hạ mức | Nâng mức |
|---|---|---|---|---|
| `20k` | 448 | 0 % | 0 % | 0 % |
| `200k` | 448 | 0 % | 0 % | 0 % |
| `1.5M` | 112 | 0 % | 0 % | 0 % |
| `3M-cuoi` | 112 | **70,5 %** | **70,5 %** | 0 % |

- **Định dạng không ảnh hưởng tới kết quả phân loại:** cả 7 bộ trích text lấy ra đủ nội dung → mọi định dạng cho đúng cùng một mức.
- **Kích thước không ảnh hưởng — trong giới hạn 2.000.000 ký tự:** nội dung nhạy cảm bị "pha loãng" trong file lớn gấp 750 lần vẫn được
  phát hiện đúng mức (mức = mức cao nhất của chỗ khớp, không phụ thuộc mật độ).
- **Vượt giới hạn là điểm mù:** nội dung nhạy cảm nằm sau ký tự thứ 2.000.000 **không được quét** → mọi file nhạy cảm bị chấm Public
  (đoán thấp hơn 75 %, F1 nhạy cảm 0 %). Service có ghi cảnh báo "chỉ quét 2,000,000 ký tự đầu" cho cả 112 file, nhưng vẫn kết luận mức
  từ phần đã quét. Đây là cách **né phát hiện** đơn giản (chèn nhiều văn bản vô hại trước dữ liệu nhạy cảm).
- Accuracy 87,5 % ở đây cao hơn 81,6 % của toàn rawdata vì chỉ 64 mẫu — dùng để so giữa các bậc/định dạng, không thay cho số trên toàn tập.

## 4. Bộ nhớ

| Bậc | Working set cao nhất | Cấp phát TB / file | Cấp phát max / file | Định dạng cấp phát nhiều nhất |
|---|---|---|---|---|
| `goc` | 103 MB | 0,6 MB | 33 MB | pdf (2,3 MB TB) |
| `20k` | 113 MB | 1,9 MB | 9 MB | pdf (8,5 MB) |
| `200k` | 110 MB | 15 MB | 67 MB | pdf (67 MB) |
| `1.5M` | **201 MB** | 114 MB | 491 MB | **pdf (490 MB)** |
| `3M-cuoi` | 159 MB | 173 MB | 657 MB | pdf (655 MB) |

- Working set (RAM thật) cao nhất **~200 MB** kể cả với file 1,5 triệu ký tự — bộ thu gom rác trả lại bộ nhớ sau mỗi file.
- **PDF cấp phát rất nhiều** (~490 MB tạm thời cho 1 file PDF 1,4 MB / 500 trang) — với nhiều file PDF lớn xử lý **song song** trong
  service, RAM đỉnh có thể cao hơn nhiều so với đo tuần tự ở đây.

## 5. Kết luận

1. **Định dạng:** không ảnh hưởng độ chính xác; ảnh hưởng thời gian chủ yếu ở chi phí cố định (pptx, xlsx, docx chậm hơn txt/csv/eml với
   file nhỏ) và với file lớn (pptx, pdf chậm nhất).
2. **Kích thước:** thời gian tăng gần tuyến tính (~31 ms / 100k ký tự cho quét regex); độ chính xác **không đổi** trong giới hạn 2 triệu
   ký tự.
3. **Rủi ro cần xử lý:** nội dung sau 2.000.000 ký tự bị bỏ qua hoàn toàn. Hướng sửa (cần quyết định): quét theo khúc (chunk) toàn bộ
   file thay vì cắt; hoặc giữ giới hạn nhưng **không kết luận Public** cho file bị cắt (đánh dấu `needs_review` / gửi LLM / mức tối thiểu
   Internal), giống cách service đang xử lý file "quét chưa trọn" khi regex timeout.
4. **Bộ nhớ:** PDF lớn là loại tốn bộ nhớ nhất; nên cân nhắc giới hạn số file PDF lớn xử lý song song.

## 6. Giới hạn của phép đo

- 64 mẫu (16 ở 2 bậc lớn) — đủ để so sánh tương đối, không đủ thay số accuracy trên toàn tập.
- File sinh tự động có cấu trúc đơn giản (docx chỉ có đoạn văn, pptx 20 dòng/slide, xlsx 1 cột) — tài liệu thật có bảng, ảnh, header... có
  thể chậm hơn. Định dạng xls và doc/ppt/rtf (cần Tika) không có trong bộ này.
- Đo 1 luồng, trong tiến trình; số tuyệt đối dao động theo tải máy (xem `docs/danh-gia-lop-regex.md` mục 7).
- Chưa đo ảnh hưởng của kích thước lên **FileConfidence** (hệ số mật độ / số lạ có thể đổi điểm dù mức không đổi).
