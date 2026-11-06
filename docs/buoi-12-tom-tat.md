# Buổi 12 — Đo lại baseline, áp công thức điểm chỗ khớp của Go + MinConfidence, FileConfidence 2 nhánh (shadow)

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái |
|---|---|
| Đo lại toàn bộ rawdata (25.486 file) bằng `DlpBaseline` hiện có | ✅ Accuracy 77,4 %, F1 "nhạy cảm" 91,6 % — giống 28/09; tốc độ chậm ×2 (nghi máy bận, chưa kiểm) |
| Công thức điểm 1 chỗ khớp theo engine Go (boost theo khoảng cách, checksum +0,12, `cvv_expiry_boost`, bỏ phạt negation) | ✅ Xong, 22 unit test |
| Ngưỡng `Scan:MinConfidence = 0,60` (loại chỗ khớp điểm thấp, như Go) | ✅ Xong — **Accuracy 77,4 → 81,6 %**, F1 91,6 → 93,7 % |
| FileConfidence 2 nhánh A/B (công thức Pha 21 của Go, hệ số copy nguyên) — chạy SHADOW | ✅ Xong: log `[Conf]` + cột DB `file_conf`/`conf_branch`, Router S1 giữ nguyên; 15 unit test |
| Hiệu chuẩn FileConfidence trên engine C# | ✅ Sửa counter `proximity_reject` (mục 2.1) + "dãy số lạ" + đoán domain (mục 2.2): giữ ở T=0,90 = 83,1 % (Go 81,2 %); AUC A 0,79 / B 0,88, ECE A 0,045 / B 0,052 |
| Báo cáo P/R/F1 theo 16 lớp (domain × mức) + thời gian theo lớp | ⏳ Chưa làm (đề xuất ở đầu buổi, chưa chốt phạm vi) |

## 1. Công thức điểm chỗ khớp (giống Go — `internal/engine/regex.go`)

```
score = (confidence ?? 0,75) × weight
score = clamp(score + boost)                 boost theo khoảng cách, MỖI LẦN từ khoá xuất hiện đều cộng, trần 0,30:
                                               0–20 ký tự: primary +0,15 / secondary +0,07
                                               21–50     : +0,10 / +0,05
                                               51–200    : +0,05 / +0,02
qua validator checksum → min(score + 0,12; 0,99)
cvv_expiry_boost (rule thẻ) + có CVV và ngày hết hạn trong ±300 ký tự → clamp(score + boost)
tài liệu hướng dẫn/mẫu → clamp(score × doc_context_discount_factor)
score < Scan:MinConfidence (0,60) → BỎ chỗ khớp (trước negation/volume/level_gate/compound/S1)
```

- File: `Scanning/CandidateScorer.cs` (viết lại), `Scanning/RegexScanner.cs` (`AddSurvivor` tính điểm + lọc ngưỡng, `KeywordBoost`/`SumKeywordBoost`, `CvvExpiryBoostFor`), `RuleLoader` + `SensitivePattern` + `FalsePositiveReductionDefinition` (đọc `cvv_expiry_boost` — trước đây JSON có khai nhưng bị bỏ qua), `Worker.cs` + `appsettings.json` (`Scan:MinConfidence`).
- `ScanMatch.Confidence` (dùng cho `min_confidence_bypass`) giờ = `Score` như Go.
- Khác Go: khoảng cách tính theo **ký tự**, Go tính theo **byte UTF-8**.

| (25.486 file) | Trước | Chỉ đổi công thức | Công thức + ngưỡng 0,60 |
|---|---|---|---|
| Accuracy 4 mức | 77,4 % | 77,4 % | **81,6 %** |
| Macro-F1 | 77,3 % | 77,3 % | **81,7 %** |
| "Nhạy cảm" P / R / F1 | 87,2 / 96,5 / 91,6 | như trước | **91,7 / 95,8 / 93,7** |
| Public bị đẩy lên Internal | 1.902 | 1.901 | **952** |
| FN (bỏ sót nhạy cảm) | 452 | 453 | 540 |

