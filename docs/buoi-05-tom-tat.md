# Buổi 5 — Độ tin cậy watcher (Nhóm D3) + Bậc 3 của engine phân loại (#1-#5, #7a) + đọc nhiều định dạng file

Buổi này gồm 3 phần lớn (phần 3 xem mục 8):

1. **Hoàn thiện Nhóm D** (độ tin cậy giám sát thư mục, `docs/buoi-04-tom-tat.md` mục 6): bắt sự kiện `watcher.Error`
   để không còn "câm" khi buffer tràn, và vá rủi ro nhiều luồng cùng đụng vào 2 dictionary trong `FileWatcherService`.
2. **Bậc 3 của engine** (`docs/buoi-03-tom-tat.md` mục 4): cho engine đọc các trường đã khai sẵn trong rule JSON nhưng
   trước đây bị bỏ qua (dead field), và bắt đầu chấm điểm tin cậy cho từng chỗ khớp.
3. **Đọc nhiều định dạng file**: ngoài `.txt`, service giờ đọc được `.csv .docx .pptx .xlsx .xls .pdf` (mục 8).

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái | Mục |
|---|---|---|
| Phân tích lỗi xoá nhầm CTS (race condition) | Đã vá từ trước, bịt hẳn bằng `TryRemove(KeyValuePair)` | 2, 4 |
| D3 — bắt `watcher.Error`, buffer 64KB, quét bù khi tràn | ✅ Xong, **chưa test thực tế** | 3, 5 |
| `Dictionary` → `ConcurrentDictionary` | ✅ Xong | 4 |
| Bậc 3 #1 `escalation` | ✅ Xong + test | 5b |
| Bậc 3 #3 `min_confidence_bypass` | ✅ Xong + test | 5c |
| Bậc 3 #2 `proximity_window` / `require_keyword_within` + chế độ bảng | ✅ Xong + test | 5d |
| Bậc 3 #4 `volume_escalation` | ✅ Xong + test | 5e |
| Bậc 3 #5 `placeholder_exclusion` | ✅ Xong + test | 5f |
| Bậc 3 #7a — `ScanMatch.Confidence` + log điểm | ✅ Xong (không đổi hành vi) | 5g |
| Bậc 3 #7 — đánh giá ngưỡng `min_confidence` | ✅ Đánh giá xong, đề xuất **0.40**, **chưa áp dụng vào code** | 5g |
| Bậc 3 #6, #7b, #8 | Chưa làm | 7 |
| Mở rộng định dạng: `.txt .csv .docx .pptx .xlsx .xls .pdf` | ✅ Xong 4 bước + test | 8 |

Tất cả thay đổi code đều build thành công (0 warning, 0 error). Mọi test Bậc 3 chạy bằng console scratch nạp 63 rule
thật (đã xoá sau khi test). **Service đang chạy dưới SCM vẫn là bản publish cũ**, chưa publish lại sau buổi này.

---

## 1. Trạng thái Nhóm D khi bắt đầu buổi

Đối chiếu code hiện tại với bảng đề xuất cuối buổi 4:

| # | Việc | Trạng thái |
|---|---|---|
| D-mới | Bỏ qua quét trùng khi nội dung không đổi (`_lastContentHash`, SHA256) | Đã có trong code |
| D1 | Quét toàn bộ file có sẵn khi khởi động (`ScanExistingFiles`, tách `ProcessFile` dùng chung) | Đã có trong code |
| D2 | `IncludeSubdirectories = true` | Đã có trong code |
| D4 | `catch (Exception)` rộng trong `ProcessAfterDebounceAsync` + dọn `_pendingTimers` trong `finally` | Đã có trong code |
| **D3** | Bắt `watcher.Error` (buffer tràn) | **Làm trong buổi này** |

---

## 2. Phân tích: xoá nhầm CTS trong `finally` (race condition)

### Vấn đề được nêu

Nếu `finally` của `ProcessAfterDebounceAsync` xoá `_pendingTimers[filePath]` **vô điều kiện**:

```
Task A qua Task.Delay, đang chạy ProcessFile("test.txt")    ← A đã quá điểm huỷ được
        ↓
Sự kiện mới → ScheduleProcess → Cancel A (vô tác dụng) → _pendingTimers["test.txt"] = CTS B
        ↓
Task A xong → finally → Remove("test.txt")                  ← xoá NHẦM CTS B
        ↓
Sự kiện thứ 3 đến → không tìm thấy B để huỷ → B và C cùng chạy → debounce bị phá
```

### Kết luận

- Lỗi **có thật**. Code tại thời điểm đó **đã được vá** bằng cách chỉ xoá khi CTS trong dict đúng là của
  chính task này (so sánh `Token`).
- Đoạn code gợi ý dán vào (`currentCts == newCts`) **không build được nguyên văn**: `newCts` là biến cục bộ của
  `ScheduleProcess`, không tồn tại trong `ProcessAfterDebounceAsync` (hàm này lúc đó chỉ nhận `CancellationToken`).
- Bản vá "`TryGetValue` rồi `Remove`" vẫn còn **khe hở nhỏ**: đó là 2 bước rời, luồng khác vẫn có thể chen vào
  giữa. Khe hở này được bịt hẳn ở mục 4 bên dưới.

---

## 3. D3 — Bắt `watcher.Error`

### Vì sao cần

`FileSystemWatcher` giữ 1 hàng đợi sự kiện có giới hạn (mặc định 8KB). Khi rất nhiều file đổi cùng lúc (đồng bộ
OneDrive, giải nén zip...), hàng đợi đầy và Windows **vứt bỏ sự kiện dư**, rồi báo qua sự kiện `Error`. Không đăng
ký `Error` → mất sự kiện âm thầm → với DLP nghĩa là **file nhạy cảm lọt qua mà không có dòng log nào**.

### Hai loại lỗi được phân biệt

| Loại | Watcher còn chạy? | Cách xử lý |
|---|---|---|
| `InternalBufferOverflowException` (tràn buffer) | Còn, chỉ mất 1 số sự kiện | Log cảnh báo → gọi lại `ScanExistingFiles` để quét bù. `_lastContentHash` tự lọc file nội dung không đổi, nên không bị log lặp |
| Lỗi khác (thư mục bị xoá, mất quyền, mất mạng...) | Có thể đã ngừng hẳn | Log lỗi. Tự khởi động lại watcher: **chưa làm** (xem mục 7) |

### Thay đổi

| Thay đổi | Lý do |
|---|---|
| Field `_watcher` thay cho biến cục bộ `var watcher` trong `StartWatching` | Biến cục bộ mất tham chiếu khi hàm kết thúc → GC có thể thu hồi watcher → service "câm" không rõ lý do. Field cũng cần cho việc khởi động lại/`Dispose` sau này |
| Field `_watchFolder` | `OnWatcherError` cần biết thư mục nào để quét bù |
| `InternalBufferSize = 64 * 1024` | Tăng buffer từ 8KB lên mức tối đa khuyến nghị để giảm khả năng tràn. Tăng hơn nữa sẽ tốn bộ nhớ kernel (non-paged pool) |
| `_watcher.Error += OnWatcherError;` + hàm mới `OnWatcherError` | Xử lý 2 loại lỗi như bảng trên |

---

## 4. An toàn đa luồng — `Dictionary` → `ConcurrentDictionary`

### Ai đụng vào 2 dictionary, trên luồng nào

| Người gọi | Luồng | Dict bị đụng |
|---|---|---|
| `ScheduleProcess` | Luồng sự kiện của watcher | `_pendingTimers` |
| `finally` của `ProcessAfterDebounceAsync` | ThreadPool (sau `await Task.Delay`) | `_pendingTimers` |
| `ProcessFile` gọi từ nhiều Task debounce (nhiều file hết giờ cùng lúc) | ThreadPool, **nhiều luồng song song** | `_lastContentHash` |
| `ProcessFile` gọi từ `ScanExistingFiles` trong `OnWatcherError` | Luồng của watcher | `_lastContentHash` |

`Dictionary` thường chỉ an toàn khi **1 luồng** dùng. Rủi ro này đã tồn tại từ trước (nhiều Task debounce song
song), D3 làm nó nặng hơn: tràn buffer xảy ra đúng lúc **đông sự kiện nhất**, và lúc đó `ScanExistingFiles` lại
chen vào quét cả thư mục.

