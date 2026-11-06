# Tổng hợp thay đổi — Buổi 6 (28/09/2026)

Bản tóm tắt ngắn. Chi tiết, lý do thiết kế và số liệu đầy đủ: `docs/buoi-06-tom-tat.md` (mục được ghi trong ngoặc).

## 1. Tổng quan

| # | Hạng mục | Đổi hành vi service? | Trạng thái |
|---|---|---|---|
| 1 | Script đo hiệu năng thực tế (4) | Không | ✅ Xong |
| 2 | Công cụ đo baseline precision/recall + tốc độ (4b) | Không | ✅ Xong |
| 3 | 6 validator: CCCD, MST, IBAN, entropy secret, email, SĐT (4c) | **Có** | ✅ Xong |
| 4 | Log timeout, "CHƯA KẾT LUẬN ĐƯỢC" khi quét chưa trọn (4e) | **Có** | ✅ Xong |
| 5 | Điểm candidate + S1 cấp file (4f) | Không (chỉ tính) | ✅ Baseline v1 |
| 6 | Router ba vùng T_high/T_low (4g) | **Có** (thêm log) | ✅ Baseline v1 |
| 7 | Ghi `file_state` / `scan_result` / `finding` vào SQLite (4h) | **Có** (thêm DB) | ✅ Xong |
| 8 | Tài liệu kiến trúc đích Regex + LLM | — | ✅ `docs/kien-truc-trien-khai.md` |

Mọi thay đổi code: build 0 warning, 0 error. **Service đang chạy chưa có các thay đổi 3, 4, 6, 7** — cần publish lại (mục 6).

## 2. Kết quả đo chính

### Độ chính xác (rawdata: 12.743 mẫu × docx + pdf = 25.486 file)

| Chỉ số | Trước validator | Sau validator |
|---|---|---|
| Precision "nhạy cảm" (confidential + top_secret) | 79,6 % | **87,2 %** |
| Recall "nhạy cảm" | 96,5 % | 96,5 % |
| Số file báo nhầm | 3.160 | **1.806** (−43 %) |
| Accuracy 4 mức | 73,9 % | **77,4 %** |
| File public bị xếp Confidential | 1.735 | **601** |
| Precision / Recall phát hiện PII | 63,5 % / 97,3 % | 66,2 % / 97,2 % |

### Tốc độ và tài nguyên (i7-13700H, 20 luồng)

| Phép đo | Kết quả |
|---|---|
| 1 file (từ lúc lưu tới lúc có kết quả, gồm 300 ms debounce) | ~355–370 ms |
| 750 file cùng lúc (61,8 MB) | 6,2 giây, ~120 file/giây, CPU tối đa ~13 %, RAM tối đa ~166 MB, buffer không tràn |
| Xử lý 1 file (không tính debounce) | 9,4 ms: **trích text 92 %**, regex 8 % (0,7 ms), phân loại ~0 |

### Router ba vùng (tập test 30 %)

| Ngưỡng | Gửi LLM | Bắt được file regex xếp sai |
|---|---|---|
| **Baseline chọn: T_high 0,80 / T_low 0,10** | **10,2 %** | **22,1 %** |
| Mặc định kiến trúc 0,90 / 0,30 | 15,9 % (vượt mục tiêu 2–10 %) | ~32 % |

## 3. Thay đổi trong service (`DlpService/`)

| Thư mục / file | Thay đổi |
|---|---|
| `Scanning/Validation/` (mới, 7 file) | `vn_cccd_prefix`, `mst`, `iban` (checksum) và `secret_entropy`, `email_personal`, `vn_phone` (bộ lọc) |
| `Scanning/Validators.cs` | Bảng tra `ValidatorEntry` (hàm + cờ `IsChecksum`). Chỉ validator checksum được tính là bằng chứng cho level_gate |
| `Scanning/ScanResult.cs` (mới) | `Scan()` trả chỗ khớp + danh sách rule bị timeout |
| `Scanning/CandidateScorer.cs` (mới) | Điểm từng chỗ khớp (`ScanMatch.Score`) |
| `Scanning/RegexScanner.cs` | Ghi rule timeout (trước nuốt im lặng); tính `Score`; tách `KeywordBoost` |
| `Scanning/RuleLoader.cs`, `SensitivePattern.cs`, `ScanMatch.cs`, `Placeholder*.cs` | Theo 2 mục trên; đọc `doc_context_discount_*` (trước là dead field) |
| `Classification/FileScorer.cs` (mới) | S1 cấp file: gộp theo rule (noisy-OR top 5 × mật độ) → gộp theo mức |
| `Classification/ScoreRouter.cs` (mới) | Router: chắc chắn / vùng xám (cần LLM) / tín hiệu yếu |
| `Classification/Modules/CompoundRuleModule.cs` | Regex loại trừ timeout → coi như không loại trừ (trước: mất cả module) |
| `Storage/ResultStore.cs` (mới) | SQLite: `file_state`, `scan_result`, `finding` — không lưu giá trị khớp |
| `Watching/FileWatcherService.cs` | Log timeout/chưa kết luận; S1 + Router; ghi DB |
| `Worker.cs` | Đọc cấu hình `Score`, `Storage`; mở DB |
| `rules/` (8 file JSON) | Gắn `validators`; sửa regex IBAN p2 (nhóm cuối ngắn bị cắt) |

