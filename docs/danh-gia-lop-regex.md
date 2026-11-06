# Đánh giá lớp Regex của DlpService (không gọi LLM)

- **Ngày đo:** 2026-10-21 11:08 · **Công cụ:** `tools/eval/DlpBaseline` (gọi thẳng pipeline của service) + phân tích CSV.
- **Dữ liệu:** `D:\dlp-classifier\rawdata` — 12.743 mẫu có nhãn × 2 định dạng (docx, pdf) = **25.486 file**; 4 domain
  (finance_banking, healthcare, hr, insurance) × 4 mức (public, internal, confidential, top_secret) = 16 lớp. Đọc lỗi 0,
  quét chưa trọn (timeout) 0.
- **Cấu hình engine:** 222 pattern, công thức điểm chỗ khớp kiểu Go, `Scan:MinConfidence = 0,60`. Nhãn → mức:
  public → Public, internal → Internal, confidential → Confidential, top_secret → Restricted.
- **Kết quả thô:** `tools/eval/results/baseline-20261021-110840.md` + `.csv` (từng file).

## Phạm vi pipeline được đo

```
File ─► Trích text (FileReaderService) ─► Quét regex (RegexScanner) ─► Phân loại (ClassificationEngine) ─► Mức
```

Giống `FileWatcherService.ProcessFile` nhưng **không gồm**: chờ debounce 300 ms, băm SHA-256 chống quét trùng, tính S1 /
FileConfidence, ghi SQLite, ghi log, và **không gọi LLM**. File không có chỗ khớp nào → Public (không gọi engine).

## 1. Độ chính xác tổng thể

| Chỉ số | Giá trị |
|---|---|
| Accuracy (xếp đúng 4 mức) | **81,6 %** |
| Macro Precision / Recall / F1 | 81,9 % / 81,6 % / **81,7 %** |
| Weighted Precision / Recall / F1 | 82,0 % / 81,6 % / 81,7 % |
| Micro P = R = F1 (bài toán 1 nhãn → bằng Accuracy) | 81,6 % |
| Nhị phân "nhạy cảm" (Confidential + Restricted) — Accuracy | 93,5 % |
| Nhị phân "nhạy cảm" — Precision / Recall / F1 | 91,7 % / 95,8 % / **93,7 %** (TP 12.258 · FP 1.116 · FN 540 · TN 11.572) |

- **Macro** = trung bình cộng 4 mức (mỗi mức nặng như nhau). **Weighted** = trung bình theo số file thật của từng mức (4 mức gần
  bằng nhau nên ≈ macro).
- **Nhị phân "nhạy cảm"** là chỉ số chính của DLP: có bắt được file cần bảo vệ (Confidential/Restricted) hay không.

## 2. Theo 4 mức nhạy cảm

| Mức | Số file thật | Precision | Recall | F1 |
|---|---|---|---|---|
| Public | 6.400 | 87,0 % | 78,8 % | 82,7 % |
| Internal | 6.288 | 78,2 % | 78,5 % | 78,4 % |
| Confidential | 6.400 | 75,6 % | 84,3 % | 79,7 % |
| Restricted | 6.398 | 87,0 % | 84,8 % | 85,9 % |

Ma trận nhầm lẫn (hàng = nhãn thật, cột = dự đoán):

| Thật \ Dự đoán | Public | Internal | Confidential | Restricted | Tổng |
|---|---|---|---|---|---|
| Public | **5.041** | 952 | 389 | 18 | 6.400 |
| Internal | 641 | **4.938** | 541 | 168 | 6.288 |
| Confidential | 53 | 328 | **5.394** | 625 | 6.400 |
| Restricted | 61 | 98 | 811 | **5.428** | 6.398 |

## 3. Theo 16 lớp dữ liệu (domain × mức)

P/R/F1 của 1 lớp tính **trong cùng domain**, mức đó so với 3 mức còn lại (VD `hr__internal`: Precision = trong các file hr bị
đoán Internal, bao nhiêu % thật là Internal; Recall = trong file hr Internal thật, bao nhiêu % được đoán đúng).