Báo cáo: `tools/eval/results/baseline-20261021-093404.md` (ngưỡng 0) và `baseline-20261021-094211.md` (0,60).
`DlpBaseline` có thêm `--min-confidence X` để đo so sánh.

## 2. FileConfidence 2 nhánh (shadow)

Nguồn: `D:\dlp-classifier\tailieu\pha21-danh-gia-lop1-regex.md` mục 1, hệ số `pha2out/pha4a_model.json` (A, target not_under),
`pha4_model.json` (B), `rule_precision_train.json`, Platt `pha21_layer1_eval.json`, ngưỡng P19 `pha19_level_threshold.json`.
File hệ số `DlpService/confidence/file-confidence-v1.json` được **sinh bằng script** đọc thẳng các file trên (không chép tay).

```
A  nếu (mức > Public và có chỗ khớp đúng bằng mức file) hoặc có rule "luôn bí mật"
Short nếu < 200 byte UTF-8 → 0,9576 ;  B còn lại
A: σ(γ0[domain] − 9,733·risk − 1,666·φ(dãy số lạ; 2,969)),  risk = 1 − min P̂(rule quyết định), không có → 0,5
B: σ(1,135·z − 0,137),  z = β0[domain] − 0,791·φ(kw_no_match;5) − 5,124·φ(proximity_reject;1,439) − 0,972·φ(gate_downgrade;1)
                              − 1,715·φ(mật độ số lạ;0,602) − 2,358·φ(volume_near_gate;0,0307) − 1,430·[tài liệu mẫu]
Giữ Lớp 1 (P19): conf ≥ Public 0,889 · Internal 0,975 · Confidential 0,961 · Restricted 0,001
Domain: đoán từ rule đã khớp (hr → hr, insurance_context → insurance, *health* → healthcare, financial → finance); không đoán được → hr (intercept thấp nhất)
```

- File: `Scanning/ScanTelemetry.cs` (mới, 7 counter), `RegexScanner.cs` (đếm tại các nhánh loại chỗ khớp + `BuildTelemetry`), `Classification/FileConfidence.cs` (mới), `FileWatcherService.cs` (`ComputeFileConfidence`, log `[Conf]`), `ResultStore.cs` (cột `file_conf`, `conf_branch` — tự migrate), `Worker.cs` (`FileConfidence:ModelPath`), `DlpBaseline/ConfidenceEval.cs` (mục 5 báo cáo).
- **Không đổi** chỗ khớp, mức file hay Router — kiểm chứng: accuracy vẫn 81,6 % sau khi thêm.
- Xấp xỉ so với Go: `proximity_reject` của C# đếm mọi chỗ khớp `context_required` bị loại (khi rule có từ khoá đâu đó trong file); Go chỉ đếm nhánh `exclude_if_no_keywords`.

### Kết quả (`tools/eval/results/baseline-20261021-101057.md`, mục 5)

| Nhánh | Số file | Tỉ lệ "đúng" thật | conf TB | AUC | ECE | (Go holdout) |
|---|---|---|---|---|---|---|
| A | 18.879 (74 %) | 93,4 % | 0,906 | 0,792 | **0,034** | AUC 0,857 / ECE 0,011 |
| B | 6.607 (26 %) | 78,6 % | **0,441** | 0,719 | **0,375** | AUC 0,776 / ECE 0,049 |

- Nhánh A dùng được. **Nhánh B chấm quá thấp** (TB 0,44 trong khi 79 % file thật là Public) → counter C# kêu nhiều hơn Go; nghi nhất là `proximity_reject` (hệ số −5,12, chỉ 1 lần đếm đã trừ ~2,6 điểm logit).
- Đoán domain đúng 56,4 %, nhưng AUC/ECE gần như không đổi khi dùng domain thật → domain không phải vấn đề chính.