## 4. Công cụ mới (`tools/`)

| Công cụ | Lệnh | Dùng để |
|---|---|---|
| `tools/perf/Measure-DlpPerformance.ps1` | `.\tools\perf\Measure-DlpPerformance.ps1 -SequentialCount 60 -BurstCount 750` | Đo service đang chạy từ bên ngoài: độ trễ, thông lượng, CPU/RAM |
| `tools/eval/DlpBaseline` | `dotnet run -c Release --project tools/eval/DlpBaseline -- "D:\dlp-classifier\rawdata"` | Precision/recall, tốc độ từng bước, chọn ngưỡng S1, đánh giá Router. Kết quả ở `tools/eval/results/` |

## 5. Cấu hình, thư viện, log

**`appsettings.json`** (mới thêm):

```json
"Score":   { "THigh": 0.80, "TLow": 0.10 },
"Storage": { "DbPath": "data\\dlp.db" }
```

**NuGet mới:** `Microsoft.Data.Sqlite` 10.0.12.

**Dòng log mới** (chỉ tên rule, đường dẫn, điểm — không có giá trị nhạy cảm):

```
warn: [DLP] <file>: quét CHƯA TRỌN — 1 rule quá thời gian: <tên rule>
warn: [DLP] <file>: CHƯA KẾT LUẬN ĐƯỢC — không gắn mức, cần xem lại (N chỗ khớp tìm được trước khi dừng)
info: [DLP] <file>: độ tin cậy 0.92 (chắc chắn) — final
warn: [DLP] <file>: độ tin cậy 0.61 (vùng xám 0.10-0.80) — cần LLM thẩm định, needs_review
warn: [DB] Không ghi được kết quả <file>: <lỗi SQLite>
```

Công cụ đọc log (`Measure-DlpPerformance.ps1`) đã được cập nhật theo.

## 6. Việc cần làm để áp dụng

Chạy bằng PowerShell **Administrator**:

```powershell
sc.exe stop DlpService
dotnet publish D:\Service-DLP\DlpService\DlpService.csproj -c Release -r win-x64 --self-contained false -o D:\Service-DLP\publish
sc.exe start DlpService
```

Sau khi chạy: DB nằm ở `D:\Service-DLP\publish\data\dlp.db`; log có thêm các dòng ở mục 5.

## 7. Điểm cần biết / hạn chế

- **Dữ liệu tổng hợp có số "không tồn tại"** (SĐT đầu 095/057/053/054/080, CCCD số thứ 4 = 4–9, MST sai checksum) → validator loại đúng, nhưng vì vậy **không đo được recall thật** của SĐT/CCCD/MST trên rawdata.
- **S1 còn yếu:** dùng S1 để tự quyết định mức làm giảm recall (95,0 % so với 96,7 %) → hiện chỉ dùng S1 làm độ tin cậy cho Router; ~20 % file vùng "chắc chắn" vẫn bị xếp sai (nhiều tín hiệu yếu cùng bắn).
- **NonBacktracking không dùng được đại trà:** trên .NET 10.0.12, NonBacktracking + timeout trả match sai (tái hiện 3/3) và 1 rule dùng lookaround.
- Lần đo baseline có lúc máy ngủ giữa chừng → đã sửa công cụ để hiển thị đúng giờ; lượt đo đó chỉ lệch số liệu tốc độ PDF.

## 8. Việc còn mở

| Việc | Ghi chú |
|---|---|
| Đo Phi-4-mini INT4 | Chờ chọn: A (Ollama, có sẵn `phi4-mini`), B (ONNX, tải ~4,7 GB), C (cả hai) |
| Giới hạn tổng thời gian quét mỗi file / check ReDoS tự động | Đã đánh giá, chờ chọn |
| Cải thiện S1 | context_penalty, gộp theo cột, hạ trọng số `vn_name_001`/`health_001`; rồi chạy lại chọn ngưỡng |
| Giai đoạn 3 (dùng S1 quyết định mức) | Chỉ làm khi S1 không giảm recall |
| Checklist ghi .NET 8 | Project đang dùng .NET 10 — sửa checklist hoặc hạ phiên bản |
| Kiến trúc: phụ lục 17.1, 17.2 (prompt, schema) | Tài liệu gốc chưa có |
