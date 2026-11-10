# Đánh giá đối chiếu độ chính xác: DlpService Linux ↔ Windows (lớp Regex, không gọi LLM)

- **Ngày đo:** 2026-11-10 · **Nhánh / commit:** `feature/linux` @ `1d22bd0` · **Kế hoạch:** GĐ2 bước 2.4 (`docs/ke-hoach-trien-khai-linux.md`)
- **Bản Linux được đo:** service thật `DlpService.Host.Linux` (`dlp-service.dll`, bản Debug), chạy qua `scripts/snapshot-ket-qua.py`
  — đi đúng đường chạy thật (watcher inotify → `ProcessFile` → trích text → regex → phân loại → S1/FileConfidence → ghi SQLite).
  LLM tắt (`Llm__Enabled=false`).
- **Mốc Windows để so:**
  1. `docs/snapshot/baseline-2-llm-tat.tsv` — ảnh kết quả **từng file** của 320 file mẫu (service Windows, LLM tắt).
  2. `docs/danh-gia-lop-regex.md` + `docs/danh-gia-service-e2e.md` — **chỉ số tổng** trên 25.486 file (2026-10-21; service thật
     dưới SCM cho cùng mức với DlpBaseline trên 100 % file).
- **Máy Linux:** Ubuntu 18.04 (kernel 4.15, glibc 2.27), 2 vCPU (i7-13700H), RAM 3,8 GB, .NET SDK 10.0.403.
- **Dữ liệu:** `~/dlp-rawdata` (bản chép của `D:\dlp-classifier\rawdata`) — 25.542 file docx/pdf, trong đó **25.485 file có
  nhãn** trong `_metadata.jsonl` (xem mục 4 về 1 file thiếu và 57 file ngoài nhãn).
- **Công cụ tính chỉ số:** `scripts/doi-chieu-do-chinh-xac.py` (file mới) — cùng định nghĩa chỉ số với `docs/danh-gia-lop-regex.md`.

## Kết luận nhanh

| Phép so | Kết quả |
|---|---|
| 320 file mẫu, so **từng file, từng cột** (mức, S1, S_final, hash, findings, needs_review…) | **Trùng 100 %** — `git diff` không có dòng nào khác |
| 25.485 file, so **chỉ số tổng** + ma trận nhầm lẫn + 16 lớp + nhóm | **Trùng mọi chỉ số** ở độ chính xác 0,1 %; ma trận nhầm lẫn lệch đúng 1 ô × 1 file = file bị thiếu khi chép |
| File không có kết quả / quét lỗi | **0** |

→ **Bản Linux phân loại giống bản Windows.** Coi 25.485/25.486 file là đủ để kết luận tương đương (quyết định của người dùng
ngày 10/11: không cần chép thêm file thiếu). **GĐ2 bước 2.4: Đạt.**

## 1. So từng file trên 320 file mẫu

Mẫu cố định: 16 thư mục × 2 đuôi × 10 file đầu theo tên (giống hệt lúc chụp ảnh Windows).

```bash
python3 scripts/snapshot-ket-qua.py --set Llm__Enabled=false --out linux-320.tsv
git diff --no-index docs/snapshot/baseline-2-llm-tat.tsv linux-320.tsv   # → không có khác biệt
```

| Cột so | Kết quả |
|---|---|
| `size`, `file_hash` (SHA-256 nội dung) | Trùng → dữ liệu đầu vào hai bên giống nhau |
| `labels` (mức + domain) | Trùng 320/320 |
| `s1`, `s_final` (6 chữ số thập phân) | Trùng 320/320 |
| `findings` (rule, số lần khớp, điểm, validator, vị trí) | Trùng 320/320 → trích text + regex cho cùng chỗ khớp, cùng offset |
| `status`, `source`, `needs_review` (19/320 = 5,9 %), `needs_ocr` | Trùng |
| `rules_ver`, `merge_ver` | Trùng → cùng bộ 222 pattern, cùng chính sách gộp |

Đây là phép so chặt nhất: không chỉ cùng kết luận mà cùng cả từng chỗ khớp và điểm số. Nghĩa là phần trích text (docx, pdf),
regex .NET và công thức điểm cho ra **cùng kết quả bit-by-bit** trên hai hệ điều hành.

## 2. So chỉ số tổng trên toàn bộ dữ liệu

```bash
python3 scripts/snapshot-ket-qua.py --per-dir 100000 --timeout 5400 --set Llm__Enabled=false --out linux-full.tsv
python3 scripts/doi-chieu-do-chinh-xac.py linux-full.tsv ~/dlp-rawdata
```

### 2.1. Tổng thể