| Luật giữ Lớp 1 | Giữ | Gửi LLM | Đúng mức (file giữ) | Under (file giữ) | Leak recall | Bắt lỗi |
|---|---|---|---|---|---|---|
| Router S1 hiện tại (0,80/0,10) | 92,3 % | 7,7 % | 82,7 % | 8,1 % | 90,1 % | 13,2 % |
| P19 (domain đoán) | 48,7 % | 51,3 % | 89,0 % | 1,6 % | 99,0 % | 71,0 % |

P19 an toàn hơn hẳn (leak recall 99 %) nhưng gửi LLM **51 %** file — quá nhiều so với tốc độ LLM hiện tại (~20–40 giây/job).

### 2.1. Chẩn đoán + sửa nhánh B

- `DlpBaseline` ghi thêm 7 counter + độ dài byte vào CSV (`tel_*`). Script `diag_branch_b.py` (scratchpad) ghép cặp 25.476 file với
  `D:\dlp-classifier\pha2out\features_split.jsonl` của Go (theo id + định dạng). Nhánh khớp Go 95 %, mức dự đoán khớp 92,6 %.
- **Thủ phạm: `proximity_reject_count`** — C# > 0 ở 53,1 % file nhánh B, Go 4,6 % (TB 2,7 so với 0,07). Các counter khác khớp sát.
  Nguyên nhân: Go chỉ đếm ở bước 4 `regex.go` cho rule khai `false_positive_reduction.exclude_if_no_keywords` (C# trước đây không đọc
  trường này), xét MỌI pattern con, không đếm khi pattern `context_required` mà cả file không có từ khoá, không đếm chỗ trượt
  `require_keyword_within`. C# cũ đếm mọi chỗ khớp `context_required` bị loại.
- **Sửa** (chỉ đếm, không đổi chỗ khớp/mức — accuracy vẫn 81,6 %): `RegexScanner.FailsGoKeywordCheck`, `SensitivePattern.ExcludeIfNoKeywords`
  + `RuleProximity`, RuleLoader đọc 2 trường; 4 unit test viết lại (71/71 pass). Sau sửa: > 0 ở 8,3 % file (Go 4,6 %).
- Còn lệch nhỏ, chưa sửa: `identifier_volume_near_gate` C# kêu ÍT hơn Go (7,8 % so với 18,4 %) — do số chỗ khớp còn lại của 5 rule
  volume khác nhau giữa 2 engine (không phải định nghĩa); hướng lệch làm conf C# hơi CAO hơn.

Kết quả sau sửa (`tools/eval/results/baseline-20261021-104144.md`):

| Nhánh | conf TB | Tỉ lệ đúng thật | AUC | ECE | Go holdout |
|---|---|---|---|---|---|
| A | 0,906 | 93,4 % | 0,792 | 0,034 | 0,857 / 0,011 |
| B | 0,441 → **0,765** | 78,6 % | 0,719 → **0,862** | 0,375 → **0,048** | 0,776 / 0,049 |

| Luật giữ Lớp 1 | Gửi LLM | Đúng mức (file giữ) | Under (file giữ) | Leak recall | Bắt lỗi |
|---|---|---|---|---|---|
| Router S1 hiện tại (0,80/0,10) | 7,7 % | 82,7 % | 8,1 % | 90,1 % | 13,2 % |
| conf ≥ 0,50 (cùng ngân sách LLM) | 8,1 % | 83,3 % | 6,5 % | **92,0 %** | **16,6 %** |
| conf ≥ 0,85 | 17,6 % | 85,7 % | 4,6 % | 95,0 % | 35,9 % |
| P19 (domain đoán) | 44,2 % | 90,1 % | 1,8 % | 98,7 % | 69,8 % |

→ Cùng mức gửi LLM ~8 %, conf tốt hơn S1 ở mọi chỉ số. P19 vẫn gửi quá nhiều (44 %) với tốc độ LLM hiện tại.

