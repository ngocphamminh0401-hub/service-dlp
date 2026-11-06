# Buổi 10 — Merger S_final (mục 7): công thức, nối pipeline, gộp chunk, lưu vết, unit test, lệnh chẩn đoán

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái | Mục kiến trúc |
|---|---|---|
| Công thức `S_final` + bảng quyết định (mục 7.1/7.2) — `Merger.cs` | ✅ Xong | 7.1, 7.2 |
| Luật an toàn: `hard_rule` không bị bác bỏ, `llm_policy: never`, ngưỡng `conf >= 0.8`, `needs_review` | ✅ Xong | 7.2 |
| Nối Merger vào pipeline thật (`LlmJobDispatcher` → `ResultStore.CompleteJob` ghi đè `scan_result`) | ✅ Xong | 7 |
| `llm_policy` (always/verify_if_ambiguous/never) đọc từ rule JSON | ✅ Xong (field mới, rule cũ mặc định `verify_if_ambiguous`) | 7.2 |
| Gộp nhiều chunk cùng rule + xử lý verdict mâu thuẫn ("không chắc", không hạ điểm) | ✅ Xong | 7.3 |
| Lưu vết đủ 8 trường: S1, S2, S_final, source, model_ver, prompt_ver, rules_ver, merge_policy_ver | ✅ Xong (2 cột mới `scan_result.rules_ver/merge_policy_ver` + hàm đọc gộp `GetTrace`/`GetExplain`) | 7.4 |
| Xử lý "Timeout, lỗi, LLM tắt → status=final" | ✅ Xong (phát hiện + vá lỗi: trước đó file kẹt vĩnh viễn ở "provisional") | 7.2 |
| Xử lý "file_hash đổi lúc chờ → bỏ kết quả LLM, quét lại Pass 1" | ✅ Đã đúng từ trước (kiểm tra lại, không cần sửa) | 7.2 |
| Unit test toàn bộ bảng quyết định (`DlpService.Tests`, xUnit — project test đầu tiên của repo) | ✅ Xong, 22/22 pass | 7 |
| Lệnh chẩn đoán `DlpService.exe explain <file_id>` | ✅ Xong | 7.4 |

---

## 1. Công thức merge + bảng quyết định (mục 7.1/7.2)

File mới `DlpService/Classification/Merger.cs`:

- `Merger.MergeOne(s1Rule, hardRule, policy, verdict)` — merge 1 rule:
  - `policy == Never` hoặc không có verdict → giữ nguyên `s1Rule`.
  - LLM xác nhận → `clamp(s1Rule + W_UP·conf, 0, 1)` (`W_UP = 0.30`).
  - LLM bác bỏ + `hard_rule=true` → giữ nguyên (luật an toàn, LLM không được bác bỏ rule có validator checksum).
  - LLM bác bỏ + `conf >= 0.8` → `clamp(s1Rule − W_DOWN·conf, 0, 1)` (`W_DOWN = 0.40`).
  - LLM bác bỏ + `conf < 0.8` → giữ nguyên, `needs_review = true`.
- `Merger.MergeFile(s1File, findings, verdicts, policyOf)` — merge cấp file: mỗi finding merge riêng qua `MergeOne`, `S_final` cấp file = **max** trong các rule (mục 7.3 cho chọn 1 trong 2 cách, chọn max để nhất quán với `ScoreRouter`); `source = "llm"` nếu có ít nhất 1 rule được LLM tác động hoặc LLM phát hiện thêm, ngược lại `"regex"`.
- `MergePolicyVersion.Current = "1.0"` (file mới, cùng kiểu `ContractVersion`) — tăng khi đổi hằng số hoặc logic bảng quyết định.

## 2. `llm_policy` theo rule (mục 7.2)