| Chỉ số | Windows (25.486 file) | Linux (25.485 file) | Lệch |
|---|---|---|---|
| Accuracy (4 mức) | 81,6 % | 81,6 % | 0 |
| Macro Precision / Recall / F1 | 81,9 / 81,6 / 81,7 % | 81,9 / 81,6 / 81,7 % | 0 |
| Weighted Precision / Recall / F1 | 82,0 / 81,6 / 81,7 % | 82,0 / 81,6 / 81,7 % | 0 |
| "Nhạy cảm" (Confidential + Restricted) — Accuracy | 93,5 % | 93,5 % | 0 |
| "Nhạy cảm" — Precision / Recall / F1 | 91,7 / 95,8 / 93,7 % | 91,7 / 95,8 / 93,7 % | 0 |
| TP · FP · FN · TN | 12.258 · 1.116 · 540 · 11.572 | 12.258 · 1.116 · 540 · 11.571 | TN −1 (file thiếu) |

### 2.2. Theo 4 mức

| Mức | Số file Win / Linux | Precision | Recall | F1 | Lệch |
|---|---|---|---|---|---|
| Public | 6.400 / 6.400 | 87,0 % | 78,8 % | 82,7 % | 0 |
| Internal | 6.288 / 6.287 | 78,2 % | 78,5 % | 78,4 % | 0 (thiếu 1 file) |
| Confidential | 6.400 / 6.400 | 75,6 % | 84,3 % | 79,7 % | 0 |
| Restricted | 6.398 / 6.398 | 87,0 % | 84,8 % | 85,9 % | 0 |

### 2.3. Ma trận nhầm lẫn (hàng = nhãn thật, cột = dự đoán; "Win → Linux" nếu khác)

| Thật \ Dự đoán | Public | Internal | Confidential | Restricted |
|---|---|---|---|---|
| Public | 5.041 | 952 | 389 | 18 |
| Internal | 641 | **4.938 → 4.937** | 541 | 168 |
| Confidential | 53 | 328 | 5.394 | 625 |
| Restricted | 61 | 98 | 811 | 5.428 |

15/16 ô trùng tuyệt đối. Ô duy nhất lệch (Internal→Internal, −1) chính là file `fin_int_00484.pdf` không có trên Linux (Windows xếp
đúng Internal; bản docx cùng nội dung trên Linux cũng ra Internal).

### 2.4. Theo 16 lớp

Mọi lớp trùng Precision / Recall / F1 / tỷ lệ "đoán thấp hơn" / "đoán cao hơn" với bảng mục 3 của `docs/danh-gia-lop-regex.md`.

| Lớp | Số file Win / Linux | P | R | F1 | Thấp hơn | Cao hơn |
|---|---|---|---|---|---|---|
| `finance_banking__public` | 1.600 / 1.600 | 92,2 % | 73,2 % | 81,6 % | 0,0 % | 26,8 % |
| `finance_banking__internal` | 1.600 / **1.599** | 78,3 % | 86,1 % | 82,0 % | 5,6 % | 8,4 % |
| `finance_banking__confidential` | 1.600 / 1.600 | 79,8 % | 94,4 % | 86,5 % | 2,2 % | 3,4 % |
| `finance_banking__top_secret` | 1.600 / 1.600 | 95,9 % | 88,7 % | 92,2 % | 11,3 % | 0,0 % |
| `healthcare__public` | 1.600 / 1.600 | 85,6 % | 88,2 % | 86,9 % | 0,0 % | 11,8 % |
| `healthcare__internal` | 1.600 / 1.600 | 81,5 % | 61,9 % | 70,3 % | 12,9 % | 25,2 % |
| `healthcare__confidential` | 1.600 / 1.600 | 66,0 % | 63,9 % | 64,9 % | 4,1 % | 32,0 % |
| `healthcare__top_secret` | 1.600 / 1.600 | 67,2 % | 83,3 % | 74,4 % | 16,7 % | 0,0 % |
| `hr__public` | 1.600 / 1.600 | 81,6 % | 76,1 % | 78,7 % | 0,0 % | 23,9 % |
| `hr__internal` | 1.488 / 1.488 | 76,2 % | 84,1 % | 80,0 % | 15,0 % | 0,9 % |
| `hr__confidential` | 1.600 / 1.600 | 77,1 % | 86,9 % | 81,7 % | 11,6 % | 1,4 % |
| `hr__top_secret` | 1.598 / 1.598 | 97,9 % | 82,6 % | 89,6 % | 17,4 % | 0,0 % |
| `insurance__public` | 1.600 / 1.600 | 89,6 % | 77,6 % | 83,1 % | 0,0 % | 22,4 % |
| `insurance__internal` | 1.600 / 1.600 | 77,5 % | 82,4 % | 79,9 % | 7,7 % | 9,9 % |
| `insurance__confidential` | 1.600 / 1.600 | 77,9 % | 91,9 % | 84,3 % | 5,9 % | 2,2 % |
| `insurance__top_secret` | 1.600 / 1.600 | 95,1 % | 84,8 % | 89,6 % | 15,2 % | 0,0 % |

### 2.5. Theo domain, định dạng, độ khó