| Lớp | Số file | Precision | Recall | F1 | Đoán thấp hơn | Đoán cao hơn |
|---|---|---|---|---|---|---|
| `finance_banking__public` | 1.600 | 92,2 % | 73,2 % | 81,6 % | 0,0 % | 26,8 % |
| `finance_banking__internal` | 1.600 | 78,3 % | 86,1 % | 82,0 % | 5,6 % | 8,4 % |
| `finance_banking__confidential` | 1.600 | 79,8 % | 94,4 % | 86,5 % | 2,2 % | 3,4 % |
| `finance_banking__top_secret` | 1.600 | 95,9 % | 88,7 % | **92,2 %** | 11,3 % | 0,0 % |
| `healthcare__public` | 1.600 | 85,6 % | 88,2 % | 86,9 % | 0,0 % | 11,8 % |
| `healthcare__internal` | 1.600 | 81,5 % | 61,9 % | 70,3 % | 12,9 % | 25,2 % |
| `healthcare__confidential` | 1.600 | 66,0 % | 63,9 % | **64,9 %** | 4,1 % | 32,0 % |
| `healthcare__top_secret` | 1.600 | 67,2 % | 83,3 % | 74,4 % | 16,7 % | 0,0 % |
| `hr__public` | 1.600 | 81,6 % | 76,1 % | 78,7 % | 0,0 % | 23,9 % |
| `hr__internal` | 1.488 | 76,2 % | 84,1 % | 80,0 % | 15,0 % | 0,9 % |
| `hr__confidential` | 1.600 | 77,1 % | 86,9 % | 81,7 % | 11,6 % | 1,4 % |
| `hr__top_secret` | 1.598 | 97,9 % | 82,6 % | 89,6 % | 17,4 % | 0,0 % |
| `insurance__public` | 1.600 | 89,6 % | 77,6 % | 83,1 % | 0,0 % | 22,4 % |
| `insurance__internal` | 1.600 | 77,5 % | 82,4 % | 79,9 % | 7,7 % | 9,9 % |
| `insurance__confidential` | 1.600 | 77,9 % | 91,9 % | 84,3 % | 5,9 % | 2,2 % |
| `insurance__top_secret` | 1.600 | 95,1 % | 84,8 % | 89,6 % | 15,2 % | 0,0 % |

(`hr__internal` 1.488 file thay vì 1.600: rawdata thiếu một số pdf / mẫu của lớp này.)

## 4. Theo domain, định dạng, độ khó

| Nhóm | Số file | Accuracy | Macro-F1 | F1 "nhạy cảm" |
|---|---|---|---|---|
| Domain finance_banking | 6.400 | 85,6 % | 85,6 % | 96,1 % |
| Domain healthcare | 6.400 | **74,3 %** | 74,1 % | 90,5 % |
| Domain hr | 6.286 | 82,4 % | 82,5 % | 93,4 % |
| Domain insurance | 6.400 | 84,2 % | 84,2 % | 94,7 % |
| Định dạng docx | 12.743 | 81,9 % | 82,0 % | 93,8 % |
| Định dạng pdf | 12.743 | 81,3 % | 81,3 % | 93,6 % |
| Ca khó (`is_borderline`) | 3.060 | 71,0 % | 71,1 % | 89,2 % |
| Ca thường | 22.426 | 83,1 % | 83,1 % | 94,3 % |

## 5. Thời gian xử lý

### 5.1. Toàn bộ pipeline

| Mục | Giá trị |
|---|---|
| Khởi động 1 lần: nạp 222 pattern / khởi tạo reader-scanner-engine | 934 ms / 902 ms |
| Làm nóng 20 file đầu (JIT, biên dịch regex — không tính vào số liệu) | 5.595 ms |
| **Thời gian toàn bộ vòng đo 25.486 file** (1 luồng, tuần tự) | **564,1 giây (≈ 9 phút 24 giây)** |
| Tổng 3 bước cộng dồn | 559,0 giây |
| Thông lượng | **45,6 file/giây** (docx 43,9 · pdf 47,4); 3,0 MB/giây đầu vào (docx 1,6 · pdf 4,5) |
| RAM cao nhất (working set) | 166 MB |

### 5.2. Từng file

| Định dạng | Bước | TB (ms) | p50 | p95 | p99 | Max |
|---|---|---|---|---|---|---|
| docx | Trích text | 21,2 | 12,9 | 42,3 | 49,0 | 123,8 |
| docx | Quét regex | 1,5 | 1,3 | 3,4 | 4,8 | 13,0 |
| docx | Phân loại | 0,0 | 0,0 | 0,0 | 0,1 | 3,8 |
| docx | **Tổng** | **22,8** | 14,3 | 44,9 | 52,0 | 124,8 |
| pdf | Trích text | 19,7 | 12,5 | 43,6 | 54,3 | 103,4 |
| pdf | Quét regex | 1,4 | 1,3 | 2,8 | 4,1 | 27,1 |
| pdf | Phân loại | 0,0 | 0,0 | 0,0 | 0,1 | 15,9 |
| pdf | **Tổng** | **21,1** | 13,9 | 45,8 | 57,2 | 106,9 |
| tất cả | **Tổng** | **21,9** | 14,0 | 45,3 | 54,8 | 124,8 |

Tỷ trọng: trích text **93 %**, quét regex 7 %, phân loại ≈ 0 %.

### 5.3. Theo 16 lớp

