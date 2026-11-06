# Buổi 6 — Đo hiệu năng, baseline, validator, timeout, điểm S1, Router, lưu kết quả SQLite

Bản tóm tắt ngắn: `docs/tong-hop-thay-doi-buoi-06.md`. File này giữ chi tiết từng phần.

Ban đầu buổi này chỉ đo hiệu năng (mục 1-4, không sửa code service); sau đó mở rộng sang baseline độ chính xác và các
thay đổi trong service (mục 4b-4h).

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái | Mục |
|---|---|---|
| Chọn cách đo: từ bên ngoài ("hộp đen") thay vì benchmark trong code | ✅ Người dùng chọn | 1 |
| Script `tools/perf/Measure-DlpPerformance.ps1` | ✅ Xong + chạy thật | 2, 3 |
| Đo 60 file tuần tự + 750 file dồn dập (+ đo lại sau publish) | ✅ 810/810 file có kết quả, 0 timeout, 0 lần tràn buffer | 4 |
| Công cụ baseline `tools/eval/DlpBaseline` (precision/recall + tốc độ) | ✅ Xong, 25.486 file | 4b |
| 6 validator + tách checksum / bộ lọc | ✅ Xong — báo nhầm −43 %, recall giữ nguyên | 4c |
| Đánh giá "rule sang cấu hình" và "NonBacktracking/ReDoS" | ✅ Đánh giá xong | 4d |
| Log timeout, "CHƯA KẾT LUẬN ĐƯỢC" | ✅ Xong | 4e |
| Điểm candidate + S1 cấp file (giai đoạn 1-2) | ✅ Baseline v1; giai đoạn 3 chưa làm | 4f |
| Router ba vùng (T_high 0,80 / T_low 0,10) | ✅ Baseline v1 | 4g |
| Ghi `file_state`/`scan_result`/`finding` vào SQLite | ✅ Xong | 4h |
| Tìm ngưỡng tràn buffer (burst > 750 file) | Chưa làm | 6 |
| **Publish lại service** (các mục 4c, 4e, 4g, 4h) | ⏳ Cần quyền Admin | — |

---

## 1. Chọn cách đo

| | Cách A: Benchmark trong code | **Cách B: Đo từ bên ngoài** (đã chọn) |
|---|---|---|
| Trả lời | Bước nào (đọc / quét / phân loại) chậm nhất? | Người dùng lưu file xong bao lâu thì có kết quả? Tốn bao nhiêu CPU/RAM? |
| Cách làm | Console app gọi thẳng `RuleLoader`, `RegexScanner`... | Thả file vào `watch-folder`, đọc file log để bấm giờ |
| Đo được debounce, watcher, ghi log? | Không | Có |

Người dùng chỉ cần hiệu năng thực tế, không cần tìm bottleneck, nên chọn **Cách B**.

---

## 2. Hai kịch bản đo

Ví dụ dễ hình dung: service là **quầy thu ngân**, mỗi file là **1 khách**.

| | Kịch bản 1: Tuần tự | Kịch bản 2: Dồn dập (burst) |
|---|---|---|
| Giống như | Quầy lúc vắng: 1 khách xong mới cho khách sau vào | Giờ cao điểm: 750 khách ùa vào cùng lúc |
| Cách làm | Bấm giờ → copy 1 file → chờ log có kết quả → dừng giờ → file tiếp theo | Bấm giờ → copy liền 750 file → chờ log đủ 750 kết quả → dừng giờ |
| Trả lời | "1 file xong nhanh cỡ nào?" | "Chịu được bao nhiêu file cùng lúc?" |
| Chỉ số chính | Độ trễ (ms): trung bình, p50, p95, max | File/giây, MB/giây, CPU, RAM, số lần buffer tràn |

**p50** = một nửa số file xong nhanh hơn mức này (trải nghiệm thường ngày). **p95** = 95% số file xong nhanh hơn mức
này (cho thấy các trường hợp chậm).

Service có thể nhanh khi vắng nhưng nghẽn khi đông (hoặc ngược lại), nên cần đo cả 2.

---

## 3. Script `tools/perf/Measure-DlpPerformance.ps1`

### Cách chạy

Service phải đang chạy (`sc start DlpService`), rồi:

```powershell
.\tools\perf\Measure-DlpPerformance.ps1 -SequentialCount 60 -BurstCount 750
```