- `RuleDefinition.LlmPolicy` (đọc field JSON `"llm_policy"`), `SensitivePattern.LlmPolicy` (enum `DlpService.Scanning.LlmPolicy`), `RuleLoader.ParseLlmPolicy` (giá trị lạ/thiếu → `VerifyIfAmbiguous`, an toàn nhất — giữ đúng hành vi cũ).
- `RegexScanner.PolicyOf(ruleKey)` — tra theo `RuleId ?? Name`, khớp đúng cách `FindingRecord.RuleId` đang được ghi.
- **Rule hiện có trong repo CHƯA rule nào khai `llm_policy`** → tất cả đang chạy `VerifyIfAmbiguous` mặc định. Việc gán `never`/`always` cho rule cụ thể (VD rule checksum quan trọng) để dành cho buổi sau.

## 3. Nối vào pipeline thật (mục 7)

- `LlmJob` (record) mang thêm `S1` (join sẵn từ `scan_result` lúc `TryPickNextJob`).
- `ResultStore.CompleteJob` nhận thêm `ScanMergeUpdate?` (SFinal, NeedsReview, Source, MergePolicyVer) — ghi đè `scan_result` trong CÙNG transaction với `llm_result`, có điều kiện `WHERE file_hash = $hash` để không ghi đè lên bản quét Pass 1 mới hơn nếu file đổi trong lúc chờ.
- `LlmJobDispatcher.TryDispatchOnceAsync`: sau khi có response OK + hash khớp lần 2 → gọi `Merger.MergeFile` bằng `findings` đã lưu + `response.Verdicts` (đổi sang `RuleVerdict` thuần, tách khỏi kiểu gRPC) + `scanner.PolicyOf` → đưa kết quả vào `CompleteJob`.

## 4. Gộp nhiều chunk + verdict mâu thuẫn (mục 7.3)

`Merger.MergeFile` gom verdict theo `rule_id` (trước đây dùng `ToDictionary` — sẽ **ném exception** nếu 2 chunk cùng trả verdict cho 1 rule, lỗi tiềm ẩn chưa lộ vì chưa test nhiều chunk):

- Đồng thuận (tất cả xác nhận hoặc tất cả bác bỏ) → gộp thành 1 verdict, lấy `confidence` **cao nhất** (tín hiệu mạnh nhất), merge bình thường.
- Mâu thuẫn (vừa xác nhận vừa bác bỏ cùng rule) → đúng nguyên văn mục 7.3: giữ nguyên S1 (không hạ, không nâng), `needs_review = true`.

## 5. Lưu vết đủ 8 trường (mục 7.4)

- `rules_ver`: `RuleLoader.ComputeRulesVersion(folderPath)` — SHA-256 nội dung mọi file rule JSON (đệ quy), tính 1 lần lúc `Worker` khởi động, gắn vào `RegexScanner.RulesVer`, ghi vào `scan_result.rules_ver` mỗi lần `SaveScan`.
- `merge_policy_ver`: ghi `MergePolicyVersion.Current` lúc Pass 1 (S_final=S1, "chính sách" hiện có là pass-through), ghi đè lại đúng phiên bản THẬT lúc `CompleteJob` merge (phòng khi công thức đổi trong lúc chờ LLM).
- 2 cột mới `scan_result.rules_ver`, `scan_result.merge_policy_ver` (migration `ALTER TABLE`, tách 2 try/catch riêng — cùng kiểu `needs_ocr` đã có).
- `ResultStore.GetTrace(path)` — đọc gộp 8 trường thành 1 `ScanTrace` (join `scan_result` + `llm_job`/`llm_result` theo ĐÚNG `file_hash` hiện tại), phục vụ debug/audit.

## 6. Vá lỗi: job hết lượt thử vẫn kẹt "provisional" (mục 7.2)