| Nhóm | Số file Win / Linux | Accuracy | Macro-F1 | F1 "nhạy cảm" |
|---|---|---|---|---|
| Domain finance_banking | 6.400 / 6.399 | 85,6 % | 85,6 % | 96,1 % |
| Domain healthcare | 6.400 / 6.400 | 74,3 % | 74,1 % | 90,5 % |
| Domain hr | 6.286 / 6.286 | 82,4 % | 82,5 % | 93,4 % |
| Domain insurance | 6.400 / 6.400 | 84,2 % | 84,2 % | 94,7 % |
| docx | 12.743 / 12.743 | 81,9 % | 82,0 % | 93,8 % |
| pdf | 12.743 / 12.742 | 81,3 % | 81,3 % | 93,6 % |
| Ca khó (`is_borderline`) | 3.060 / 3.060 | 71,0 % | 71,1 % | 89,2 % |
| Ca thường | 22.426 / 22.425 | 83,1 % | 83,1 % | 94,3 % |

Tất cả trùng với mục 4 của `docs/danh-gia-lop-regex.md`.

## 3. Độ ổn định trong lượt chạy toàn bộ

| Kiểm tra | Kết quả |
|---|---|
| File đưa vào / có dòng `scan_result` | 25.542 / 25.542 (gồm 57 file ngoài nhãn) |
| File có nhãn bị thiếu kết quả | **0** |
| `status` | 100 % `final`, `source = regex` |
| `needs_review` (vùng xám Router, LLM tắt) | 1.978 / 25.542 = 7,7 % |
| Thời gian (chép trước, service quét lượt đầu) | 3 phút 01 giây (≈ 141 file/giây) |
| RAM cao nhất (RSS, gồm cả script đo) | 188 MB |
| Service thoát lỗi | Không |

**Không dùng các số này để so tốc độ với Windows.** Cách đo khác nhau: ở đây file được chép vào trước rồi service quét lượt đầu
lúc khởi động, còn đo E2E trên Windows (`docs/danh-gia-service-e2e.md`) vừa chép vừa quét qua FileSystemWatcher, có debounce
300 ms, và thông lượng bị giới hạn bởi tốc độ chép. Máy cũng khác (Windows 20 luồng, Linux VM 2 vCPU). `needs_review` cũng
không so trực tiếp được với 1.971 cảnh báo của Windows, vì bên đó LLM bật nhưng `LlmSvc` dừng (đếm theo log, có job bị bỏ do đầy
hàng đợi). Trên 320 file mẫu, `needs_review` trùng từng file (mục 1).

## 4. Khác biệt về dữ liệu (không phải do code)

| Khác biệt | Ảnh hưởng | Xử lý |
|---|---|---|
| `finance_banking__internal/fin_int_00484.pdf` có trong `_metadata.jsonl` nhưng **không có** trong `~/dlp-rawdata` (lỗi khi chép từ Windows) | Linux thiếu 1/25.486 file; lệch 1 ô ma trận, mọi % không đổi | Người dùng quyết định coi là tương đương, không chép bổ sung |
| 57 file docx không có trong metadata (`hr__internal` 56, `hr__top_secret` 1) | Không ảnh hưởng: không có nhãn nên bị loại khỏi chỉ số, giống cách tính của Windows | — |

## 5. Phạm vi và giới hạn

- **Đã kiểm chứng:** lớp Regex + Router của service Linux thật, LLM tắt, 2 định dạng docx/pdf, 4 domain × 4 mức.
- **Chưa kiểm chứng:**
  - So **từng file** trên toàn bộ 25.485 file: repo không lưu CSV từng file của Windows (`tools/eval/results/` không có trên máy
    Linux). Muốn so: chạy `scripts/snapshot-ket-qua.py --per-dir 100000` trên Windows rồi `git diff --no-index` với `linux-full.tsv`.
  - Đường có LLM (`Llm__Enabled=true`), Tika native (GĐ5), file OCR.
  - Bản publish/gói `.deb` chạy dưới systemd. Lượt này chạy bản Debug bằng `dotnet dlp-service.dll`; code phân loại giống nhau,
    nhưng chưa chạy lại toàn bộ dữ liệu qua gói đã cài.
  - Các bản phân phối khác (GĐ6).

## 6. File liên quan

| File | Vai trò |
|---|---|
| `scripts/snapshot-ket-qua.py` | Chạy service thật trên tập con / toàn bộ rawdata, xuất `.tsv` mỗi file 1 dòng |
| `scripts/doi-chieu-do-chinh-xac.py` | **Mới.** Tính chỉ số độ chính xác từ `.tsv` + `_metadata.jsonl` |
| `docs/snapshot/baseline-2-llm-tat.tsv` | Ảnh Windows 320 file (mốc so từng file) |
| `docs/danh-gia-lop-regex.md` | Chỉ số Windows trên 25.486 file (mốc so tổng) |