### Hai loại hậu quả

**(a) Hỏng cấu trúc bên trong dict (nghiêm trọng).** Khi dict tự nới rộng (tạo mảng mới, chép dữ liệu sang) mà
luồng khác ghi chen vào, có thể mất dữ liệu, ném `InvalidOperationException`, hoặc làm dict hỏng vĩnh viễn.
→ **Đã sửa** bằng `ConcurrentDictionary`.

**(b) Tranh nhau cùng 1 file (nhẹ).** 2 luồng cùng xử lý `a.txt` lúc nội dung vừa đổi v1 → v2:

```
Luồng Task debounce                     Luồng ScanExistingFiles
đọc a.txt → v1 (đọc sớm)
                                        đọc a.txt → v2, hash khác → quét, log v2, lưu hash(v2)
hash(v2) ≠ v1 → quét, log v1
lưu hash(v1)   ← sổ ghi sai, file thật đang là v2
```

Hậu quả: log trùng/sai thứ tự, lần sau quét lại thừa 1 lần. **Không bị sót** dữ liệu nhạy cảm.
→ **Chấp nhận, chưa sửa**, vì `ConcurrentDictionary` không bịt được chuỗi 3 bước "đọc → so sánh → ghi". Muốn
triệt để phải `lock` theo từng file, phức tạp hơn nhiều so với lợi ích.

### Thay đổi

| Chỗ sửa | Trước | Sau |
|---|---|---|
| `using` | — | `System.Collections.Concurrent` |
| `_pendingTimers`, `_lastContentHash` | `Dictionary<...>` | `ConcurrentDictionary<...>` |
| `ScheduleProcess` | Truyền `newCts.Token` | Truyền nguyên `newCts` |
| Tham số `ProcessAfterDebounceAsync` | `CancellationToken cancellationToken` | `CancellationTokenSource myCts` |
| `finally` | `TryGetValue` + so sánh Token + `Remove` (2 bước rời) | `TryRemove(new KeyValuePair<...>(filePath, myCts))` |

`TryRemove(KeyValuePair)` = "chỉ xoá nếu value hiện tại **đúng là** `myCts`", so sánh và xoá **liền 1 bước**
(atomic), không luồng nào chen vào giữa được. Đây là lý do phải truyền cả CTS thay vì chỉ Token.

`ProcessFile` không phải sửa: `TryGetValue` và `dict[key] = value` dùng y hệt `Dictionary`.

---

## 5. `try/catch` quanh quét bù trong `OnWatcherError`

### Mục đích chính: không để service sập (ghi log chỉ là phụ)

| | Task debounce (`ProcessAfterDebounceAsync`) | `OnWatcherError` |
|---|---|---|
| Có `catch (Exception)` từ trước? | Có (D4) | Không |
| Exception thoát ra thì sao? | Task chạy rời (`_ = ...`) → lỗi bị nuốt, không sập | Event handler chạy trên ThreadPool → exception không ai bắt → **sập cả tiến trình** |

Ví dụ lỗi có thể xảy ra: `Directory.GetFiles` gặp thư mục vừa bị xoá hoặc thư mục con không có quyền đọc;
`_scanner.Scan`/`_engine.Classify` gặp dữ liệu lạ.

`catch` là **tấm lưới** (service không rơi), `LogError` trong `catch` là **ghi lại** việc đã rơi xuống lưới, để
biết từng có lỗi. Cần cả 2.

### Quyết định: bọc bên ngoài, trong `OnWatcherError`

Bọc quanh lời gọi `ScanExistingFiles(_watchFolder)`. **Hạn chế đã biết:** 1 file lỗi sẽ dừng cả lượt quét bù (file
thứ 5/100 lỗi → 95 file sau không được quét). Nếu thấy cần, có thể bọc thêm quanh từng `ProcessFile` bên trong
vòng `foreach` của `ScanExistingFiles`. Cách đó bảo vệ luôn cả lượt quét lúc khởi động (D1).

---

## 5b. Bậc 3 #1 — `escalation` (nâng mức khi có từ khoá nguy hiểm gần chỗ khớp)

Tiếp tục danh sách Bậc 3 trong `docs/buoi-03-tom-tat.md`, mục 4. Trường `escalation` đã khai sẵn trong 3 rule
nhưng trước buổi này engine bỏ qua (dead field):

| Rule | Từ khoá | Nâng lên |
|---|---|---|
| `pii/cccd.json` | mật khẩu, password, sinh trắc học, biometric, vân tay | SECRET |
| `org/contract.json` | quốc phòng, an ninh quốc gia, tối mật, tài liệu mật, bí mật nhà nước | SECRET |
| `org/internal-ip.json` | password, mật khẩu, credentials, username, login, admin, root, ssh, rdp, telnet | CONFIDENTIAL |

**Ý tưởng:** compound rule nhìn **cả file** ("có hợp đồng VÀ có CCCD"), còn escalation nhìn **cục bộ quanh 1 chỗ
khớp** ("số CCCD này đứng CẠNH chữ mật khẩu"). Hai cơ chế bổ sung cho nhau.

### 3 quyết định thiết kế (người dùng chọn)

| # | Câu hỏi | Quyết định | Lý do |
|---|---|---|---|
| Q1 | Vùng tìm từ khoá rộng bao nhiêu? | Dùng lại `ContextWindow` sẵn có của rule | JSON không khai vùng riêng; nhất quán với `context_required` |
| Q2 | Chỗ khớp đã nâng có qua `level_gate` không? | **Vẫn qua gate**, nhưng từ khoá escalation được tính là **1 bằng chứng củng cố** | Nếu không, `internal-ip` (fallback PUBLIC) sẽ bị gate hạ ngược về PUBLIC ngay sau khi nâng, escalation mất tác dụng. Nhánh `strict_tags_only` KHÔNG tính escalation (strict = chỉ dựa vào tag) |
| Q3 | Vừa có từ khoá escalation vừa có từ phủ định? | **Negation thắng** | "Ví dụ: ssh root@192.168.1.10" là tài liệu hướng dẫn, không phải hạ tầng thật |

Lưu ý với Q3: từ phủ định lấy từ `negation_filter.trigger_words` **của từng rule**, không có danh sách chung. Trong
3 rule trên chỉ `internal-ip` có `negation_filter`. KHÔNG nên thêm "không" vào trigger_words vì quá phổ biến
("Server không được public: 10.0.0.5" sẽ bị hạ nhầm).

### Luồng tính mức của 1 chỗ khớp sau thay đổi

```
baseSeverity = override_level ?? Severity
   → [MỚI] có từ khoá escalation trong vùng ContextWindow → max(base, escalate_to)   (chỉ nâng)
   → negationDowngrade?           → FallbackLevel          (Q3: xét đầu tiên)
   → gate áp dụng?                → corroborated (+ escalated) ? base : FallbackLevel   (Q2)
   → ngược lại                    → base
```

Mức đã nâng cũng được dùng ở vòng chuẩn bị `strongRuleNames`, nên 1 CCCD đã lên SECRET được tính là "rule mạnh",
giúp các rule khác cùng nội dung qua gate.

### Thay đổi

| File | Thay đổi |
|---|---|
| `Scanning/EscalationDefinition.cs` (mới) | Khuôn JSON thô: `keywords`, `escalate_to` |
| `Scanning/EscalationRule.cs` (mới) | Dạng "sẵn sàng": `Keywords`, `EscalateTo` |
| `Scanning/RuleDefinition.cs`, `SensitivePattern.cs` | Thêm property `Escalation` |
| `Scanning/RuleLoader.cs` | Chuyển Definition → Rule, gán vào cả 2 chỗ tạo pattern. Thiếu `keywords`/`escalate_to` thì **chỉ tắt escalation** + log cảnh báo, rule vẫn được nạp (khác negation/gate: thiếu escalation chỉ làm rule lỏng hơn, không chạy sai) |
| `Scanning/RegexScanner.cs` | Tuple `survivors` thêm cờ `Escalated`; vòng 1 đánh dấu cờ; hàm mới `ResolveBaseSeverity` dùng chung cho vòng chuẩn bị và vòng gộp; thêm `\|\| escalated` vào `corroborated` |
| `rules/org/contract.json`, `rules/org/internal-ip.json` | Sửa ghi chú "dead field" trong `notes[]` |