Phát hiện khi rà lại bảng quyết định: `RetryOrFail` (trong `ResultStore.LlmJobs.cs`) khi job hết `MaxJobAttempts` (3 lần) chỉ đổi `llm_job.status → failed`, **không đụng `scan_result`** → sai với "Timeout, lỗi, LLM tắt → **status=final**; giữ điểm regex". Đã thêm `MarkScanFinalAfterJobFailed` — chuyển `scan_result.status = 'final'` đúng `(file_id, file_hash)` của job, giữ nguyên `needs_review` (Router đã đặt đúng `true` cho P1/vùng xám từ Pass 1).

Nhánh "file đổi trong lúc chờ (file_hash khác) → bỏ kết quả LLM, quét lại Pass 1" đã kiểm tra lại — code cũ (`MarkJobStale`) đã đúng từ trước, không cần sửa.

## 7. Unit test (project test đầu tiên của repo)

`DlpService.Tests/` (xUnit, `dotnet new xunit`, tham chiếu `DlpService.csproj`):

- `Classification/MergerTests.cs` — 18 test, mỗi test đặt tên theo đúng câu chữ 1 dòng của bảng quyết định để đối chiếu ngược tài liệu: xác nhận nâng điểm, bác bỏ hạ điểm theo `conf >= 0.8`, đúng biên `conf = 0.8`, clamp `[0, 1]`, `hard_rule` không bị bác bỏ (kể cả khi lẫn với rule khác trong cùng file), `llm_policy: never` (kể cả khi chỉ áp dụng cho 1 rule, rule khác vẫn merge bình thường), gộp nhiều chunk đồng thuận, chunk mâu thuẫn (kể cả khi mọi verdict đều đủ `conf` để hạ nếu xét riêng lẻ), LLM phát hiện thêm rule mới (`rule_id` rỗng).
- `Storage/ResultStoreExplainTests.cs` — 4 test tích hợp thật (SQLite file tạm, không mock) qua toàn bộ vòng đời `SaveScan → TryPickNextJob → CompleteJob → GetExplain/GetTrace`.

**Kết quả: 22/22 pass.**

## 8. Lệnh chẩn đoán `explain <file_id>` (mục 7.4)

- `Program.cs`: đọc `args[0] == "explain"` TRƯỚC khi dựng `Host` — không khởi động Windows Service/Worker, chỉ chạy lệnh rồi thoát.
- `Diagnostics/ExplainCommand.cs` + `Storage/ResultStore.Explain.cs` (`GetExplain`, gộp `file_state` + `scan_result` + `finding` + `llm_job`/`llm_result` theo đúng `file_hash` hiện tại — không gọi lồng `GetFindings`/`GetTrace` công khai để tránh khoá lồng `_writeLock`).
- In ra: path/size/mtime/hash, status/source/needs_review/needs_ocr, S1/S_final, `rules_ver`/`merge_policy_ver` kèm **cảnh báo nếu khác** bộ rule/công thức hiện tại trên đĩa (đọc lại rule để so sánh), danh sách finding kèm `llm_policy` từng rule, trạng thái job + kết quả LLM (model_ver/prompt_ver/s2/confidence/json_out) nếu có.
- Không in giá trị thật của file (đúng mục 5 CLAUDE.md) — mọi bảng nguồn vốn đã không lưu giá trị thật.
- Đã smoke-test thủ công: thiếu tham số, `file_id` không parse được, DB không tồn tại — cả 3 đều báo lỗi đúng, exit code 1.

---

## 9. Bảng file đã thay đổi/thêm mới trong buổi 10