### 2.2. Vì sao ở T=0,90 Go giữ ~81 % còn C# ~74 % — đã sửa

Tính lại conf Go từ `features_split.jsonl` bằng đúng `file-confidence-v1.json` → 81,2 % (khớp Pha 21: 81,1 %) ⇒ công thức port đúng,
lệch nằm ở ĐẦU VÀO. Ghép cặp từng file (script `diag_keep90.py`, `whatif_numeric.py` trong scratchpad):

| Nguyên nhân | Mất | Sửa |
|---|---|---|
| "Dãy số lạ" kêu sai: rule C# có validator Go không có (`tax_code_business_001` + `mst`: Go khớp 4.343 file, C# 548; `phone_001` + `vn_phone`) → số MST/SĐT bị loại thành "số lạ" → nhánh A bị trừ điểm (931/962 file lệch có số lạ ở C#, 84 ở Go) | −3,1 điểm | **A**: `CountUnstructuredNumericHits` bỏ cả vị trí regex đã khớp nhưng bị validator loại (`validatorRejectedSpans`) — chỉ đổi counter |
| Đoán domain: file nhánh B thường không có chỗ khớp → rơi về hr (β0 thấp nhất) | −2,8 điểm | **B**: `ScanTelemetry.PrimaryKeywordRules`; `GuessDomain` không có chỗ khớp thì bỏ phiếu theo rule có từ khoá primary |
| Rule C# lọc chặt hơn (email cá nhân, ngưỡng 0,60, gate) → 698 file A của Go thành B ở C# | −1,1 điểm | Giữ nguyên (khác biệt có chủ đích của engine C#) |

Kết quả (`baseline-20261021-110840.md`), accuracy vẫn 81,6 %, 74/74 test:

| T = 0,90 | Trước | Sau A+B |
|---|---|---|
| Go (domain thật) | 81,2 % | 81,2 % |
| C# (domain thật) | 77,0 % | **81,2 %** |
| C# (domain đoán) | 74,2 % | **83,1 %** |

| Nhánh | AUC đoán / thật | ECE đoán / thật | conf TB vs tỉ lệ đúng thật |
|---|---|---|---|
| A | 0,790 / 0,787 | 0,045 / 0,039 (trước 0,034 / 0,037) | 0,919 vs 93,4 % |
| B | 0,877 / 0,853 | 0,052 / 0,040 (trước 0,048 / 0,046) | 0,835 vs 78,6 % — **hơi tự tin quá ~5 điểm** |

- Đoán đúng domain 56,4 % → **65,0 %**. Domain đoán cho conf nhánh B CAO hơn domain thật (đoán sang domain intercept cao hơn hr) → giữ nhiều hơn Go 2 điểm, đổi lại ECE nhánh B tăng nhẹ.

Bảng quét ngưỡng 0,05 (domain đoán, toàn bộ 25.486 file):

| T | Giữ Lớp 1 | Gửi LLM (file) | Đúng (giữ) | Under (giữ) | Leak recall | Leak Restricted | Bắt lỗi |
|---|---|---|---|---|---|---|---|
| 0,30 | 94,2 % | 1.466 | 82,3 % | 7,2 % | 91,0 % | 85,7 % | 9,5 % |
| 0,50 | 93,5 % | 1.650 | 82,7 % | 7,0 % | 91,2 % | 86,2 % | 12,1 % |
| 0,70 | 92,0 % | 2.027 | 83,4 % | 6,6 % | 91,9 % | 86,3 % | 16,9 % |
| 0,80 | 90,7 % | 2.381 | 83,6 % | 6,3 % | 92,4 % | 86,8 % | 19,3 % |
| 0,85 | 87,1 % | 3.285 | 84,6 % | 5,2 % | 94,0 % | 88,5 % | 27,2 % |
| 0,90 | 83,1 % | 4.306 | 85,3 % | 4,6 % | 94,8 % | 89,3 % | 33,4 % |
| 0,95 | 61,5 % | 9.814 | 85,9 % | 2,6 % | 97,9 % | 96,2 % | 53,0 % |