| Lớp | Số file | KB file TB | Ký tự text TB | Trích (ms) | Quét (ms) | Tổng TB (ms) | p95 (ms) |
|---|---|---|---|---|---|---|---|
| `finance_banking__public` | 1.600 | 68 | 2.132 | 20,9 | 1,6 | 22,6 | 45,7 |
| `finance_banking__internal` | 1.600 | 68 | 1.992 | 33,1 | 2,0 | 35,0 | 52,9 |
| `finance_banking__confidential` | 1.600 | 67 | 1.545 | 32,0 | 1,6 | 33,7 | 51,1 |
| `finance_banking__top_secret` | 1.600 | 68 | 1.900 | 20,0 | 1,6 | 21,6 | 44,1 |
| `healthcare__public` | 1.600 | 68 | 1.891 | 17,9 | 1,4 | 19,3 | 43,1 |
| `healthcare__internal` | 1.600 | 66 | 1.808 | 19,2 | 1,5 | 20,7 | 44,1 |
| `healthcare__confidential` | 1.600 | 67 | 1.273 | 19,2 | 1,2 | 20,4 | 43,0 |
| `healthcare__top_secret` | 1.600 | 68 | 2.041 | 18,5 | 1,6 | 20,1 | 42,4 |
| `hr__public` | 1.600 | 67 | 1.939 | 17,9 | 1,4 | 19,3 | 40,4 |
| `hr__internal` | 1.488 | 67 | 1.473 | 17,9 | 1,2 | 19,1 | 41,2 |
| `hr__confidential` | 1.600 | 67 | 1.472 | 17,1 | 1,2 | 18,4 | 40,9 |
| `hr__top_secret` | 1.598 | 68 | 2.067 | 19,3 | 1,6 | 21,0 | 44,1 |
| `insurance__public` | 1.600 | 67 | 1.763 | 17,0 | 1,3 | 18,3 | 38,4 |
| `insurance__internal` | 1.600 | 67 | 1.603 | 16,8 | 1,2 | 18,1 | 40,4 |
| `insurance__confidential` | 1.600 | 67 | 1.756 | 17,9 | 1,4 | 19,3 | 43,5 |
| `insurance__top_secret` | 1.600 | 68 | 2.234 | 22,0 | 1,8 | 23,9 | 51,0 |

## 6. Nhận xét

1. **Bắt file nhạy cảm tốt, phân mức chi tiết còn trung bình**: F1 nhị phân 93,7 % (recall 95,8 % — chỉ sót 540/12.798 file
   Confidential/Restricted) nhưng Accuracy 4 mức 81,6 %. Lỗi chủ yếu là **nhầm giữa 2 mức kề nhau**: Public→Internal (952),
   Restricted→Confidential (811), Confidential→Restricted (625).
2. **Lỗi theo hướng an toàn nhiều hơn hướng rò**: ở các lớp public, 11–27 % file bị đoán cao hơn (báo nhầm) nhưng 0 % bị đoán
   thấp hơn. Ngược lại, các lớp top_secret bị đoán thấp hơn 11–17 % — đây là rủi ro rò lọt cần ưu tiên (đặc biệt `hr__top_secret`
   17,4 %, `healthcare__top_secret` 16,7 %).
3. **Healthcare là domain yếu nhất** (Accuracy 74,3 %): `healthcare__confidential` F1 64,9 % — 32 % bị đoán lên Restricted (từ
   ngữ y tế/bệnh án dễ kích hoạt rule mức cao); `healthcare__internal` 25,2 % bị đoán lên.
4. **Ca khó (`is_borderline`) kém hơn ~12 điểm** Accuracy (71,0 % so với 83,1 %) — đúng kỳ vọng: đây là các mẫu ranh giới giữa 2 mức.
5. **docx và pdf gần như ngang nhau** (chênh 0,6 điểm) → bước trích text 2 định dạng cho nội dung tương đương.
6. **Thời gian bị chi phối bởi trích text (93 %)**; quét regex ~1,5 ms/file với 222 pattern, phân loại không đáng kể. Muốn nhanh hơn
   thì tối ưu/song song hoá bước trích text, không phải regex.

## 7. Giới hạn của phép đo

- **Thời gian phụ thuộc tải máy**: cùng công cụ, cùng dữ liệu, lần đo 2026-09-28 cho **8,6 ms/file** (116 file/giây), lần này
  **21,9 ms/file** — cả bước nạp rule cũng chậm tương ứng, nên chênh lệch do máy (chế độ nguồn Balanced, các ứng dụng khác đang
  chạy), không phải do code. Số tuyệt đối nên đo lại lúc máy rảnh, chế độ nguồn High performance; **tỷ trọng giữa các bước** thì ổn định.
- **Thời gian theo lớp bị lẫn với thời điểm đo**: file đo tuần tự theo thư mục; `finance_banking__internal/confidential` chậm hơn
  ~15 ms dù kích thước file và độ dài text tương đương các lớp khác → nhiều khả năng do tải máy lúc đó, không phải do nội dung.
- **Trích text gồm cả đọc đĩa**: file đã nằm trong cache của Windows nhanh hơn lần đọc đầu.
- **1 luồng tuần tự**: service thật xử lý theo sự kiện file thay đổi; thông lượng song song chưa đo.
- **Dữ liệu tổng hợp (synthetic)**, cân bằng 4 mức (~25 % mỗi mức). Dữ liệu thật thường lệch nhiều về Public/Internal → Precision các
  mức cao trên dữ liệu thật có thể thấp hơn.
- **Pipeline đo chưa gồm** băm SHA-256, tính S1/FileConfidence, ghi SQLite và debounce 300 ms của service.