| File | Thay đổi | Lý do |
|---|---|---|
| `DlpService/Classification/Merger.cs` (mới) | `MergeOne` + `MergeFile` — công thức + bảng quyết định + gộp chunk/mâu thuẫn | Mục 7.1, 7.2, 7.3 |
| `DlpService/Classification/MergePolicyVersion.cs` (mới) | Hằng số phiên bản công thức merge | Mục 7.4 |
| `DlpService/Scanning/RuleDefinition.cs`, `SensitivePattern.cs`, `RuleLoader.cs` | Thêm đọc/parse `llm_policy`; `RuleLoader.ComputeRulesVersion` (rules_ver) | Mục 7.2, 7.4 |
| `DlpService/Scanning/RegexScanner.cs` | `PolicyOf(ruleKey)`; constructor nhận thêm `rulesVer` → `RulesVer` | Mục 7.2, 7.4 |
| `DlpService/Worker.cs` | Tính `rulesVer` 1 lần lúc nạp rule, truyền vào `RegexScanner` | Mục 7.4 |
| `DlpService/Storage/ResultStore.cs` | `ScanRecord` thêm `RulesVer`/`MergePolicyVer`; 2 cột mới + migration; `ScanTrace` + `GetTrace` | Mục 7.4 |
| `DlpService/Storage/ResultStore.LlmJobs.cs` | `LlmJob` thêm `S1`; `CompleteJob` nhận `ScanMergeUpdate?` ghi đè `scan_result`; vá lỗi `status=final` khi job failed hẳn | Mục 7, 7.2, 7.4 |
| `DlpService/Storage/ResultStore.Explain.cs` (mới) | `ExplainRecord` + `GetExplain(fileId)` | Mục 7.4 |
| `DlpService/Llm/LlmJobDispatcher.cs` | Gọi `Merger.MergeFile` trước khi `CompleteJob` | Mục 7 |
| `DlpService/Watching/FileWatcherService.cs` | `TrySave` truyền `RulesVer`/`MergePolicyVer` vào `ScanRecord` | Mục 7.4 |
| `DlpService/Diagnostics/ExplainCommand.cs` (mới) | Lệnh `explain <file_id>` | Mục 7.4 |
| `DlpService/Program.cs` | Rẽ nhánh `explain` trước khi dựng Host | Mục 7.4 |
| `DlpService.Tests/` (mới, cả project) | xUnit — `MergerTests.cs` (18 test), `ResultStoreExplainTests.cs` (4 test) | Mục 7 |

---

## 10. Việc còn để lại (theo đúng thứ tự phụ thuộc)

| Việc | Mục kiến trúc | Ghi chú |
|---|---|---|
| **Khai báo `llm_policy` thật trong rule JSON** (hiện tất cả đang mặc định `verify_if_ambiguous`) | 7.2 | Cần bạn tự quyết rule nào nên `never` (VD rule đã có validator checksum riêng, không cần LLM can thiệp) hay `always` |
| **`Classify` sinh `Label`/`Verdict` thật theo 56 rule cũ** — hiện `DlpLlmHost` (LLM thật) mới chỉ trả `SensitivityLevel` 4 mức; `Verdict.rule_id/true_positive/confidence` đã có trong hợp đồng và được `Merger` dùng, nhưng **chưa có code nào ở `DlpLlmHost` thực sự điền vào** | 6.5, 7 | Nợ từ buổi 9 — `Merger` đã sẵn sàng nhận, chỉ chờ nguồn dữ liệu thật. Không có bước này thì `response.Verdicts` luôn rỗng khi chạy với `DlpLlmHost` thật (chỉ có dữ liệu khi test với `DlpFakeLlm`) |
| **Test `Merger` với dữ liệu THẬT** qua toàn luồng `DlpLlmHost` (không chỉ unit test cô lập) | 7, 13.3 | Cần có Label/Verdict thật trước (mục ngay trên) |
| **Đăng ký Windows Service thật** — đã chuẩn bị từ buổi 9, vẫn CHƯA thực thi cài đặt | 6.1, 6.3, 14 | Nợ cũ, chưa liên quan buổi này |
| Bản DirectML (GPU) nếu hiệu năng CPU không đạt | 6.2 | Nợ cũ |
| Đặt tên pipe không đoán trước được + tài khoản dịch vụ quyền thấp cho `DlpLlmHost` | 5.5, 6.1, 8 | Nợ cũ |