(Router S1 đang chạy 0,80/0,10: gửi LLM 7,7 %, leak recall 90,1 %, bắt lỗi 13,2 %.)

**Sự cố trong buổi**: `RegexScanner.cs` bị ghi đè về 1 bản trung gian cũ (nghi buffer IDE chưa lưu) — đã khôi phục đầy đủ, kiểm bằng build + 74 test + đo lại.

## 2.3. Đo service thật (bản publish dưới SCM) + sửa FileLogger

- **Log `[Perf]`** trong `FileWatcherService.ProcessFile`: thời gian từng bước (cache, đọc, băm, quét, phân loại, ghi DB, tổng) cho mỗi
  file đã quét.
- **`tools/perf/Measure-DlpServiceE2E.ps1`** (mới, chạy quyền admin): làm nóng 10 file → mức nền → 64 file tuần tự → burst
  25.486 file có nhãn (danh sách = CSV DlpBaseline) → 2 phút sau tải → khởi động lại service. Lấy mẫu CPU/RAM/handle/thread mỗi
  giây; đọc mức/[Perf]/[Conf] từ log; luôn ghi kết quả trong `finally`. **`tools/perf/Analyze-DlpServiceE2E.py`** → báo cáo Markdown.
- **Lượt `20261021-144046`** (script lỗi `Measure-Object` trên hashtable ở cuối burst → mất mẫu tài nguyên; đã sửa; số liệu còn lại
  dựng lại từ log — `tools/perf/results/e2e-20261021-144046-report.md`):
  - Độ chính xác service thật = DlpBaseline **100 %** trùng mức (Accuracy 81,6 %, F1 nhạy cảm 93,7 %).
  - 25.486 file trong **340 giây (75 file/giây)**, ~23 file song song; 0 file mất / trùng / timeout / tràn buffer, không crash.
  - **Nút thắt: FileLogger** — mỗi dòng log lấy khoá chung + mở/ghi/đóng file → ~1.070 dòng/giây (364.213 dòng / 340 giây); bước
    "phân loại" TB 240 ms/file thực chất là chờ khoá log.
- **Sửa FileLogger** (`Logging/FileLogger.cs`, `FileLoggerProvider.cs`): `FileLogger.Log` chỉ đưa dòng vào hàng đợi (`BlockingCollection`,
  tối đa 50.000 dòng, đầy thì chờ); 1 luồng nền giữ file mở sẵn, ghi theo lô, flush sau mỗi lô; xoay vòng 5 MB/ngày, giữ 7 ngày,
  FileShare cho Notepad, thử lại 3 lần khi lỗi — giữ nguyên hành vi cũ. Xả hàng đợi khi `Dispose`/`ProcessExit` (host không tự
  Dispose provider thêm bằng `AddProvider`). 5 unit test mới (79/79 pass).
- **Lượt đo sau sửa `20261021-150346`** (đầy đủ, xem `docs/danh-gia-service-e2e.md`): bước phân loại 240 → **4 ms**; service theo kịp
  tốc độ thả file (xong 4 giây sau khi copy xong, 25.486 file / 419 giây); 0 mất/trùng/timeout/tràn buffer/crash; RAM max 138 MB, sau
  tải 76 MB, độ dốc +1,7 MB/phút (không rò); khởi động lại 0,8 giây, 0 file quét lại. Nút thắt kế tiếp: ghi DB (p95 204 ms) + trích text.

## 2.4. Giảm thời gian ghi DB + chuẩn bị đo thông lượng tối đa

Đo bằng công cụ mới **`tools/perf/DlpDbBench`** (dùng đúng `ResultStore`, DB tạm có sẵn 30.000 file lịch sử, 20 luồng cùng tra cache +
ghi 10.000 file mới):