| Tham số | Mặc định | Ý nghĩa |
|---|---|---|
| `-SampleFolder` | `D:\crawl data\output\filled_forms\mau-don` | Thư mục file mẫu (750 file: 375 `.pdf` ~153KB, 375 `.docx` ~16KB, tổng 61.8MB) |
| `-LogFolder` | `D:\Service-DLP\publish\logs` | Nơi service ghi log (service đang chạy từ `publish\`) |
| `-WatchFolder` | `D:\Service-DLP\watch-folder` | Thư mục service theo dõi; script tạo tạm `_perf-test\` bên trong rồi tự xoá |
| `-SequentialCount` | 60 | Số file kịch bản 1, chia đều theo đuôi file, chọn ngẫu nhiên |
| `-BurstCount` | 750 | Số file kịch bản 2; lớn hơn số file mẫu thì lấy xoay vòng |
| `-TimeoutSec` / `-BurstTimeoutSec` | 30 / 900 | Chờ tối đa cho 1 file / cả đợt burst |
| `-PollIntervalMs` | 50 | Bao lâu đọc log 1 lần — cũng là sai số của phép đo |

Kết quả in ra màn hình + lưu chi tiết từng file vào `tools/perf/results/perf-<ngày giờ>.csv` (chỉ tên file và số đo,
**không** có nội dung file).

### Các bước bên trong

```
Bước 0: kiểm tra service đang chạy, thư mục log, chọn file mẫu
Bước 1: đo CPU/RAM lúc rảnh (~6 giây) làm mức nền
Bước 2: kịch bản tuần tự
Bước 3: kịch bản dồn dập (lấy mẫu CPU/RAM song song)
Bước 4: dọn _perf-test\, in báo cáo, xuất CSV
```

### 5 vấn đề phát hiện khi thiết kế và cách xử lý

| # | Vấn đề | Cách xử lý |
|---|---|---|
| 1 | Timestamp trong log chỉ tới **giây** (`HH:mm:ss`), trong khi debounce chỉ 300ms → dùng giờ trong log sẽ sai tới ±1 giây | Script **tự bấm giờ** bằng `Stopwatch`, đọc log mỗi 50ms. Sai số ~±50ms, không phải sửa service |
| 2 | Service bỏ qua file có nội dung không đổi (`_lastContentHash`) → copy lại file cũ cùng tên sẽ không có dòng log nào, script chờ mãi | Mỗi file test có **tên duy nhất**: `seq-<mã lượt>-0001.pdf`, `burst-<mã lượt>-0001.docx` |
| 3 | Log tự tách file khi quá 5MB (`.1.log`, `.2.log`) → có thể lỡ dòng nếu chỉ đọc 1 file | Hàm `Read-NewLogLines` phát hiện file log mới, đọc nốt phần cuối file cũ rồi mới chuyển |
| 4 | `Get-Process` **không đọc được CPU** của service chạy bằng tài khoản hệ thống (khi không có quyền admin) | Dùng performance counter `\Process(DlpService)\% Processor Time`. Mỗi lần đọc mất ~1 giây nên chạy trong **background job** (tiến trình PowerShell phụ), không làm chậm vòng lặp chờ log |
| 5 | 1 file sinh nhiều dòng log (chi tiết từng chỗ khớp, "mức ...") | Hàm `Get-TestResult` chỉ nhận 3 loại dòng kết quả cuối: `: sạch`, `: phát hiện`, `Đọc thất bại` |

### 3 lỗi PowerShell 5.1 gặp khi viết script (đáng nhớ)

| Lỗi | Nguyên nhân | Cách tránh |
|---|---|---|
| `Argument types do not match` | Biến PowerShell **không phân biệt hoa thường**: `$rows = @($Rows)` là gán đè chính tham số | Đặt tên biến cục bộ khác hẳn tham số |
| `Argument types do not match` | `@($list)` với `$list` là `List[object]` bị lỗi trên PS 5.1 | Dùng `@(foreach ($x in $list) { $x })` hoặc `$list.ToArray()` |
| Chữ Việt bị hỏng | PS 5.1 đọc file `.ps1` không có BOM theo mã ANSI | Lưu script dạng **UTF-8 with BOM** (VS Code: Save with Encoding) |

Thêm 1 bẫy nhỏ: trong chuỗi `"$newName: 523 ms"`, PowerShell hiểu `$newName:` là biến có phạm vi (như `$script:x`).
Phải viết `"$($newName): 523 ms"`.

---

## 4. Kết quả đo (lượt `20260928-094813`)

**Môi trường:** Intel Core i7-13700H (20 luồng), RAM 13.7GB. Bản service được đo: `publish\` build lúc 25/09 16:24
(sau đó source chỉ sửa thêm `FileLoggerProvider.cs`, không liên quan tốc độ quét). 64 file rule.

### Kịch bản 1 — Tuần tự (60 file: 30 PDF + 30 DOCX)

| Nhóm | Trung bình | p50 | p95 | Max |
|---|---|---|---|---|
| Tất cả | 368 ms | 380 ms | 409 ms | 433 ms |
| PDF (~150KB) | 377 ms | 383 ms | 418 ms | 433 ms |
| DOCX (~16KB) | 359 ms | 352 ms | 390 ms | 395 ms |

- Trong ~370ms có **300ms là debounce cố ý**. Thời gian thật để đọc + quét + phân loại: **~30–130ms/file**.
- PDF lớn hơn DOCX ~10 lần nhưng chỉ chậm hơn ~20ms. Không có file nào chậm bất thường.
- **Kết luận:** người dùng lưu file xong, chưa tới nửa giây đã có kết quả.

### Kịch bản 2 — Dồn dập (750 file, 61.8MB cùng lúc)

| Chỉ số | Kết quả |
|---|---|
| Thời gian copy 750 file | 1.4 giây |
| Xử lý xong cả 750 file | **6.2 giây** |
| Thông lượng | **~122 file/giây, ~10 MB/giây** |
| Thời điểm từng file xong (tính từ lúc bắt đầu thả) | p50 3.0 giây, p95 5.8 giây |
| Buffer tràn | **0 lần** |
| Timeout | 0 |

| Tài nguyên | Lúc rảnh | Lúc burst |
|---|---|---|
| CPU (% cả máy) | 0% | TB 8.2%, cao nhất 12.4% (~2.5 luồng / 20) |
| RAM (Working Set - Private) | 42 MB | TB 139 MB, cao nhất 166 MB |

- **Kết luận:** 750 file cùng lúc → xử lý hết trong ~6 giây, CPU tối đa ~12%, RAM lên ~166MB.
- Lo ngại buffer watcher 64KB bị tràn (buổi 5, mục 3) **không xảy ra** ở mức 750 file.

### Lưu ý khi đọc số liệu

- Sai số độ trễ ~±50ms (chu kỳ đọc log).
- CPU/RAM chỉ lấy mẫu ~1 lần/giây → burst 6 giây chỉ có 7 mẫu, có thể lỡ đỉnh rất ngắn.
- RAM lúc rảnh giữa các lần đo lệch nhau nhiều (lần chạy thử: 4.5MB, lần này: 42MB) vì Windows tự thu gọn RAM của tiến
  trình rảnh lâu. Nên so sánh con số **lúc burst**.
- Chi tiết từng file: `tools/perf/results/perf-20260928-094813.csv`. **Giữ file này làm mốc** để so với các lần đo sau.

### Đo lại sau khi publish bản mới (lượt `20260928-100137`)

Publish lại lúc 28/09 10:00 (so với bản cũ 25/09 16:24, source chỉ khác ở `FileLoggerProvider.cs`). Cùng cấu hình
60 tuần tự + 750 burst.

| Chỉ số | Bản cũ (094813) | Bản mới (100137) | Chênh lệch |
|---|---|---|---|
| Tuần tự — TB / p50 / p95 | 368 / 380 / 409 ms | 355 / 340 / 393 ms | −13 ms TB, trong sai số ±50ms |
| Burst — tổng thời gian | 6.2 giây | 6.3 giây | Như nhau |
| Burst — thông lượng | 121.6 file/s, 10.02 MB/s | 119.9 file/s, 9.87 MB/s | Như nhau |
| CPU burst TB / max | 8.2% / 12.4% | 8.3% / 13.5% | Như nhau |
| RAM burst TB / max | 139 / 166 MB | 66 / 82 MB | Thấp hơn, nhưng xem ghi chú |
| Timeout / buffer tràn | 0 / 0 | 0 / 0 | — |

**Kết luận:** hiệu năng **không đổi**, đúng như dự đoán vì code quét/phân loại giữa 2 bản giống nhau. Chênh lệch RAM
nhiều khả năng do thời điểm .NET dọn rác (GC) và Windows thu gọn RAM khác nhau giữa 2 tiến trình, **không phải** do
code. Muốn kết luận về RAM cần đo nhiều lượt. Lấy `perf-20260928-100137.csv` làm **mốc mới**.

---

## 4b. Baseline regex-only trên rawdata (`tools/eval/DlpBaseline`)

Console app gọi thẳng pipeline của service (đọc → quét → phân loại) trên **12.743 mẫu × 2 định dạng = 25.486 file** của
`D:\dlp-classifier\rawdata`, so với nhãn trong `_metadata.jsonl`. Chạy:

```
dotnet run -c Release --project tools/eval/DlpBaseline -- "D:\dlp-classifier\rawdata" [--limit N] [--format docx|pdf|both]
```

Báo cáo đầy đủ: `tools/eval/results/baseline-20260928-105532.md` (+ `.csv` từng file, chỉ mã rule + số đo).

### Độ chính xác

| Chỉ số | Precision | Recall | F1 |
|---|---|---|---|
| **A. Nhị phân "nhạy cảm"** (confidential + top_secret) | **79.6 %** | **96.5 %** | 87.2 % |
| B. 4 mức | Accuracy **73.9 %**, Macro-F1 73.5 % | | |
| C. Phát hiện PII (`contains_pii`) | 63.5 % | 97.3 % | 76.8 % |

- **Recall cao, precision là điểm yếu:** bỏ sót ít (451 file nhạy cảm bị lọt), nhưng báo nhầm nhiều (3.160 file).
- **Nhầm nhiều nhất: đẩy mức LÊN.** Chỉ 49.5 % file public được xếp đúng Public; 1.735 file public và 1.225 file internal
  bị xếp Confidential.
- DOCX và PDF cho kết quả gần như nhau → trích text PDF không làm mất thông tin.
- Healthcare kém nhất (accuracy 67.5 %); ca khó (`is_borderline`) accuracy 66.1 %.

### Rule gây báo nhầm (phân tích trên bản docx)

| Rule | Bắn trên file KHÔNG có PII | Có mặt trong file public bị xếp Confidential/Restricted |
|---|---|---|
| `email_001` | 1.033 file (17 %) | **83 %** (733/880) |
| `vn_name_001` | **2.713 file (45 %)** | 41 % |
| `hr_internal_business_001`, `business_internal_001`, `business_internal_specific_001` | 20–24 % mỗi rule | < 10 % |

**4 rule từng lo ngại hoạt động tốt:** `vn_id_001` (CCCD), `phone_001`, `bank_account_001` chỉ bắn trên file không có PII
ở mức 0–1 %. `tax_code_business_001` bắn 12 % trên file không PII, nhưng đó là MST doanh nghiệp (vốn không phải PII).

### Tốc độ (1 luồng, i7-13700H)

| Bước | TB / file | Tỷ trọng |
|---|---|---|
| Trích text (docx ~36KB, pdf ~95KB) | 8.7 ms | **92 %** |
| Quét regex (222 pattern) | 0.7 ms | 8 % |
| Phân loại | ~0 ms | 0 % |
| **Tổng** | **9.4 ms** (p95 14.4 ms) | ~106 file/giây/luồng |

- Nạp rule 85 ms. RAM cao nhất 122 MB. Cấp phát ~1.3 MB bộ nhớ/file (32.7 GB cho 25.486 file) → GC làm việc nhiều.
- **Trả lời câu hỏi "vì sao CPU 8-12% khi burst" (mục 4):** regex chỉ chiếm 8 %, **trích text docx/pdf chiếm 92 %** thời gian xử lý.

---

## 4c. Cài validator: CCCD, MST, Luhn, IBAN, entropy secret, email/SĐT

### 6 validator (thư mục mới `Scanning/Validation/`)

| Tên trong JSON | Loại | Kiểm tra gì | Gắn vào |
|---|---|---|---|
| `vn_cccd_prefix` | Checksum | 3 số đầu là 1 trong 63 mã tỉnh; số thứ 4 (giới tính + thế kỷ) 0-3; năm sinh ≤ năm hiện tại | `cccd` p1 |
| `mst` | Checksum | Số thứ 10 = 10 − (Σ 9 số đầu × 31,29,23,19,17,13,7,5,3) mod 11; nhận 10 hoặc 13 số | `tax-code-business` p1-3, `tax-code-personal-bare/labeled` |
| `luhn` | Checksum | Đã có từ trước | 8 rule thẻ |
| `iban` | Checksum | Chuyển 4 ký tự đầu xuống cuối, chữ → số (A=10..Z=35), mod 97 = 1 | `iban` p1-2 |
| `secret_entropy` | Bộ lọc | Giá trị sau nhãn dài ≥ 8, không phải biến mẫu, entropy Shannon ≥ 3,0 bit/ký tự, ≥ 2 loại ký tự | `credentials` p1, p2, p6 |
| `email_personal` | Bộ lọc | Đúng cấu trúc + không phải hộp thư chung (tuyendung@, phongnhansu@, pr@...) | `email` p1-3 |
| `vn_phone` | Bộ lọc | Đầu số nhà mạng đang cấp / mã vùng hợp lệ; loại số giả (0900000000, 0912345678) | `phone-vn` p1-4 |

**Thay đổi quan trọng ở engine:** trước đây mọi rule có validator đều được coi là "đủ bằng chứng" để qua level_gate
(`RegexScanner`: `hasValidatedMatch = Validator != null`). Nay chỉ validator **checksum** mới được tính (cờ
`ValidatorEntry.IsChecksum` → `SensitivePattern.ValidatorIsChecksum`) — nếu không, gắn bộ lọc email/SĐT sẽ làm gate
không bao giờ hạ mức email/SĐT nữa và báo nhầm TĂNG.

Sửa thêm: regex IBAN p2 không nhận nhóm cuối ngắn (`... 7654 32` bị cắt mất `32`) → IBAN thật sẽ sai mod-97. Đã thêm
`(?:\s[A-Z0-9]{1,3})?`.

Test: console tạm 54 trường hợp đúng/sai biết trước (MST thật 0100109106, IBAN mẫu chuẩn GB82/DE89, CCCD đúng/sai mã
tỉnh...) — đúng cả 54, đã xoá console. Build 0 warning, 0 error.

### Kết quả baseline trước / sau (25.486 file)

| Chỉ số | Trước (105532) | Sau (143817) | Thay đổi |
|---|---|---|---|
| A. "Nhạy cảm" — Precision | 79.6 % | **87.2 %** | +7.6 điểm |
| A. "Nhạy cảm" — Recall | 96.5 % | 96.5 % | không đổi (FN 451 → 453) |
| A. Số file báo nhầm (FP) | 3.160 | **1.806** | −43 % |
| B. Accuracy 4 mức | 73.9 % | **77.4 %** | +3.5 điểm |
| B. Recall mức Public | 49.5 % | 60.5 % | +11 điểm |
| B. Public bị xếp Confidential | 1.735 | 601 | −65 % |
| C. PII — Precision / Recall | 63.5 % / 97.3 % | 66.2 % / 97.2 % | |

Theo rule (bản docx): `email_001` bắn trên file public **733 → 136**, trên file không PII 1.033 → 338.

### Recall giảm ở từng rule là do DỮ LIỆU TỔNG HỢP, không phải lỗi validator

| Rule | File CÓ PII mất phát hiện | Nguyên nhân (kiểm tra 3 số đầu, không in số đầy đủ) |
|---|---|---|
| `phone_001` | 331 | Bộ dữ liệu sinh SĐT với đầu số **chưa từng được cấp** ở VN: 095, 057, 053, 054, 080 |
| `vn_id_001` | 60 | Bộ dữ liệu sinh CCCD với số thứ 4 = 4-9 (thế kỷ 22 trở đi) hoặc năm sinh ở tương lai |
| `tax_code_business_001` | 1.825 | MST trong bộ dữ liệu là số ngẫu nhiên, không có chữ số kiểm tra đúng |

→ Trên tài liệu thật, những số này là số **không tồn tại**, validator loại là đúng. Recall tổng (chỉ số A) không đổi vì
các file đó vẫn bị bắt nhờ tín hiệu khác. **Không đo được recall thật của MST/SĐT/CCCD trên rawdata** — cần mẫu có số thật.

### Sự cố khi đo: máy ngủ giữa chừng

Lần chạy "sau" có 1 file PDF ghi nhận 11.027 giây trích text: Windows ghi nhận thoát Modern Standby lúc 14:35:56 →
laptop ngủ ~3 giờ, `Stopwatch` vẫn đếm. Chỉ số độ chính xác không bị ảnh hưởng; số liệu tốc độ của lần chạy này (cột TB/Max
của pdf) **không dùng được** — p50/p95 và bản docx vẫn bình thường (tổng 9,1 ms/file). Đã sửa `DlpBaseline`: tiến độ in
`hh:mm:ss` (trước bỏ mất giờ), tên file/báo cáo lấy giờ bắt đầu (trước là giờ kết thúc).

---

## 4d. Đánh giá 2 task "rule sang cấu hình" và "NonBacktracking/timeout/ReDoS"

| Task | Kết luận | Lý do |
|---|---|---|
| Chuyển rule sang cấu hình (id, pattern, category, base_score, validator, context_boost/penalty, hard_rule, llm_policy) | **Phần lớn đã có** — không làm thành task lớn | 63 file JSON đã có id/regex/category/confidence+weight/validators/keywords/negation/level_gate. Thiếu: hệ số boost (0,15/0,05/0,30) còn ghi cứng trong `RegexScanner`; `hard_rule`/`llm_policy` phụ thuộc thiết kế tích hợp LLM chưa chốt → hoãn, tránh thêm "dead field". Nên làm thay: cảnh báo khi JSON có trường lạ |
| Bật NonBacktracking hoặc timeout; test ReDoS | **Làm, nhưng KHÔNG bật NonBacktracking cho mọi regex** | Mọi regex đã có timeout 1s. Thử nghiệm .NET 10.0.12: NonBacktracking + timeout trả **match sai** (dài 100.001 ký tự, không khớp regex) trên chuỗi ~160k ký tự — tái hiện 3/3; `credit-card.json` dùng lookaround (không tương thích); regex chỉ chiếm 8% thời gian và .NET 10 tự tối ưu (regex email không bị ReDoS với 160k ký tự). Lỗ hổng thật: timeout bị NUỐT IM LẶNG |

Thứ tự đề xuất: (1) log timeout + không kết luận khi quét chưa trọn — **đã làm, mục 4e**; (2) test ReDoS tự động cho
mọi pattern; (3) giới hạn tổng thời gian quét/file; (4) NonBacktracking chỉ cho pattern mà (2) phát hiện nguy hiểm.

## 4e. Log timeout, không kết luận khi quét chưa trọn

**Vấn đề:** `RegexScanner.Scan` bắt `RegexMatchTimeoutException` rồi bỏ qua im lặng → rule đó mất tác dụng với file,
file có thể bị log "sạch" → kẻ xấu chèn chuỗi làm regex chạy quá lâu để né phát hiện. `CompoundRuleModule` không bắt
timeout của regex loại trừ → exception lan lên engine → engine bỏ CẢ module compound (mất mức nâng SECRET).

**Quyết định (người dùng):** có rule bị timeout → cảnh báo, **không gắn mức**, log tên rule lỗi + đường dẫn file, log
"CHƯA KẾT LUẬN ĐƯỢC".

| File | Thay đổi |
|---|---|
| `Scanning/ScanResult.cs` (mới) | `Matches`, `TimedOutPatterns`, `IsComplete` |
| `Scanning/RegexScanner.cs` | `Scan()` trả `ScanResult`; `catch` ghi tên rule vào `TimedOutPatterns` (chỗ khớp tìm được trước đó vẫn giữ) |
| `Watching/FileWatcherService.cs` → `ProcessFile` | Quét chưa trọn: 2 dòng Warning (dưới), return trước bước phân loại, xoá hash (`TryRemove(KeyValuePair)`) để lần quét sau được thử lại |
| `Classification/Modules/CompoundRuleModule.cs` | `IsMatchOrFalseOnTimeout`: timeout regex loại trừ = coi như KHÔNG loại trừ (không chắc thì vẫn xét compound) |
| `tools/eval/DlpBaseline` | Cột CSV `scan_complete`, `timed_out_rules`; báo cáo đếm số file quét chưa trọn |
| `tools/perf/Measure-DlpPerformance.ps1` | Nhận dạng dòng "CHƯA KẾT LUẬN ĐƯỢC" (kind `chua-ket-luan`) |

Log mới (chỉ tên rule, không có giá trị trong file):

```
warn: [DLP] <đường dẫn file>: quét CHƯA TRỌN — 1 rule quá thời gian: <tên rule>
warn: [DLP] <đường dẫn file>: CHƯA KẾT LUẬN ĐƯỢC — không gắn mức, cần xem lại (N chỗ khớp tìm được trước khi dừng)
```

Placeholder blocklist timeout (`RegexScanner.IsPlaceholder`) đã an toàn từ trước (coi như không phải placeholder → giữ
chỗ khớp), không sửa.

**Test:**
- Console tạm, rule "độc" `(a+)+b` timeout 50ms + rule thường, chạy qua `FileWatcherService` thật trên thư mục tạm:
  file chỉ có chuỗi độc (0 chỗ khớp) → trước đây sẽ log "sạch", nay log "CHƯA KẾT LUẬN ĐƯỢC"; file bình thường/sạch
  log y như cũ. Đã xoá console.
- `DlpBaseline` toàn bộ 25.486 file: **0 file khác** lần trước (mức dự đoán + rule bắn), 0 file quét chưa trọn.
  Lần chạy này máy không ngủ: 168,9 giây cho cả vòng đo.
- Build 0 warning, 0 error.

---

## 4f. Điểm S1 — giai đoạn 1 (tính song song) và 2 (chọn ngưỡng)

Kiến trúc đích lưu tại `docs/kien-truc-trien-khai.md` (Regex service + LLM thẩm định; S1 là điểm regex cấp file mà
Router dùng để chọn file gửi LLM). Kế hoạch 3 giai đoạn (người dùng chốt): **tính song song → chọn ngưỡng trên dữ liệu
→ chuyển sang dùng S1**. Chưa gộp theo cột bảng.

### Giai đoạn 1 — tính song song (không đổi hành vi)

| Thành phần | Công thức |
|---|---|
| Điểm candidate (`Scanning/CandidateScorer.cs`, `ScanMatch.Score`) | `[(confidence ?? 0,75) × weight + boost từ khoá (≤ 0,30) + 0,10 nếu qua checksum − 0,30 nếu negation hạ mức]` giới hạn 0-1, rồi `× doc_context_discount_factor` nếu tài liệu có từ khoá hướng dẫn/mẫu |
| S1 cấp file (`Classification/FileScorer.cs`) | Mỗi rule: bỏ trùng giá trị → noisy-OR 5 giá trị điểm cao nhất → × hệ số mật độ (0,7-1). Mỗi mức L: S1(≥L) = noisy-OR điểm các rule có mức ≥ L |

- `doc_context_discount_keywords/factor` (5 rule: credentials, cvv, otp ×3) được đọc lần đầu — trước là dead field.
- `ScanMatch.Confidence` và bypass level_gate giữ nguyên (chỉ tách phần boost từ khoá ra hàm `KeywordBoost` dùng chung).
- Service log S1 ở mức **Debug** (không hiện khi chạy thật vì `appsettings.json` mặc định Information).

### Giai đoạn 2 — chọn ngưỡng (`tools/eval/DlpBaseline/S1Tuning.cs`)

- Chia theo **id mẫu** (hash FNV-1a, cố định): train 70% (17.806 file), test 30% (7.680 file); docx và pdf cùng mẫu luôn chung
  1 phía (tránh "lộ đề").
- Dò lưới 19³ = 6.859 bộ ngưỡng (0,05 → 0,95). Ràng buộc: recall "nhạy cảm" ≥ cách hiện tại trên train; mục tiêu: F1
  "nhạy cảm" cao nhất, hoà thì Macro-F1 4 mức. Ngưỡng lưu ra `results/s1-thresholds-<ngày giờ>.json`.

**Kết quả lần đầu (baseline-20260928-160654), trên tập TEST:**

| Cách | Precision | Recall | F1 | FP | FN | Accuracy 4 mức |
|---|---|---|---|---|---|---|
| Hiện tại | 87,4 % | **96,7 %** | 91,8 % | 540 | **127** | 77,2 % |
| S1 (Internal ≥ 0,60, Confidential ≥ 0,05, Restricted ≥ 0,05) | **91,8 %** | 95,0 % | **93,4 %** | **328** | 193 | 76,8 % |

- **Không bộ ngưỡng nào giữ được recall** như cách hiện tại (ràng buộc không đạt) → dùng bộ F1 cao nhất.
- Ngưỡng Confidential/Restricted chạm **đáy lưới 0,05** = "cứ có 1 rule mức đó bắn là tính" → S1 hiện tại gần như chỉ
  phân biệt có/không, điểm dồn về 2 đầu (0 hoặc gần 1). Vùng xám 48% là hệ quả của ngưỡng sát 0, **chưa dùng được**.
- S1 trung bình theo nhãn tách tốt ở mức Restricted (Restricted 0,69 so với Confidential 0,08) nhưng các file public/
  internal vẫn có S1 Confidential ~0,05-0,12 → phần báo nhầm còn lại nằm ở điểm candidate chứ không ở ngưỡng.

→ **Chưa làm giai đoạn 3**: S1 giảm 39% báo nhầm nhưng tăng 52% bỏ sót trên test — cần chỉnh lại S1 trước.

## 4g. Router ba vùng — baseline v1

**Công thức baseline (chốt, cải thiện sau):**

- Điểm candidate + S1 cấp file: giữ nguyên như mục 4f (baseline v1, chưa gộp theo cột).
- **Mức nhạy cảm vẫn do regex/engine gán** (không dùng S1 để quyết định mức — mục 4f cho thấy làm vậy giảm recall).
- **Độ tin cậy** = S1 của đúng mức đã gán (Confidential → S1(≥ Confidential); Public có chỗ khớp → S1(≥ Internal)).
- Router (`Classification/ScoreRouter.cs`): không khớp → sạch/final; độ tin cậy ≥ T_high → chắc chắn/final;
  T_low ≤ độ tin cậy < T_high → **vùng xám → cần LLM** (chưa có LLM: log Warning "cần LLM thẩm định, needs_review");
  < T_low → tín hiệu yếu/final. Ngưỡng đọc từ `appsettings.json` mục `"Score"` (sai/thiếu → mặc định 0,90/0,30 + cảnh báo).

**Đo trên rawdata (baseline-20260928-162043):**

| Ngưỡng | Gửi LLM (test) | Bắt lỗi (file regex xếp sai rơi vào vùng xám) | Vùng chắc chắn xếp đúng |
|---|---|---|---|
| Mặc định kiến trúc 0,90 / 0,30 | 15,9 % — vượt mục tiêu 2-10 % | ~32 % (train) | 82 % |
| **Chọn làm baseline: 0,80 / 0,10** | **10,2 %** | **22,1 %** (387/1.753) | 79,6 % |

- Chọn trên train: trong khoảng gửi LLM 2-10 % (kiến trúc mục 4.7), bắt được nhiều lỗi nhất. Đã đặt vào `appsettings.json`.
- Router **có tác dụng định hướng**: vùng xám regex chỉ đúng 51,9 %, vùng chắc chắn đúng 82 % → lỗi dồn về vùng xám.
- Nhưng **S1 còn yếu**: ~18-20 % file "chắc chắn" vẫn bị xếp sai (phần lớn public ↔ internal ↔ confidential do nhiều tín
  hiệu yếu cùng bắn — mục 4f). T_low hầu như không ảnh hưởng (0,10 hay 0,30 chênh < 1 %).
- Hướng cải thiện sau: context_penalty (kiến trúc 4.4), gộp theo cột, giảm trọng số `vn_name_001`/`health_001`.

## 4h. Ghi finding + scan_result vào SQLite (chưa có LLM)

**Quyết định (người dùng):** `file_hash` = SHA-256 của **nội dung text đã trích** (dùng lại hash chống quét trùng); thêm
NuGet `Microsoft.Data.Sqlite` 10.0.12.

- `Storage/ResultStore.cs`: 3 bảng `file_state`, `scan_result`, `finding` đúng lược đồ kiến trúc mục 3.1 (bảng `llm_*` làm
  khi có LLM). PRAGMA WAL / synchronous=NORMAL / busy_timeout=5000 / foreign_keys=ON. Ghi 3 bảng trong **1 transaction**;
  1 kết nối dùng chung + khoá (nhiều luồng ProcessFile). Quét lại cùng file: `scan_result` upsert, `finding` xoá rồi ghi mới.
- DB mặc định `<thư mục chạy>\data\dlp.db` (`appsettings.json` mục `Storage:DbPath`). Mở DB lỗi → service chạy tiếp, không
  ghi DB. Ghi lỗi → log cảnh báo `[DB]`, không dừng quét. DB đã đóng (service đang dừng) → `SaveScan` bỏ qua.

| Kết quả | status | needs_review | s1 = s_final | labels_json |
|---|---|---|---|---|
| Không khớp | final | 0 | 0 | `{"level":"Public","domains":[]}` |
| Chắc chắn / tín hiệu yếu | final | 0 | độ tin cậy Router | mức + domain |
| **Vùng xám** | **provisional** | **1** | độ tin cậy Router | mức + domain |
| **Quét chưa trọn** | **provisional** | **1** | 0 | NULL (không gắn mức) |

`finding` (1 dòng/rule): `rule_id`, `score` (điểm rule trong S1), `validator_passed`/`hard_rule` (= có validator checksum),
`count`, `location` = `offset:<vị trí chỗ khớp đầu tiên>`. **Không lưu giá trị khớp** ở bất kỳ bảng nào. `source` luôn `regex`.

**Test** (console tạm, rule thật + 1 rule "độc", 8 file rawdata + 3 file txt, quét 2 lượt, đã xoá): 11 file → đúng 11
`file_state` / 11 `scan_result`, không nhân đôi khi quét lại; vùng xám và quét chưa trọn ghi `provisional` + `needs_review`;
CCCD có `hard_rule=1`; tìm thẳng trong byte của file `.db` và `.db-wal`: **không có** số CCCD/email thật. Build 0 warning.

## 4i. Đủ lược đồ SQLite mục 3.1 + kiểm tra PRAGMA + ACL thư mục dữ liệu

- **Lược đồ:** `ResultStore` tạo thêm `llm_job` (+ index `ix_job_pick`), `llm_result`, `llm_cache`, `audit` đúng mục 3.1 (chưa
  có code ghi, chỉ tạo sẵn). Đặt `PRAGMA user_version=1` để đánh dấu phiên bản lược đồ. DB cũ mở lại tự được thêm bảng
  (`CREATE TABLE IF NOT EXISTS`).
- **Kiểm tra PRAGMA:** SQLite không báo lỗi khi bật WAL thất bại (VD ổ mạng) mà âm thầm giữ chế độ cũ → đọc lại
  `journal_mode` (phải là `wal`) và `busy_timeout` (phải 5000); sai thì log cảnh báo `[DB]`, DB vẫn dùng được.
- **ACL (`Storage/DataFolderSecurity.cs`):** thư mục chứa `dlp.db` tắt kế thừa quyền, chỉ còn Full Control cho SYSTEM,
  Administrators và tài khoản đang chạy (để `dotnet run` vẫn ghi được). Đặt ACL lỗi → cảnh báo, service chạy tiếp. Không đặt
  ACL nếu thư mục là gốc ổ đĩa. **Lưu ý:** `Storage:DbPath` phải trỏ vào thư mục riêng của service, vì thư mục cha của DB
  sẽ bị khoá.
- `ResultStore(dbPath, logger)`: thêm tham số logger (không bắt buộc); `Worker` truyền logger vào.

**Test** (console tạm, đã xoá): mở DB 2 lần + ghi 1 file → đủ 7 bảng, 3 index, `journal_mode=wal`, `user_version=1`. Thư
mục thử trên ổ D: có thư mục cha cho `Users`/`Authenticated Users` quyền đọc/sửa → sau khi chạy, `icacls data` và
`icacls data\dlp.db` chỉ còn SYSTEM, Administrators, tài khoản hiện tại. Build 0 warning.

## 4j. Cache file_state: (path, mtime, size) rồi file_hash — bỏ qua file không đổi

Kiến trúc mục 2.3 bước 2. Trước đây cache chỉ nằm trong RAM (`_lastContentHash`) → mỗi lần khởi động lại phải trích text
và quét lại toàn bộ; và luôn phải trích text mới so được hash.

- **Tầng 1 (không mở file):** size + mtime giống hệt lần trước và lần trước đã kết luận → bỏ qua.
- **Tầng 2:** size/mtime đổi → trích text, tính `file_hash`; trùng lần trước (mở file rồi Save lại) → chỉ cập nhật
  size/mtime (`MarkUnchanged`), không quét.
- **Chỉ dùng lại kết quả đã kết luận** (`labels_json` khác NULL). Quét chưa trọn (timeout) → lần sau vẫn quét lại.
- **Chụp size/mtime TRƯỚC khi đọc file** (trước đây `TrySave` lấy sau khi quét → file sửa đúng lúc đang quét sẽ lưu mtime
  mới + kết quả nội dung cũ → nội dung mới không bao giờ được quét).
- **`mtime_utc` đổi đơn vị giây → 100 nano giây** (`DateTime.Ticks`): theo giây thì 2 lần sửa trong cùng 1 giây, size không
  đổi sẽ lọt. DB cũ lệch đơn vị → quét lại 1 lần (thực tế rơi vào tầng 2 vì hash không đổi).
- Cache RAM `_lastContentHash` giữ lại để chống quét trùng khi không mở được DB. Lỗi đọc cache → coi như không có cache.
- Mỗi lượt quét thư mục log 1 dòng: `Quét <thư mục>: N file — quét M, bỏ qua K không đổi, lỗi đọc F`. Từng file bỏ qua chỉ
  log Debug.

**Chưa làm (bước sau):** bộ rule đổi thì cache không biết → file cũ giữ kết quả theo rule cũ (hướng: lưu hash bộ rule, khác
thì bỏ cache). File bị xoá khỏi thư mục vẫn còn dòng trong DB.

**Test** (753 file `watch-folder`, bản Debug): lần 1 (DB cũ mtime theo giây) 93 s, cả 753 qua tầng 2; lần 2 **1,1 s**, cả 753
qua tầng 1. Service đang chạy: tạo file mới → quét; chỉ đổi mtime → bỏ qua; sửa nội dung → quét lại; khởi động lại → 754
file bỏ qua hết. Build 0 warning.

## 4k. Bảng llm_job + máy trạng thái (kiến trúc mục 3.2)

```
pending ──lấy job──► running ──OK──► done
   ▲                    │ ├─ lỗi tạm ─► pending (attempts+1, backoff)
   │                    │ ├─ quá deadline ─► timeout ─► pending (nếu attempts < max) | failed
   │                    │ └─ file đã đổi ─► stale (bỏ, quét lại từ Pass 1)
   └── khởi động lại service: running ──► pending
```

- `Storage/ResultStore.LlmJobs.cs` (partial của `ResultStore`, dùng chung kết nối + khoá). Mỗi bước chuyển = 1 `UPDATE ...
  WHERE status = <trạng thái nguồn>` → bước chuyển sai không sửa dòng nào, hàm trả `false`. Mỗi bước ghi 1 dòng `audit`
  (mã sự kiện, `from→to`, attempts — không có nội dung file).
- **Lấy job:** 1 câu `UPDATE ... RETURNING` chọn job pending ưu tiên cao nhất, cũ nhất, đã hết backoff → 2 worker không lấy trùng.
- **Lỗi tạm / timeout:** `attempts` = số lần đã thử lại; còn lượt (< 3) → pending, chờ 1 / 5 / 20 phút; hết lượt → failed.
  Timeout đi `running → timeout → pending|failed` trong **cùng 1 transaction** (không bị kẹt ở timeout; audit ghi đủ 2 bước).
- **Hoàn thành:** running → done + upsert `llm_result` theo `job_id` cùng transaction. Gọi lại (xử lý trùng) → ghi đè, không
  lỗi. Job đã stale/failed/về pending → trả false, bỏ kết quả. (Merge S1+S2 cập nhật `scan_result` → final: bước sau.)
- **Stale:** `SaveScan` (cùng transaction) đổi job pending/running của file có `file_hash` khác thành stale; worker thấy file
  đổi thì gọi `MarkJobStale`.
- **Tạo job:** file vùng xám → `ScanRecord.LlmPriority = 1` → `SaveScan` tạo job pending trong cùng transaction với
  `scan_result` (không bao giờ có file provisional mà thiếu job). Đã có job khác stale cho cùng (file, hash) → không tạo trùng.
- **Khởi động service (`Worker`):** `RecoverRunningJobs` (running → pending, không tăng attempts), `EnqueueMissingGreyJobs`
  (tạo bù job cho file vùng xám quét trước khi có hàng đợi — cache bỏ qua chúng nên không tự tạo), `PurgeOldJobs(30 ngày)`
  (stale/failed), log `[Job] ... Hiện có: pending=N, ...`.
- Chưa có worker LLM → job nằm ở pending. Hằng số (3 lần, 1/5/20 phút, 30 ngày) để trong code; đưa vào `appsettings.json` khi làm worker.

**Test** (console tạm, DB tạm, đã xoá): 23 kiểm tra PASS — không tạo trùng; pick/null khi đang backoff; lỗi tạm → 60 s,
timeout → 300 s, lỗi → 1200 s, timeout lần 4 → failed; bước chuyển sai → false; done + upsert idempotent; stale khi file đổi
(cả pending lẫn running); khởi động lại; **8 luồng lấy 50 job → 50 job khác nhau**; tạo bù bỏ qua file quét chưa trọn; dọn
job cũ. Bản sao DB thật (754 file): 32 file vùng xám → tạo bù đúng 32 job P1, chạy lần 2 → 0. Build 0 warning.

## 4l. Eligibility filter + phân nhóm P1/P2/P3 + hạn ngạch (kiến trúc mục 4.7-4.8)

**Quyết định (người dùng):** P1 vượt hạn ngạch → hoãn sang giờ sau (không bỏ); tín hiệu P2 theo tiêu đề cột + mật độ chuỗi
số để sau; file đã quét trước đây chỉ được xét P2/P3 khi bị sửa (cache bỏ qua file không đổi).

- `Classification/EscalationPolicy.cs` (mới, logic thuần): `Decide(zone, textLength, path, relativePath, fileHash)`.
  - **Điều kiện cần:** ≥ `MinTextChars` (200) ký tự text (loại luôn PDF scan); không thuộc `ExcludedPaths` (so theo thư mục).
    Chưa làm: `llm_cache` theo `pack_hash`, `file_hash` đã xử lý ở cùng `model_ver`/`prompt_ver` (cần worker LLM).
  - **P1** vùng xám. **P2** không khớp / tín hiệu yếu + tên file hoặc thư mục con có từ khoá đáng nghi. **P3** không khớp /
    tín hiệu yếu, chọn mẫu `SampleRatePct` (2%). Chắc chắn → không bao giờ gửi.
  - Từ khoá: bỏ dấu (đ→d), tách từ (ký tự lạ, chữ hoa, chữ↔số), khớp theo từ hoặc ghép ≤ 4 từ liền nhau: `hopdong` khớp
    `HopDong`, `hop-dong`, `Hợp đồng`; `chatluong` KHÔNG khớp `luong`. Hạn chế: `Chất lượng.docx` vẫn khớp `luong`.
    Chỉ xét đường dẫn tính từ watch-folder (thư mục cha như `D:\Backup` không làm mọi file thành P2).
  - Chọn mẫu P3 theo 8 ký tự hex đầu của `file_hash` mod 10.000 → cùng nội dung luôn cùng kết quả, đo tái lập được.
- **Hạn ngạch** (`ResultStore.LlmJobs.cs`, `EnqueueWithQuota`, cùng transaction với `SaveScan`):
  - **P1**: `P1PerHour` = số job được xếp lịch chạy mỗi giờ (đếm theo `next_try_utc`). Giờ này đủ → `next_try_utc` = đầu giờ kế
    tiếp còn chỗ (job hoãn dàn đều ra các giờ sau). Không bao giờ bỏ.
  - **P2/P3**: đếm job tạo trong 24 giờ gần nhất; đủ → bỏ, audit `job_dropped_quota`.
  - **Hàng đợi đầy** (`pending ≥ MaxPendingJobs`): bỏ P2/P3 mới (audit `job_dropped_queue_full`); P1 vẫn nhận.
  - `SaveScan` trả `JobEnqueueOutcome` (None/Duplicate/Created/Deferred/DroppedQuota/DroppedQueueFull) → log `[Job]`.
  - `EnqueueMissingGreyJobs` cũng đi qua hạn ngạch P1.
- `FileWatcherService`: xét ở cả nhánh "sạch" lẫn "có khớp". **Vùng xám nhưng không đủ điều kiện** (VD < 200 ký tự) → ghi
  `final` + `needs_review=1` (không có job nào chốt kết quả; nếu để provisional thì tạo bù lúc khởi động sẽ vượt bộ lọc).
  File P2/P3 vẫn `final` (theo regex là sạch).
- `appsettings.json` mục `Llm`: `MinTextChars`, `ExcludedPaths`, `SuspiciousKeywords`, `SampleRatePct`,
  `Quota { P1PerHour, P2PerDay, P3PerDay, MaxPendingJobs }`. `Worker` đọc + log 1 dòng cấu hình lúc khởi động.

**Test** (console tạm, đã xoá): 21 kiểm tra `Decide` (đủ vùng, ít text, loại trừ, từ khoá có/không dấu, ghép từ, tách
chữ/số, thư mục con, `chatluong` không khớp, tỉ lệ P3 trên 100.000 hash = 2,02%); 10 kiểm tra hạn ngạch (P1 3/giờ: 8 job →
3 chạy ngay + 5 hoãn, dàn 3/3/2 theo giờ; P2/P3 vượt → bỏ + audit; hàng đợi đầy chặn P2/P3, P1 vẫn nhận; không tạo trùng);
chạy thật 753 file `watch-folder` (DB tạm, 18 s): vùng NoMatch 55 / Low 156 / Grey 32 / Certain 510 → job P1 31, P2 4
(`don-xin-viec-nhan-su`), P3 1; 1 file vùng xám 29 ký tự → final + needs_review; không file provisional nào thiếu job.
Test máy trạng thái 4k chạy lại: PASS. Build 0 warning.

## 4m. llm_cache + llm_result — upsert theo khoá (idempotent), cache hai tầng

**Quyết định (người dùng):** `llm_cache` trùng khoá → ghi đè bằng kết quả mới; dọn cache cũ hơn 90 ngày (`Llm:CacheDays`).

- `llm_result`: upsert theo `job_id` (đã có từ 4k, trong `CompleteJob`).
- `Storage/ResultStore.LlmCache.cs` (mới, partial):
  - **Tầng 1** `TryGetResultForContent(fileHash, modelVer, promptVer)`: kết quả mới nhất của job **done** cùng nội dung,
    cùng phiên bản (điều kiện cần số 4 mục 4.8).
  - **Tầng 2** `TryGetLlmCache(packHash, promptVer, modelVer)`: kết quả của gói đã mask giống hệt.
  - `PurgeLlmCache(olderThan)` + audit `llm_cache_purged`; `CountLlmCache()`.
- `CompleteJob(jobId, result, packHash = null)`: kiểm tra `json_out` là JSON hợp lệ **trước** khi mở transaction (hỏng →
  `ArgumentException`, không ghi gì, job giữ running); có `packHash` → upsert `llm_cache` theo
  `(pack_hash, prompt_ver, model_ver)` trong **cùng transaction** với `running → done` + `llm_result`.
- Kết quả lấy từ cache vẫn đi qua `CompleteJob` (latency 0, `packHash = null`) → mọi job done đều có 1 dòng `llm_result`.
- `Worker` khi khởi động: `PurgeLlmCache(CacheDays)` + log số mục còn lại.
- Chưa có worker LLM → các hàm tra cứu chưa được gọi; luồng dùng: tầng 1 → đóng gói + mask → tầng 2 → gọi LLM →
  `CompleteJob(..., packHash)`.

**Test** (console tạm, đã xoá): 15 kiểm tra PASS — ghi cả 2 bảng; khoá sai phiên bản/gói → null; gọi lại cùng job → vẫn
1 + 1 dòng, ghi đè kết quả + thời điểm; job khác cùng gói → 2 `llm_result`, 1 `llm_cache` (ghi đè); khác model → mục riêng;
JSON hỏng → không thêm dòng nào, job vẫn running; tầng 1 bỏ qua job chưa done; bản sao cùng nội dung hoàn thành từ tầng 1;
dọn cache chỉ xoá mục cũ, không đụng `llm_result`. Test máy trạng thái 4k chạy lại: PASS. Build 0 warning.

## 4n. Dọn dẹp theo lịch + test kill service giữa chừng

**Quyết định (người dùng):** dọn mỗi 24 giờ; dọn `audit` cũ hơn 180 ngày; công cụ test để trong repo `tools/resilience/`.

**Dọn dẹp theo lịch** (`Worker.cs`, `ResultStore.PurgeOldAudit`):
- `RunCleanup` chạy lúc khởi động + mỗi `CleanupIntervalHours` (24) trong vòng lặp 1 phút có sẵn của Worker (không thêm
  timer/luồng). Mốc được cập nhật TRƯỚC khi dọn → dọn lỗi cũng không thử lại mỗi phút.
- Dọn: job stale/failed > `JobRetentionDays` (30, trước đây viết cứng), `llm_cache` > `CacheDays` (90), `audit` >
  `AuditRetentionDays` (180; audit giữ lâu hơn job để còn truy vết job đã xoá; ghi 1 dòng `audit_purged`). KHÔNG dọn job
  done / `llm_result` (tầng cache 1 cần). Giá trị cấu hình < 1 → dùng 1. Mỗi lần dọn log 1 dòng `[Dọn dẹp] ... Lần sau: ...`.

**Test kill giữa chừng** (`tools/resilience/DlpResilience`, chạy `DlpService.exe` thật + kill thật = TerminateProcess):
- **A. Kill service lúc quét** 753 file, 3 lần (ở 25/50/75%), rồi cho quét nốt; so với 1 lần chạy sạch.
- **B. Kill worker lúc job đang chạy** (worker giả, process riêng, 2 worker song song, "gọi LLM" 400 ms, 30% job gọi
  `CompleteJob` 2 lần), 3 lần, mỗi lần khởi động lại service thật (→ `RecoverRunningJobs`), rồi chạy nốt.

**Kết quả** (`tools/resilience/results/resilience-20260928-225433.md`) — **tất cả PASS**:

| Kiểm tra | Kết quả |
|---|---|
| A: `integrity_check` sau mỗi lần kill + cuối | ok |
| A: đủ 753 `file_state` / `scan_result`, không file provisional thiếu job, không job trùng | đúng |
| A: từng file (trạng thái, nhãn, S1, finding, job) so với chạy sạch | **0 khác biệt** |
| A: job theo nhóm | sạch P1=31, P2=4, P3=1 = bị kill |
| B: mỗi lần kill | 2 job đang running → kẹt running → service khởi động lại đưa về pending |
| B: không mất job | 36/36 done |
| B: mỗi job đúng 1 `llm_result`, đúng nội dung của chính job đó | 36 dòng, đúng |
| B: `llm_cache` | 36 mục (1/nội dung) |
| B: số lần lấy job = 1 + số lần được khôi phục | 6 lần khôi phục, 0 job lệch |
| B: hoàn thành trùng (at-least-once) | 9 lần, vẫn 1 `llm_result`/job |

Chưa kiểm được trực tiếp: lần dọn định kỳ thứ 2 (chu kỳ tối thiểu 1 giờ) — đã kiểm lần dọn lúc khởi động (log
`[Dọn dẹp]` mỗi lần service chạy trong test) và `PurgeOldAudit` riêng (xoá đúng 3 dòng cũ, lần 2 xoá 0). Build 0 warning.

---

## 5. Bảng data flow — file đã thay đổi trong buổi 6

| File | Thay đổi | Lý do |
|---|---|---|
| `tools/perf/Measure-DlpPerformance.ps1` (mới) | Script đo 2 kịch bản, lấy mẫu CPU/RAM, xuất báo cáo + CSV | Có số liệu hiệu năng thực tế, đo lại được sau mỗi thay đổi |
| `tools/perf/results/*.csv` (mới) | Kết quả từng lượt đo | Làm mốc so sánh |
| `docs/muc-luc-code.md` | Thêm mục `tools/perf/` và `tools/eval/DlpBaseline/` | Tra cứu file mới |
| `tools/eval/DlpBaseline/*` (mới), `DlpService.slnx` | Console app đo baseline precision/recall + thời gian từng bước | Task "Đo baseline" |
| `Scanning/Validation/*.cs` (mới, 7 file) | 6 validator + hàm phụ tách giá trị | Task "Cài validator" (mục 4c) |
| `Scanning/Validators.cs`, `SensitivePattern.cs`, `RuleLoader.cs`, `RegexScanner.cs` | `ValidatorEntry` (hàm + `IsChecksum`); chỉ checksum là bằng chứng cho level_gate | Không để bộ lọc email/SĐT vô hiệu hoá gate |
| `rules/pii/{cccd,email,phone-vn}.json`, `rules/financial/{tax-code-business,tax-code-personal-bare,tax-code-personal-labeled,iban}.json`, `rules/org/credentials.json` | Thêm `validators`, ghi chú `notes[]`; sửa regex IBAN p2 | Gắn validator vào đúng pattern |
| `tools/eval/DlpBaseline/Program.cs` | Tiến độ `hh:mm:ss`, giờ bắt đầu đúng | Lỗi lộ ra khi máy ngủ giữa lượt đo |
| `Scanning/ScanResult.cs` (mới), `RegexScanner.cs`, `Watching/FileWatcherService.cs`, `Classification/Modules/CompoundRuleModule.cs` | `Scan()` trả `ScanResult`; timeout → cảnh báo + "CHƯA KẾT LUẬN ĐƯỢC", không gắn mức; compound không bị bỏ cả module khi timeout | Bịt lỗ né phát hiện bằng timeout (mục 4e) |
| `tools/eval/DlpBaseline/{Program,Metrics,Report}.cs`, `tools/perf/Measure-DlpPerformance.ps1` | Theo `ScanResult`; cột/dòng đếm quét chưa trọn; nhận dạng log mới | Công cụ đo không bị vỡ khi log đổi |
| `Scanning/CandidateScorer.cs` (mới), `ScanMatch.cs`, `RegexScanner.cs`, `PlaceholderExclusionDefinition.cs`, `PlaceholderRule.cs`, `RuleLoader.cs` | Điểm candidate `Score`; đọc `doc_context_discount_*` | S1 giai đoạn 1 (mục 4f) |
| `Classification/FileScorer.cs` (mới), `Watching/FileWatcherService.cs` | S1 cấp file; log Debug | S1 giai đoạn 1 |
| `tools/eval/DlpBaseline/S1Tuning.cs` (mới), `Program.cs`, `Metrics.cs`, `Report.cs` | Chia train/test, dò ngưỡng, báo cáo mục 3, cột S1 trong CSV | S1 giai đoạn 2 |
| `docs/kien-truc-trien-khai.md` (mới) | Tài liệu kiến trúc đích (Regex + LLM) | Người dùng cung cấp |
| `Classification/ScoreRouter.cs` (mới), `Watching/FileWatcherService.cs`, `Worker.cs`, `appsettings.json` | Router ba vùng; ngưỡng `Score: {THigh 0.80, TLow 0.10}`; log độ tin cậy + vùng | Mục 4g |
| `tools/eval/DlpBaseline/RouterEval.cs` (mới), `Program.cs`, `Report.cs` | Báo cáo mục 4 (quét ngưỡng Router), cột `route_confidence`, `route_zone` | Chọn ngưỡng baseline |
| `Storage/ResultStore.cs` (mới), `DlpService.csproj` (+ Microsoft.Data.Sqlite), `Watching/FileWatcherService.cs`, `Worker.cs`, `appsettings.json` | Ghi `file_state`/`scan_result`/`finding` vào SQLite | Mục 4h |
| `Storage/ResultStore.cs`, `Storage/DataFolderSecurity.cs` (mới), `Worker.cs` | Đủ 7 bảng mục 3.1 + `user_version`; đọc lại `journal_mode`/`busy_timeout`; ACL thư mục dữ liệu | Mục 4i |
| `Storage/ResultStore.cs` (`FileStateInfo`, `TryGetFileState`, `MarkUnchanged`, mtime → ticks), `Watching/FileWatcherService.cs` (`ProcessFile` 2 tầng cache, `TrySave`, `ScanExistingFiles`) | Cache (path, mtime, size) rồi file_hash; bỏ qua file không đổi | Mục 4j |
| `Storage/ResultStore.LlmJobs.cs` (mới), `Storage/ResultStore.cs` (partial, `ScanRecord.LlmPriority`, `SaveScan` gọi `SyncLlmJob`), `Watching/FileWatcherService.cs` (vùng xám → P1), `Worker.cs` (khôi phục/tạo bù/dọn job khi khởi động) | Hàng đợi `llm_job` + máy trạng thái 6 trạng thái | Mục 4k |
| `Classification/EscalationPolicy.cs` (mới), `Storage/ResultStore.LlmJobs.cs` (`JobQuotaOptions`, `JobEnqueueOutcome`, `EnqueueWithQuota`), `Storage/ResultStore.cs` (ctor + hạn ngạch, `SaveScan` trả outcome), `Watching/FileWatcherService.cs`, `Worker.cs`, `appsettings.json` (mục `Llm`) | Eligibility + P1/P2/P3 + hạn ngạch + hàng đợi đầy | Mục 4l |
| `Storage/ResultStore.LlmCache.cs` (mới), `Storage/ResultStore.LlmJobs.cs` (`CompleteJob` + `packHash`, kiểm tra JSON), `Worker.cs`, `appsettings.json` (`Llm:CacheDays`) | llm_cache + tra cứu llm_result (cache hai tầng), upsert idempotent | Mục 4m |
| `Worker.cs` (`RunCleanup` định kỳ), `Storage/ResultStore.LlmJobs.cs` (`PurgeOldAudit`), `appsettings.json` (`JobRetentionDays`, `AuditRetentionDays`, `CleanupIntervalHours`), `tools/resilience/DlpResilience/*` (mới), `DlpService.slnx` | Dọn dẹp theo lịch; test kill service/worker giữa chừng | Mục 4n |

---

## 6. Việc có thể làm tiếp

| Việc | Ghi chú |
|---|---|
| Tìm ngưỡng tràn buffer | Tăng `-BurstCount` lên 2000, 5000 (script tự lấy xoay vòng 750 file mẫu). Xem dòng "Buffer tràn" trong báo cáo |
| Đo lại sau khi publish bản mới | Buổi 5 có nhiều thay đổi Bậc 3 và đọc nhiều định dạng; so với CSV mốc ở mục 4 |
| Đo sau khi áp `min_confidence` (buổi 5, mục 5g) | Kiểm tra thêm bước lọc có làm chậm quét không |
| Thêm file mẫu `.xlsx`, `.pptx`, `.txt` | Bộ mẫu hiện chỉ có PDF và DOCX |