### Kết quả test (console scratch nạp 63 rule thật, đã xoá sau khi test)

| Kịch bản | Kết quả | Cơ chế |
|---|---|---|
| CCCD + "mật khẩu" | `vn_id_001=SECRET` | Nâng mức + qua gate nhờ escalation |
| CCCD không từ khoá | `vn_id_001=INTERNAL` | Đối chứng: gate hạ như cũ |
| IP + "ssh" | `internal_ip_001=CONFIDENTIAL` | Q2: không bị gate hạ về PUBLIC |
| IP không từ khoá | `internal_ip_001=PUBLIC` | Đối chứng |
| "Ví dụ: ssh root@192.168.1.10" | `internal_ip_001=PUBLIC` | Q3: negation thắng |
| Hợp đồng + "tối mật" | `contract_001=SECRET` | Nâng mức |
| Hợp đồng thường | `contract_001=INTERNAL` | Đối chứng |

---

## 5c. Bậc 3 #3 — `min_confidence_bypass` (miễn gate khi độ tin cậy cao)

7 rule khai trường này: `cccd`, `passport`, `social-insurance`, `watermark`, `internal-ip`,
`tax-code-personal-bare`, `bank-account-vn`.

### Vấn đề phát hiện khi phân tích

`confidence` trong JSON là **hằng số của từng pattern**. So thẳng với ngưỡng thì 6/7 rule **không bao giờ** bypass
(hằng số của mọi pattern bị gate đều < ngưỡng), còn `watermark` thì vài pattern **luôn luôn** bypass. Cả 2 đều vô
nghĩa, nên cần **độ tin cậy động**, tăng theo từ khoá quanh chỗ khớp.

### Công thức: dò ngược từ chính `notes[]` trong JSON

Repo không có code tham khảo gốc, nhưng 3 ghi chú cho ra 3 con số khớp đúng 1 công thức:

```
confidence_động = confidence × weight + boost   (boost tối đa 0.30)
```

| Ghi chú | Tính | Khớp |
|---|---|---|
| social-insurance p4 "tối đa ~0.898" | 0.68 × 0.88 + 0.30 = 0.898 | ✅ |
| passport p4 "~0.87" | 0.65 × 0.88 + 0.30 = 0.872 | ✅ |
| bank-account "conf=0.92 với label 'số tài khoản'" | 0.70 × 0.88 + 0.30 = 0.916 | ✅ |

### Quyết định (người dùng chọn)

| # | Quyết định |
|---|---|
| Q1 | Công thức `confidence × weight + boost`, boost tối đa 0.30 |
| Q2 | Mỗi từ khoá **primary** tìm thấy trong vùng `ContextWindow` +0.15, mỗi từ khoá **secondary** +0.05, cộng dồn tối đa 0.30. Mỗi từ khoá chỉ tính 1 lần |
| Q3 | Giống escalation: thêm `\|\| bypassed` vào `corroborated` ở nhánh không strict. Negation vẫn thắng |
| — | Giữ nguyên hành vi CCCD (xem "Hệ quả" bên dưới) |

### Thay đổi

| File | Thay đổi |
|---|---|
| `Scanning/LevelGateDefinition.cs`, `LevelGateRule.cs` | Thêm `MinConfidenceBypass` |
| `Scanning/SensitivePattern.cs` | Thêm `Confidence`, `Weight`, `PrimaryKeywords`, `SecondaryKeywords` (`Keywords` cũ vẫn là bản gộp, dùng cho `context_required`) |
| `Scanning/RuleLoader.cs` | Nạp các trường trên. Rule dạng cũ không có confidence theo pattern nên không bao giờ bypass |
| `Scanning/RegexScanner.cs` | Hàm mới `ComputeConfidence` (+ 3 hằng số `PrimaryBoost`/`SecondaryBoost`/`MaxBoost`); thêm `bypassed` vào `corroborated` |
| `rules/org/watermark.json`, `rules/org/internal-ip.json` | Sửa ghi chú "dead field" |

### Kết quả test (console scratch, 63 rule thật, đã xoá sau khi test)

| Kịch bản | Trước | Sau | Điểm |
|---|---|---|---|
| `Server database production: 10.20.30.40, firewall` | PUBLIC | **INTERNAL** | 0.66 + 0.30 = 0.96 |
| IP + chỉ "server" | PUBLIC | PUBLIC | 0.66 + 0.15 = 0.81 |
| "Ví dụ: server 10.20.30.40 sau firewall" | PUBLIC | PUBLIC | negation thắng |
| `Số tài khoản: 440037110392` | INTERNAL | **CONFIDENTIAL** | 0.616 + 0.30 = 0.916 (đúng con số trong notes) |
| STK chỉ có "chuyển khoản" | INTERNAL | INTERNAL | 0.616 + 0.15 = 0.77 |
| `Số CCCD của nhân viên: 012345678901` | INTERNAL | **CONFIDENTIAL** | 0.765 + 0.30 |
| `CMND 012345678901` | INTERNAL | **CONFIDENTIAL** | 0.765 + 0.15 = 0.915 |

### Hệ quả đã biết và chấp nhận

- **CCCD pattern_1 gần như luôn bypass:** base 0.85 × 0.9 = 0.765, chỉ cần 1 từ khoá primary là đạt 0.90, mà pattern
  này vốn có `context_required` (bắt buộc có từ khoá). Vì vậy CCCD đơn lẻ có nhãn → CONFIDENTIAL. Hợp lý về DLP (12 số
  cạnh nhãn "CCCD" gần như chắc chắn là PII thật), nhưng gate của CCCD p1 hầu như không còn tác dụng.
- **Từ khoá lồng nhau bị tính trùng:** "số CCCD" chứa luôn "CCCD" → tính 2 primary. Chính hiệu ứng này làm
  bank-account ra đúng 0.916 như notes, nên nhiều khả năng thiết kế gốc cũng tính như vậy.
- Nếu sau này thấy quá lỏng: giảm `PrimaryBoost` xuống 0.10, hoặc không tính trùng từ khoá lồng nhau (cả 2 cách đều
  làm bank-account không còn khớp con số 0.92 trong notes).

---

## 5d. Bậc 3 #2 — `proximity_window` (từ khoá phải nằm GẦN chỗ khớp)

**Vấn đề:** `context_required` tìm từ khoá trong vùng rộng 150-200 ký tự, nên 1 chữ "CCCD" ở câu trước có thể làm
mã đơn hàng 12 số ở câu sau bị nhận nhầm là CCCD.

### Hai dạng khai báo trong JSON

| Dạng | Vị trí | Rule | Nội dung |
|---|---|---|---|
| A. `proximity_window` | Cấp rule | cccd, passport, social-insurance, bank-account-vn, tax-code-personal-bare, tax-code-personal-labeled, income | `max_chars` 50-80, `same_sentence_required`, `keyword_scope` |
| B. `require_keyword_within` | Cấp pattern con | date-of-birth (p3/p4/p5), location-tracking (p1) | Chỉ khoảng cách (40-60) |

### Quyết định (người dùng chọn theo đề xuất)

| # | Quyết định |
|---|---|
| Q1 | Ranh giới câu = xuống dòng, hoặc `.` `!` `?` `;` **theo sau là khoảng trắng**. Dấu chấm trong số (`10.20.30.40`, `12.05.2020`) không bị coi là hết câu |
| Q2 | **Chế độ bảng:** chỗ khớp trượt proximity không bị loại ngay. Nếu cùng rule khớp ≥ 5 lần trong file (dấu hiệu bảng/danh sách) VÀ có từ khoá ở bất kỳ đâu trong file thì vẫn giữ |
| Q3 | `keyword_scope: "primary"` chỉ xét từ khoá primary. Thiếu/lạ → coi như `primary_or_secondary` |

### Chi tiết thiết kế thêm (không phải quyết định của người dùng)

- Proximity chỉ áp cho pattern có `context_required: true`, và **thay thế** kiểm tra `ContextWindow` rộng (không phải
  kiểm tra thêm). Pattern không khai proximity giữ nguyên hành vi cũ.