| Phiên bản | Thông lượng ghi | Ghi DB p50 / p95 / p99 / max | Tra cache max |
|---|---|---|---|
| Ban đầu | 2.430 file/giây | 0,28 / 0,89 / 251 / 1.421 ms | 742 ms |
| + Index `llm_job` | 4.474 file/giây | 0,12 / 0,72 / 123 / 841 ms | 470 ms |
| + Checkpoint nền + kết nối đọc riêng | ~3.800 file/giây | 4,6 / 11–17 / 20–31 / 54–79 ms | 37–46 ms |

Nguyên nhân & sửa (`Storage/ResultStore.cs`):
1. **Thiếu index**: `SyncLlmJob` tra `llm_job` theo `file_id` 2 lần mỗi lần ghi + đếm hạn ngạch theo `priority` → quét toàn bảng, chậm dần
   theo số job tích luỹ. Thêm `ix_job_file(file_id, file_hash)`, `ix_job_prio_created`, `ix_job_prio_next` (tự thêm vào DB cũ).
2. **Checkpoint WAL trong lúc giữ khoá ghi** (nguyên nhân các lần kẹt 0,1–1,4 giây): tắt `wal_autocheckpoint`, chạy `wal_checkpoint(PASSIVE)`
   mỗi giây trên kết nối riêng (không giữ `_writeLock`); `journal_size_limit` 64 MB.
3. **Tra cache chờ sau ghi**: `TryGetFileState` dùng kết nối CHỈ ĐỌC riêng + khoá riêng (WAL cho đọc song song ghi).

Đánh đổi: thông lượng ghi tối đa giảm ~15 % (vẫn gấp ~40–60 lần nhu cầu của service) để đổi lấy hết các lần kẹt dài. 5 unit test mới
(`ResultStoreConcurrencyTests`), 84/84 pass.

**`Measure-DlpServiceE2E.ps1 -FastDrop`**: copy trước file ra thư mục tạm cùng ổ (ngoài watch-folder, không tính giờ) rồi `File.Move` từng
file vào watch-folder → thả 25.000 file trong vài giây, đo được thông lượng tối đa của service (lượt trước bị giới hạn bởi copy 416 giây).
Lưu ý: thả nhanh dễ làm tràn buffer watcher → service quét bù cả thư mục TUẦN TỰ song song với luồng sự kiện — lượt đo sẽ cho thấy
có xử lý trùng/chậm do cơ chế này không.

**Lượt `20261021-161412` (`-FastDrop`, sau sửa DB)** — chi tiết `docs/danh-gia-service-e2e.md` mục 5b: thông lượng tối đa **104 file/giây**
(25.486 file / 244 giây, service là giới hạn); tra cache dưới tải 14,2 → 3,1 ms TB; khởi động lại 8,1 → 2,3 giây; ghi DB trong service
vẫn ~60 ms dù DB đo riêng ~5 ms → nghi tranh tài nguyên ngoài service (CPU máy 50 % trong khi service 11 %; nghi Windows Defender quét
thời gian thực). 0 mất/trùng/timeout/tràn buffer/crash; RAM max 190 MB, không rò.

## 3. Việc còn mở

1. ~~Chẩn đoán nhánh B~~ — xong (mục 2.1).
2. Chưa chuyển Router sang FileConfidence (vẫn shadow) — cần chốt luật giữ Lớp 1 theo ngân sách LLM (VD 1 ngưỡng ~0,50–0,85, hoặc P19 dò lại ngưỡng cho C#).
3. Cache file_state chỉ so size/mtime/hash, **không xét rules_ver** → file đã quét không tự quét lại khi đổi công thức/rule (có từ trước, chưa sửa).
4. Báo cáo theo 16 lớp + thời gian theo lớp (đề xuất đầu buổi, chưa chốt).
5. Tốc độ đo hôm nay chậm ×2 so với 28/09 (cả bước nạp rule) — cần đo lại lúc máy rảnh để loại trừ hồi quy.