- Dạng B ưu tiên hơn dạng A; chỉ xét từ khoá **primary** (notes[] ghi rõ), **không xét câu**, và **không có chế độ bảng**,
  vì ngày sinh/năm sinh/toạ độ là loại dễ báo nhầm nhất.
- `max_chars` thiếu/≤ 0 → chỉ tắt proximity + log cảnh báo, rule vẫn được nạp.
- Boost độ tin cậy của `min_confidence_bypass` (mục 5c) vẫn tính trên vùng `ContextWindow` rộng, không bị proximity thu hẹp.

### Thay đổi cấu trúc trong `RegexScanner.Scan()`

- Vòng 1 thêm `candidateCounts` (đếm chỗ khớp qua validator theo tên rule) và danh sách `deferred` (chỗ khớp trượt
  proximity, chờ xét chế độ bảng sau khi quét hết).
- Khối negation_filter + escalation **tách ra hàm `AddSurvivor`**, để chỗ khớp được cứu nhờ chế độ bảng cũng đi qua đúng
  các bước lọc đó. Nội dung giữ nguyên, chỉ đổi `continue` → `return`.
- Hàm mới: `HasKeywordInProximity`, `ProximityKeywords`, `IsSentenceEnd`, `FindSentenceStart`, `FindSentenceEnd`;
  hằng số `TableModeMinMatches = 5`.

### Thay đổi file

| File | Thay đổi |
|---|---|
| `Scanning/ProximityWindowDefinition.cs`, `ProximityRule.cs` (mới) | Khuôn JSON thô / dạng sẵn sàng |
| `Scanning/RuleDefinition.cs` | Thêm `ProximityWindow` |
| `Scanning/PatternDefinition.cs` | Thêm `RequireKeywordWithin` |
| `Scanning/SensitivePattern.cs` | Thêm `Proximity` |
| `Scanning/RuleLoader.cs` | Dựng `ProximityRule` cấp rule; cấp pattern ưu tiên hơn; truyền vào cả 2 chỗ tạo pattern |
| `Scanning/RegexScanner.cs` | Như mục trên |
| `rules/pii/date-of-birth.json`, `rules/pii/location-tracking.json` | Sửa ghi chú "dead field" |

### Kết quả test (console scratch, 63 rule thật, đã xoá sau khi test)

| Kịch bản | Kết quả | Ghi chú |
|---|---|---|
| `Số CCCD: 012345678901` | 1 chỗ CONFIDENTIAL | Nhãn ngay cạnh |
| "Hồ sơ CCCD được lưu … theo quy định. Mã đơn hàng của bạn là 012345678901." | **không khớp** | Trước đây khớp (vùng rộng) |
| Bảng CSV 6 dòng, nhãn "Số CCCD" chỉ ở tiêu đề | **6 chỗ** | Chế độ bảng |
| Bảng CSV 3 dòng | **không khớp** | Dưới ngưỡng 5, xem "Hạn chế" |
| "Số CCCD của ông A (cấp ngày 12.05.2020) là 012345678901" | 1 chỗ | Dấu chấm trong ngày không cắt câu |
| `Ngày sinh: 12 tháng 5 năm 1990` | 2 chỗ (ngày đầy đủ + năm) | |
| "Ngày sinh của khách hàng không được ghi …, văn bản ký ngày 12 tháng 5 năm 1990" | **không khớp** | Xa hơn 40 ký tự |
| Regression escalation (CCCD+mật khẩu, IP+ssh, Ví dụ+ssh, Hợp đồng+tối mật) và bypass (IP+server/firewall, STK nhãn rõ) | Không đổi | |

Lần test này chạy trước khi sửa dạng B từ "primary + secondary" sang "chỉ primary". Các kịch bản ngày sinh dùng từ khoá
primary ("Ngày sinh") nên kết quả không đổi về logic; sau khi sửa chỉ build lại, chưa chạy lại test.

### Hạn chế đã biết

- **Bảng < 5 dòng bị bỏ sót** nếu nhãn chỉ ở dòng tiêu đề. Có thể hạ `TableModeMinMatches` nếu thấy cần.
- **Chế độ bảng có thể báo nhầm:** file có ≥ 5 dãy 12 số bất kỳ (VD mã đơn hàng) + 1 chữ "CCCD" ở đâu đó → tất cả bị tính là CCCD.
- `tax_code_personal_001` xuất hiện ở **2 file** (`tax-code-personal-bare.json` và `tax-code-personal-labeled.json`) với
  cùng `id`. Không ảnh hưởng proximity (đếm theo `Name`), nhưng cần xem lại nếu sau này có logic đếm theo `RuleId` (VD #4 `volume_escalation`).

---

## 5e. Bậc 3 #4 — `volume_escalation` (file chứa hàng loạt dữ liệu → nâng mức)

**Vấn đề:** file 200 số CCCD trước đây vẫn chỉ CONFIDENTIAL như file có 1 số, dù rò rỉ HÀNG LOẠT nguy hiểm hơn nhiều.

| Rule | Ngưỡng |
|---|---|
| cccd, passport, social-insurance, bank-account-vn | ≥ 50 → SECRET |
| email | ≥ 50 → CONFIDENTIAL, ≥ 200 → SECRET |

**Về 2 file trùng `id` `tax_code_personal_001` (hạn chế nêu ở mục 5d):** cả 2 không khai `volume_escalation`, và với 5
rule trên mỗi tên rule ứng đúng 1 `id`, nên đếm theo tên rule cũng chính là "đếm riêng theo rule.ID" như notes yêu cầu.

### Quyết định (người dùng chọn)

| # | Quyết định |
|---|---|
| Q1 | Đếm **giá trị khác nhau**, sau khi chuẩn hoá (bỏ khoảng trắng, `-`, `.`, viết hoa). 1 CCCD lặp 60 lần KHÔNG bị nâng |
| Q2 | Chỗ khớp bị negation hạ mức **không được đếm** và **không được nâng** (negation vẫn thắng, nhất quán #1/#3) |
| Q3 | `min_count: 50` nghĩa là **≥ 50** (theo tên trường) |

### Thiết kế

- Bước đếm mới chạy **sau** vòng 1 (và sau chế độ bảng), **trước** vòng chuẩn bị `strongRuleNames`, lưu vào
  `volumeLevels` (tên rule → mức). Nhờ vậy mức đã nâng cũng được tính là "rule mạnh" cho các rule khác.
- `ResolveBaseSeverity` nhận thêm `volumeLevel`: mức gốc = **cao nhất** trong 3 nguồn (override_level/Severity,
  escalation, volume). Chỉ nâng, không bao giờ hạ.
- Không cần đụng level_gate: ≥ 50 chỗ khớp thì điều kiện "khớp ≥ 2 lần" đã đúng sẵn.
- Nếu đạt nhiều bậc, lấy bậc cao nhất (loader sắp xếp `min_count` giảm dần).
- Bậc thiếu `min_count`/`escalate_to` bị bỏ; không còn bậc nào hợp lệ thì chỉ tắt volume + log cảnh báo, rule vẫn được nạp.

### Thay đổi file

| File | Thay đổi |
|---|---|
| `Scanning/VolumeEscalationDefinition.cs` (mới) | `VolumeEscalationDefinition` + `VolumeThresholdDefinition` (khuôn JSON thô) |
| `Scanning/VolumeEscalationRule.cs` (mới) | Dạng sẵn sàng + hàm `LevelFor(distinctCount)` |
| `Scanning/RuleDefinition.cs`, `SensitivePattern.cs` | Thêm property `VolumeEscalation` |
| `Scanning/RuleLoader.cs` | Lọc bậc hợp lệ, sắp xếp, gán vào cả 2 chỗ tạo pattern |
| `Scanning/RegexScanner.cs` | Bước đếm `volumeLevels`; `ResolveBaseSeverity` thêm tham số; hàm mới `NormalizeValue` |

### Kết quả test (console scratch, 63 rule thật, đã xoá sau khi test)

| Kịch bản | Kết quả |
|---|---|
| CSV 60 CCCD khác nhau | SECRET x60 |
| CSV 49 CCCD | CONFIDENTIAL x49 (dưới ngưỡng) |
| CSV đúng 50 CCCD | SECRET x50 (≥) |
| 1 CCCD lặp 60 lần | CONFIDENTIAL x60 (chỉ 1 giá trị khác nhau) |
| 30 / 60 / 200 email khác nhau | INTERNAL / CONFIDENTIAL / SECRET |
| Regression #1, #2, #3 (9 kịch bản) | Không đổi |

### Hạn chế đã biết

- **Q2 chưa test được với rule thật:** cả 5 rule có `volume_escalation` đều không khai `negation_filter`. Code đã xử lý,
  nhưng chỉ có tác dụng khi sau này có rule khai cả 2 trường.
- `ScanMatch` chưa mang lý do "nâng do số lượng", nên log/Reasons chưa cho biết 1 chỗ khớp lên SECRET vì volume hay vì
  mức gốc. Có thể bổ sung khi làm #7 (thêm thông tin vào `ScanMatch`).

---

## 5f. Bậc 3 #5 — `placeholder_exclusion` (loại giá trị mẫu/giả)

14 file khai `placeholder_exclusion`, gồm 3 loại trường:

| Trường | Rule | Làm ở |
|---|---|---|
| `known_test_values` | 7 file thẻ tín dụng (chung `credit_card_001`) | #5 |
| `value_blocklist_patterns` (+ `value_blocklist_full_match`) | credentials, cvv, otp ×3, email, vietnamese-name | #5 |
| `doc_context_discount_keywords/factor` | credentials, cvv, otp ×3 | #6 (chưa làm) |

### Vấn đề phát hiện khi phân tích

Blocklist viết cho **phần giá trị** và có neo `^...$`, nhưng regex rule bắt **cả nhãn lẫn giá trị**
(`password=changeme123`, `CVV: 123`, `mã OTP của bạn là 123456`, `1\nNguyễn Văn A`). So thẳng `match.Value` thì
blocklist của credentials/cvv/otp không bao giờ có tác dụng.

### Quyết định (người dùng chọn theo đề xuất)

| # | Quyết định |
|---|---|
| Q1 | Thử blocklist trên **nhiều ứng viên giá trị**: toàn bộ chỗ khớp; phần sau `:`/`=` cuối; phần sau xuống dòng cuối; từ cuối cùng. Mỗi ứng viên bỏ khoảng trắng, dấu nháy, dấu câu thừa ở cuối. 1 ứng viên khớp là đủ. Không sửa regex trong JSON |
| Q2 | Placeholder bị **loại hoàn toàn** khỏi kết quả (đúng notes của `email.json`; không tạo tag cho compound rule) |
| Q3 | Loại ngay sau validator, **trước bước đếm**, nên không góp vào chế độ bảng (#2) lẫn volume (#4) |

### Thiết kế thêm

- `known_test_values` so chính xác sau khi chỉ giữ chữ số (`4111 1111 1111 1111` = `4111111111111111`).
- `value_blocklist_full_match: true` → loader bọc regex thành `^(?:...)$`.
- Regex blocklist sai cú pháp → chỉ bỏ regex đó + log cảnh báo, rule vẫn được nạp.
- Regex blocklist bị timeout → coi như KHÔNG phải placeholder (thà giữ còn hơn bỏ sót dữ liệu thật).

### Thay đổi file

| File | Thay đổi |
|---|---|
| `Scanning/PlaceholderExclusionDefinition.cs`, `PlaceholderRule.cs` (mới) | Khuôn JSON thô / dạng sẵn sàng |
| `Scanning/RuleDefinition.cs`, `SensitivePattern.cs` | Thêm `PlaceholderExclusion` / `Placeholder` |
| `Scanning/RuleLoader.cs` | Biên dịch blocklist, chuẩn hoá số test, gán vào cả 2 chỗ tạo pattern |
| `Scanning/RegexScanner.cs` | Gọi `IsPlaceholder` ngay sau validator; hàm mới `IsPlaceholder`, `ValueCandidates` |
| `rules/pii/email.json`, `rules/org/credentials.json` | Sửa ghi chú "dead field" (credentials vẫn ghi doc_context_discount là dead, chờ #6) |

### Kết quả test (console scratch, 63 rule thật, đã xoá sau khi test)

| Kịch bản | Kết quả |
|---|---|
| `password=changeme123` / `{"password": "changeme"}` | Loại |
| `password=Xk9#mP2vQ7` | credentials SECRET (giữ) |
| `CVV: 123` / `CVV: 847` | Loại / CONFIDENTIAL |
| `mã OTP của bạn là 123456` / `… 583920` | Loại / SECRET |
| `4111 1111 1111 1111` / `4539 1488 0343 6467` | `credit_card_001` loại / `credit_card_001` SECRET |
| `noreply@vnpt.vn` / `nguyenvana@vnpt.vn` | `email_001` loại / `email_001` INTERNAL |
| `Họ và tên: Nguyễn Văn A` / `… Nguyễn Văn An` | Loại / INTERNAL |
| 60 email `support@congtyN.com` | Loại hết, không kích hoạt volume |
| Regression #1-#4 (9 kịch bản) | Không đổi |

### Hạn chế đã biết

- **Placeholder chỉ áp cho ĐÚNG rule khai nó.** Số thẻ test `4111…` vẫn bị rule khác `fin_credit_card_statement_001`
  bắt (CONFIDENTIAL); `noreply@vnpt.vn` vẫn bị `vnpt_email_001` (rule org, tín hiệu "email nội bộ VNPT") bắt. Nếu muốn
  loại cả ở đó, cần thêm `placeholder_exclusion` vào JSON của 2 rule đó (không cần sửa code).
- Ứng viên "từ cuối cùng" có thể loại nhầm nếu giá trị THẬT tình cờ trùng blocklist (VD mật khẩu thật đúng là
  `admin123`). Đây là đánh đổi do chính người viết rule chọn trong blocklist.

---

## 5g. Bậc 3 #7 — Chấm điểm tin cậy

### Bối cảnh

- Engine làm việc kiểu "khớp là tính": 5 module (Finance, PII, Org, HR, Compound) quyết định mức chỉ dựa vào `Severity`.
- `ComputeConfidence` (làm ở #3) đã tính đúng công thức `confidence × weight + boost`, nhưng chỉ được gọi trong gate
  để xét bypass rồi bỏ kết quả.
- JSON **không khai ngưỡng quyết định nào** (không có `min_confidence`/`threshold`), nên phải tự đặt và cần dữ liệu.

### Quyết định (người dùng chọn)

| Câu hỏi | Quyết định |
|---|---|
| Làm gì với điểm tin cậy? | Đặt **ngưỡng loại chỗ khớp điểm thấp** (`min_confidence`) |
| Ngưỡng đặt ở đâu? | **Theo từng rule trong JSON**, rule không khai thì dùng giá trị mặc định chung |
| Hiển thị điểm trong `Reasons` của module? | Không (theo mặc định) — chỉ hiển thị trong log từng chỗ khớp |
| Giá trị mặc định? | Người dùng yêu cầu đánh giá thực tế → đề xuất **0.40** (xem bên dưới) |

### Đã làm — giai đoạn 7a (không đổi hành vi phân loại)

| File | Thay đổi |
|---|---|
| `Scanning/ScanMatch.cs` | Thêm `double? Confidence` (null nếu pattern không khai `confidence`) |
| `Scanning/RegexScanner.cs` | Tính điểm **1 lần** cho mỗi chỗ khớp ở vòng gộp, gán vào `ScanMatch`, dùng lại cho bypass (thay vì tính riêng trong gate). `ComputeConfidence` giới hạn tối đa 1.0 |
| `Watching/FileWatcherService.cs` → `ProcessFile` | Log từng chỗ khớp thêm `(tin cậy 0.92)`, hoặc `(tin cậy —)` nếu không có điểm |

### Đánh giá ngưỡng — phân tích lý thuyết trên 221 pattern

Chỉ 1 pattern không khai `confidence` (rule dạng cũ `CreditCard`). Với 221 pattern còn lại:

| Ngưỡng | LUÔN bị loại (dù đủ từ khoá) | Bị loại khi ít từ khoá |
|---|---|---|
| 0.40 | 0 | 22 |
| 0.50 | 5 | 29 |
| 0.60 | 18 | 46 |

Phát hiện quan trọng: 6 rule tài chính nội bộ có `weight` rất thấp (0.35-0.45) — `fin_loan_approval_process_001`,
`fin_process_change_notice_001`, `fin_internal_risk_policy_discussion_001`, `fin_internal_training_guide_001`,
`fin_compliance_report_001`, `fin_internal_branch_financials_001`. Điểm tối đa của chúng chỉ 0.46-0.60. `weight` thấp
là **có chủ ý** (tín hiệu INTERNAL yếu nhưng thật); ngưỡng cao sẽ làm file của chúng rơi từ INTERNAL xuống PUBLIC.

### Đánh giá ngưỡng — thực nghiệm trên bộ văn bản mẫu

Bộ 21 văn bản tiếng Việt tự viết, gán nhãn sẵn: 6 PUBLIC (thông cáo báo chí, hướng dẫn router, quảng cáo thẻ, cảnh báo
lừa đảo OTP, tin tuyển dụng, bài báo kinh tế), 7 INTERNAL (thông báo quy trình, đào tạo, biên bản họp rủi ro, báo cáo
tuân thủ, email số liệu chi nhánh, quy trình vay, ghi chú họp ít từ khoá), 4 CONFIDENTIAL (hồ sơ nhân viên, sao kê,
thư mời nhận việc, hợp đồng + CCCD), 4 SECRET (credentials + IP, bảng 60 CCCD, thẻ + CVV + OTP, CCCD + mật khẩu).
Chạy toàn pipeline (quét → phân loại), mô phỏng ngưỡng bằng cách lọc chỗ khớp theo `Confidence` trước khi phân loại.

| Ngưỡng | Đúng | Báo nhầm (FP) | Bỏ sót (FN) | Văn bản đổi mức |
|---|---|---|---|---|
| 0 (hiện tại) | 19/21 | 2 | 0 | — |
| 0.30 – 0.35 | 19 | 2 | 0 | — |
| **0.40** | **20** | **1** | **0** | P6 bài báo kinh tế INTERNAL → PUBLIC (đúng) |
| 0.45 | 20 | 1 | 0 | như 0.40 |
| 0.50 | 19 | 1 | 1 | + ghi chú họp rủi ro INTERNAL → PUBLIC (sai) |
| 0.55 | 18 | 1 | 2 | + báo cáo tuân thủ bị mất (sai) |
| 0.60 | 15 | 1 | 5 | mất gần hết tài liệu nội bộ |

**Kết luận: ngưỡng mặc định 0.40.** Là mức thấp nhất sửa được báo nhầm P6 ("Ngân hàng Nhà nước (NHNN)" trong bài báo,
điểm 0.37) mà không gây bỏ sót. Chọn 0.40 thay vì 0.45 để chừa khoảng an toàn (báo cáo tuân thủ có chỗ khớp điểm
0.42-0.45, rất sát ngưỡng).

**Giới hạn của đánh giá:** bộ mẫu nhỏ và tự viết, có thể chưa đại diện tài liệu thật; mô phỏng lọc SAU khi quét, còn
code thật sẽ loại TRƯỚC bước gate/đếm nên kết quả có thể lệch nhẹ — cần chạy lại cùng bộ mẫu sau khi áp dụng.

### Change Brief đã đề xuất (chờ người dùng đồng ý)

- Thêm trường `"min_confidence"` cấp rule trong JSON + hằng số `DefaultMinConfidence = 0.40` trong `RegexScanner`.
- Chỗ khớp có điểm < ngưỡng bị **loại hẳn**, đặt ở vòng 1 ngay sau placeholder, **trước bước đếm** (không góp vào chế độ
  bảng/volume).
- Pattern không có `confidence` không bao giờ bị loại. Chưa khai `min_confidence` riêng cho rule nào.
- Tác động vào: `RuleDefinition.cs`, `SensitivePattern.cs`, `RuleLoader.cs`, `RegexScanner.cs`.

---

## 6. Bảng data flow — file đã thay đổi trong buổi 5

| File | Thay đổi | Lý do |
|---|---|---|
| `Watching/FileWatcherService.cs` | Thêm field `_watcher`, `_watchFolder`; `InternalBufferSize = 64KB`; đăng ký `Error` + hàm `OnWatcherError` (có `try/catch` quanh quét bù) | D3: không mất sự kiện âm thầm khi buffer tràn, không sập service khi quét bù lỗi |
| `Watching/FileWatcherService.cs` | 2 dict → `ConcurrentDictionary`; `ProcessAfterDebounceAsync` nhận `CancellationTokenSource`; `finally` dùng `TryRemove(KeyValuePair)` | An toàn khi nhiều luồng cùng đọc/ghi; bịt hẳn lỗi xoá nhầm CTS |
| `Scanning/EscalationDefinition.cs`, `EscalationRule.cs` (mới); `RuleDefinition.cs`, `SensitivePattern.cs`, `RuleLoader.cs`, `RegexScanner.cs` | Đọc và áp dụng trường `escalation` (xem mục 5b) | Bậc 3 #1 |
| `rules/org/contract.json`, `rules/org/internal-ip.json` | Sửa ghi chú "dead field" | Phản ánh đúng trạng thái engine |
| `Scanning/LevelGateDefinition.cs`, `LevelGateRule.cs`, `SensitivePattern.cs`, `RuleLoader.cs`, `RegexScanner.cs` | Đọc và áp dụng `min_confidence_bypass` với độ tin cậy động (xem mục 5c) | Bậc 3 #3 |
| `rules/org/watermark.json`, `rules/org/internal-ip.json` | Sửa ghi chú "dead field" về `min_confidence_bypass` | Phản ánh đúng trạng thái engine |
| `Scanning/ProximityWindowDefinition.cs`, `ProximityRule.cs` (mới); `RuleDefinition.cs`, `PatternDefinition.cs`, `SensitivePattern.cs`, `RuleLoader.cs`, `RegexScanner.cs` | Đọc và áp dụng `proximity_window` / `require_keyword_within` + chế độ bảng (xem mục 5d) | Bậc 3 #2 |
| `rules/pii/date-of-birth.json`, `rules/pii/location-tracking.json` | Sửa ghi chú "dead field" về `require_keyword_within` | Phản ánh đúng trạng thái engine |
| `Scanning/VolumeEscalationDefinition.cs`, `VolumeEscalationRule.cs` (mới); `RuleDefinition.cs`, `SensitivePattern.cs`, `RuleLoader.cs`, `RegexScanner.cs` | Đọc và áp dụng `volume_escalation` (xem mục 5e) | Bậc 3 #4 |
| `Scanning/PlaceholderExclusionDefinition.cs`, `PlaceholderRule.cs` (mới); `RuleDefinition.cs`, `SensitivePattern.cs`, `RuleLoader.cs`, `RegexScanner.cs` | Đọc và áp dụng `placeholder_exclusion` (xem mục 5f) | Bậc 3 #5 |
| `rules/pii/email.json`, `rules/org/credentials.json` | Sửa ghi chú "dead field" về `placeholder_exclusion` | Phản ánh đúng trạng thái engine |
| `Scanning/ScanMatch.cs`, `RegexScanner.cs`, `Watching/FileWatcherService.cs` | `ScanMatch.Confidence`; tính điểm 1 lần/chỗ khớp, giới hạn 1.0; log điểm (xem mục 5g) | Bậc 3 #7a |
| `Reading/IContentExtractor.cs`, `Reading/Extractors/*` (8 file mới); `Reading/FileReaderService.cs` (viết lại); `Watching/FileWatcherService.cs`; `DlpService.csproj` (+3 package) | Đọc 7 định dạng file (xem mục 8) | Mở rộng định dạng |

Buổi này thêm 8 file (`EscalationDefinition.cs`, `EscalationRule.cs`, `ProximityWindowDefinition.cs`,
`ProximityRule.cs`, `VolumeEscalationDefinition.cs`, `VolumeEscalationRule.cs`, `PlaceholderExclusionDefinition.cs`,
`PlaceholderRule.cs`), đã ghi vào `docs/muc-luc-code.md`. Mục lục
cũng được viết lại toàn bộ, vì trước đó nó thiếu nhiều file
thêm từ các buổi trước (`Logging/`, `Classification/Modules/` ngoài Finance, các class `*Definition`/`*Rule` trong
`Scanning/`, `scripts/`). Build: `dotnet build` thành công, 0 warning, 0 error.

---

## 7. Việc chưa làm và cải tiến để làm sau

### 7.1 Việc kế tiếp ngay (đã phân tích xong, chỉ chờ đồng ý)

| # | Việc | Ghi chú |
|---|---|---|
| **1** | **Áp dụng `min_confidence` = 0.40** | Change Brief ở cuối mục 5g. Sau khi code: chạy lại bộ 21 văn bản mẫu để xác nhận kết quả khớp mô phỏng |
| **2** | **Điều tra P5 "tin tuyển dụng" bị CONFIDENTIAL** | Văn bản chỉ có "sinh năm 1995" (INTERNAL) + email tuyển dụng (PUBLIC) mà tổng hợp lên CONFIDENTIAL. Ngưỡng không sửa được (ở 0.55 vẫn còn INTERNAL). Nghi do 1 compound rule hoặc `HrModule` nâng mức — cần xem `Reasons` của từng module |
| **3** | **Publish lại + test end-to-end trên service thật** | Service dưới SCM vẫn là bản cũ. Cần: dừng service → `dotnet publish` → start → thả vài file mẫu (credentials, bảng CCCD, tài liệu hướng dẫn...) vào `watch-folder` → đọc log (giờ có thêm điểm tin cậy) |

### 7.2 Bậc 3 còn lại

| # | Việc | Phụ thuộc | Ghi chú |
|---|---|---|---|
| #6 | `doc_context_discount_keywords/factor` (5 file: credentials, cvv, otp ×3) | Cần `min_confidence` (7.1 #1) | Nhân điểm với hệ số (×0.4/×0.5) khi gần đó có "hướng dẫn/ví dụ/template...". Có ngưỡng rồi thì điểm bị giảm mới có tác dụng (bị loại khi xuống dưới ngưỡng) |
| #7b | Chỉnh `min_confidence` riêng từng rule | Cần log điểm thật từ service (7.1 #3) | Đặc biệt 6 rule tài chính nội bộ weight thấp — xem lại sau khi có dữ liệu |
| #7c | Hiển thị điểm trong `Reasons` của module | — | Người dùng chọn chưa làm. Cân nhắc khi 7b ổn định |
| #8 | Tiền lọc Aho-Corasick | Cần số đo hiệu năng | Chỉ làm khi đo được quét đang chậm (tránh tối ưu sớm) |

### 7.3 Lỗi/nghi vấn phát hiện trong buổi (chưa sửa)

| Vấn đề | Chi tiết | Hướng xử lý |
|---|---|---|
| Rule trùng lặp `CreditCard` | Rule dạng cũ (không có `confidence`) vẫn chạy song song với `credit_card_001` — bộ mẫu S3 ra cả 2 | Kiểm tra file rule dạng cũ còn sót từ các buổi đầu, cân nhắc tắt (`"enabled": false`) |
| 2 file trùng `id` `tax_code_personal_001` | `tax-code-personal-bare.json` + `tax-code-personal-labeled.json` | Hiện chưa gây lỗi (đếm theo tên rule), nhưng nên đặt `id` riêng |
| Nhiều rule khác trùng `id` | `otp_auth_001` dùng cho 3 file otp; `credit_card_001` cho 7 file thẻ | Có chủ ý (cùng họ rule) — ghi nhận, lưu ý nếu sau này đếm theo `RuleId` |
| Placeholder chỉ áp cho rule khai nó | Số thẻ test `4111…` vẫn bị `fin_credit_card_statement_001` bắt; `noreply@vnpt.vn` vẫn bị `vnpt_email_001` bắt | Thêm `placeholder_exclusion` vào JSON 2 rule đó nếu muốn (không cần sửa code) |
| Email khớp trùng | `nguyenvana@vnpt.vn` khớp cả `email_001` pattern_1 lẫn pattern_3 → 2 `ScanMatch` cho 1 giá trị | Volume đã đếm giá trị khác nhau nên không sai số lượng; chỉ làm log dài hơn |
| Ghi chú "dead field" lỗi thời | Nhiều file (`health`, `biometric`, `passport`, `phone-vn`, `classified-doc-*`, `insurance-context`, `fraud-transaction-flag`...) vẫn ghi `override_level`/`required_corroboration_tags`/`strict_tags_only` là dead field, dù đã làm ở Bậc 1-2 | Dọn 1 lượt |
| CCCD pattern_1 gần như luôn bypass gate | 0.765 + 1 từ khoá primary đã ≥ 0.90 (mục 5c) | Đang chấp nhận; nếu quá lỏng thì giảm `PrimaryBoost` |
| Chế độ bảng: bảng < 5 dòng bị bỏ sót; ≥ 5 dãy số bất kỳ + 1 từ khoá bị tính nhầm | Mục 5d | Chỉnh `TableModeMinMatches` khi có dữ liệu thật |
| Volume Q2 chưa test được | Cả 5 rule có `volume_escalation` đều không có `negation_filter` | Chờ rule có cả 2 trường |

### 7.4 Watcher / vận hành (từ phần đầu buổi, vẫn còn)

| # | Việc | Ghi chú |
|---|---|---|
| **Test D3** | Chưa chạy test thực tế | Tạm đặt `InternalBufferSize = 4096`, chạy service, dùng PowerShell tạo nhanh ~2000 file: `1..2000 \| ForEach-Object { Set-Content "...\watch-folder\t$_.txt" "hello $_" }`. Mong đợi: log `Buffer tràn... quét lại toàn thư mục` rồi kết quả quét. Test xong trả về `64 * 1024` |
| D3-mở rộng | Tự khởi động lại watcher khi gặp lỗi khác (không phải tràn buffer) | Đã có field `_watcher` làm nền. Cần: `Dispose` watcher cũ, tạo lại, có giới hạn số lần thử/khoảng chờ để tránh vòng lặp khi thư mục mất hẳn |
| D-xoá | Xử lý sự kiện `Deleted` | Dọn `_lastContentHash` khi file bị xoá, tránh dict phình to (đã nhắc từ buổi 4) |
| try/catch từng file | Bọc `try/catch` quanh từng `ProcessFile` trong `ScanExistingFiles` | 1 file lỗi không làm dừng cả lượt quét (khởi động lẫn quét bù) |
| Race (b) | Tranh nhau cùng 1 file | Đang chấp nhận. Chỉ xem lại nếu log trùng gây phiền thực tế |
| CTS không `Dispose` | CTS không dùng `CancelAfter` gần như không tốn tài nguyên | Chấp nhận. `Dispose` vội dễ gây `ObjectDisposedException` khi `ScheduleProcess` gọi `Cancel()` lên CTS đã huỷ |
| Nhóm E | Tài khoản chạy service, lưu kết quả bền vững | Xem `docs/buoi-04-tom-tat.md`, mục 6 |

### 7.5 Cải tiến kỹ thuật nên làm sau

| Việc | Vì sao |
|---|---|
| Đưa bộ 21 văn bản mẫu vào repo thành **bộ test hồi quy** (VD `tests/samples/*.txt` + file nhãn kỳ vọng) | Hiện mỗi lần test phải dựng console scratch rồi xoá. Có bộ mẫu cố định thì mỗi thay đổi engine chạy lại được ngay, so được trước/sau |
| Tách `RegexScanner.Scan()` thành các bước nhỏ | Hàm đã rất dài (vòng 1 + chế độ bảng + volume + chuẩn bị gate + vòng gộp). Tách giúp dễ đọc và dễ test từng bước |
| `ScanMatch` mang lý do nâng/hạ mức (escalation, volume, bypass, gate) | Hiện log không cho biết 1 chỗ khớp lên SECRET vì volume hay vì mức gốc — khó chẩn đoán khi phân loại sai |
| Gom các hằng số tinh chỉnh (`PrimaryBoost`, `SecondaryBoost`, `MaxBoost`, `TableModeMinMatches`, `DefaultMinConfidence`) vào `appsettings.json` | Chỉnh được mà không cần build lại |

---

## 8. Mở rộng định dạng file: `.txt .csv .docx .pptx .xlsx .xls .pdf`

### Quyết định (người dùng chọn theo đề xuất)

| # | Quyết định |
|---|---|
| Q1 Thư viện | **DocumentFormat.OpenXml 3.5.1** (Microsoft, MIT) cho docx/pptx; **ExcelDataReader 3.9.0** (MIT) cho xlsx/xls; **PdfPig 0.1.11** (Apache-2.0) cho pdf. Tránh iText7 (giấy phép AGPL buộc công khai mã nguồn) |
| Q2 Phần "ẩn" | Có trích: ghi chú người thuyết trình (pptx), header/footer/chú thích/comment/chữ đã xoá khi Track Changes (docx), sheet ẩn (xlsx), slide ẩn (pptx) |
| Q3 Giới hạn | txt/csv ≤ 10MB; docx/pptx/xlsx/xls/pdf ≤ 50MB; text trích ra ≤ 2 triệu ký tự — vượt thì **cắt và vẫn quét phần đầu** + cảnh báo |
| Q4 Thứ tự | 4 bước, mỗi bước build + test xong mới sang bước sau |

### Kiến trúc

```
Reading/
├── IContentExtractor.cs     "hợp đồng": Extensions + MaxFileSizeBytes + Extract(path) → text
├── FileReaderService.cs     bộ điều phối (tự nạp extractor qua reflection, như ClassificationEngine)
└── Extractors/              mỗi định dạng 1 file — thêm định dạng mới KHÔNG phải sửa core/watcher
```

- **Mọi định dạng → text thuần**, nên `RegexScanner` và các module phân loại **không phải sửa gì**.
- **Bảng** (Excel, bảng Word/PowerPoint): mỗi hàng 1 dòng, ô nối ` | ` → chế độ bảng (#2) và volume (#4) vẫn hoạt động.
- Mốc vị trí trong text: `=== Sheet: tên (ẩn) ===`, `--- Slide N ---`, `--- Trang N ---`, `[Header]`, `[Comment]`, `[Ghi chú]`.
- `TryReadTextFile` đổi tên thành `TryReadFile`; `FileReadResult` thêm `Warning` (bị cắt, PDF không có text).

### Các bước

| Bước | Nội dung | File |
|---|---|---|
| 1 | Tái cấu trúc + `.txt`/`.csv`; watcher dùng `Filters` theo `SupportedExtensions`, bỏ qua file khoá Office `~$…`; `ScanExistingFiles` lọc bằng `IsSupported` (không dùng mẫu `*.xls` vì Windows khớp nhầm cả `.xlsx`) | `IContentExtractor.cs`, `FileReaderService.cs` (viết lại), `Extractors/TextFileReader.cs`, `TxtExtractor.cs`, `CsvExtractor.cs`; `Watching/FileWatcherService.cs` |
| 2 | `.docx` + `.pptx` | `Extractors/ExtractorHelpers.cs`, `DocxExtractor.cs`, `PptxExtractor.cs`; `DlpService.csproj` |
| 3 | `.xlsx` + `.xls` | `Extractors/ExcelExtractor.cs`; `DlpService.csproj` |
| 4 | `.pdf` | `Extractors/PdfExtractor.cs`; `DlpService.csproj` |

### Vấn đề phát hiện và xử lý trong lúc làm

| Vấn đề | Xử lý |
|---|---|
| **Unicode NFD/NFC:** file lưu bảng mã Windows-1258 (và file tạo trên macOS) lưu "ọ" thành "o" + dấu rời. Nhìn giống nhưng so chuỗi khác → mọi từ khoá tiếng Việt có dấu trong rule sẽ không khớp | `FileReaderService` chuẩn hoá `Normalize(FormC)` cho MỌI định dạng |
| **CSV/TXT lưu ANSI** (Excel cũ) đọc bằng UTF-8 ra ký tự lỗi `�` | `TextFileReader`: thấy ký tự lỗi thì đọc lại bằng Windows-1258; đăng ký `CodePagesEncodingProvider` |
| **Excel mất số 0 đầu:** CCCD lưu dạng số + định dạng `000000000000`, giá trị thô chỉ còn 10 chữ số → regex CCCD 12 số bỏ sót | Áp lại định dạng toàn số 0 của ô |
| **Số lớn ra dạng khoa học** (`4.5E+15`) → mất số thẻ | Số nguyên in đủ chữ số |
| **`InnerText` của Word làm dính chữ** ở chỗ tab/xuống dòng | Tự ghép: tab → `\t`, ngắt dòng → `\n` |
| Thư viện ném đủ loại exception (file hỏng, mật khẩu, đổi đuôi) | Điều phối bắt hết; chỉ log TÊN loại lỗi, không log `ex.Message` (có thể chứa trích đoạn nội dung) |

### Kết quả test (console scratch, file tạo bằng code, đã xoá sau khi test)

| File | Nội dung test | Kết quả |
|---|---|---|
| `.txt` UTF-8 | CCCD + mật khẩu | SECRET |
| `.csv` UTF-8 / ANSI 1258 | Bảng 60 CCCD, nhãn chỉ ở dòng tiêu đề | SECRET ×60 (cả 2); ANSI đọc đúng dấu sau chuẩn hoá |
| `.docx` | Tab trong đoạn, bảng 60 CCCD, header, comment chứa `password=` | SECRET; bảng mỗi hàng 1 dòng; comment bị bắt |
| `.pptx` | Bảng trong slide có số TK, ghi chú người thuyết trình có IP + ssh | CONFIDENTIAL; ghi chú được đọc |
| `.xlsx` | 60 CCCD lưu dạng số có số 0 đầu, cột ngày sinh, sheet ẩn có `db_pass=` | SECRET; số 0 đầu được khôi phục; sheet ẩn bị bắt |
| `.xls` (97-2003) | Số thẻ 16 chữ số lưu dạng số + CVV | SECRET; số thẻ in đủ |
| `.pdf` có text | Hồ sơ tiếng Việt: CCCD + mật khẩu | SECRET; tiếng Việt đúng |
| `.pdf` scan (chỉ hình) | Không có chữ | Cảnh báo "có thể là PDF/ảnh scan", mức PUBLIC |
| `hong.docx` | Đuôi docx nhưng là text | Lỗi an toàn: "Không trích xuất được nội dung (FileFormatException)…" |
| `~$baocao.docx`, `anh.png` | File khoá Office, đuôi không hỗ trợ | Bị bỏ qua |

### Việc chưa làm / hạn chế (bổ sung vào mục 7)

| Việc | Ghi chú |
|---|---|
| **OCR cho PDF scan / ảnh** | Hiện chỉ cảnh báo. Cần thư viện OCR (VD Tesseract) — nặng, cân nhắc riêng |
| **Định dạng Office cũ `.doc`, `.ppt`** | Chưa hỗ trợ (chỉ `.xls` cũ được, nhờ ExcelDataReader). Cần thư viện khác (VD NPOI — lưu ý giấy phép) |
| **Cột ngày sinh trong Excel chỉ bắt vài dòng đầu** | Rule ngày sinh dùng `require_keyword_within` (dạng B của #2) — có chủ ý KHÔNG có chế độ bảng. Test: 60 ngày sinh, chỉ bắt 3. Cân nhắc cho dạng B có chế độ bảng khi nguồn là bảng |
| **Chế độ bảng báo nhầm đã xảy ra thật** | docx test: 60 CCCD bị tính thêm là 60 số tài khoản vì comment có chữ "Tài khoản hệ thống" → `bank_account_001` SECRET ×60. Đúng hạn chế ghi ở mục 5d; cần xem lại điều kiện "từ khoá ở bất kỳ đâu" (VD chỉ xét từ khoá nằm ở dòng tiêu đề của bảng) |
| **File có mật khẩu** | Chỉ báo lỗi chung "file hỏng, có mật khẩu hoặc sai định dạng". Với DLP, file mã hoá có thể đáng cảnh báo riêng |
| **Publish lại service** | Service đang chạy dưới SCM vẫn là bản cũ (chỉ đọc `.txt`). Publish sẽ kèm thêm các DLL thư viện mới |
| Hiệu năng file lớn | Chưa đo thời gian đọc xlsx/pdf 50MB; `ProcessFile` chạy đồng bộ trên luồng debounce |
