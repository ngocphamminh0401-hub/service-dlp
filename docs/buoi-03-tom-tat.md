# Buổi 3 — Bậc 1 & Bậc 2: Level Gate đầy đủ, Negation Filter đúng thiết kế, Compound Rules

Cải thiện engine theo checklist rút ra từ đối chiếu thiết kế engine Go tham khảo (`aho_corasick.go`,
`rules.go`, `regex.go`, `classifier.go`, `rules.yaml`) với engine C# hiện tại. Không sửa nội dung 63 rule
JSON đã có (financial/hr/org/pii) — chỉ mở rộng phần engine đọc được các trường đã khai báo sẵn trong đó,
và thêm `rules/compound.json` hoàn toàn mới.

---

## 1. Bậc 1 — Override level theo pattern con, Negation Filter đúng action, Level Gate theo phạm vi pattern

### 1.1 `override_level` theo từng pattern con

**Thay đổi gì:** Trước đây 1 rule "dạng mới" (nhiều `patterns[]`) dùng CHUNG 1 mức `rule.level` cho mọi
pattern con. Giờ mỗi pattern con có thể tự khai `override_level` riêng, đè lên mức chung của rule.

**Lý do:** Nhiều rule PDF gốc mô tả rõ 1 rule có nhiều "tầng" nghiêm trọng khác nhau tuỳ pattern khớp — ví
dụ `pii/health.json`: pattern bắt "HIV/tâm thần/di truyền" (HEALTH_RESTRICTED) phải SECRET, nhưng pattern
bắt "ung thư/tiểu đường/tim mạch" (HEALTH_GENERAL) chỉ nên CONFIDENTIAL. Không có `override_level`, toàn
bộ 13 pattern của `health.json` bị gán chung SECRET — sai lệch so với thiết kế, dễ over-classify các file
chỉ nhắc bệnh thông thường.

**Tác động vào đâu:**
- `Scanning/PatternDefinition.cs` — thêm `OverrideLevel` để đọc trường JSON `override_level`.
- `Scanning/SensitivePattern.cs` — thêm `OverrideLevel` (dạng sẵn sàng) + `PatternIdx` (vị trí 0-indexed
  thật trong `patterns[]`, cần để tra đúng `override_level`/`applies_to_patterns` dù pattern trước đó có bị
  bỏ qua do lỗi regex).
- `Scanning/RuleLoader.cs` — vòng lặp `LoadMultiPatternRule` đổi từ `foreach` sang `for (patIdx...)` để giữ
  đúng chỉ số gốc; gán `OverrideLevel = patternDef.OverrideLevel`.
- `Scanning/RegexScanner.cs` — `baseSeverity = pattern.OverrideLevel ?? pattern.Severity` thay vì luôn dùng
  `pattern.Severity` (mức chung của rule).

**Vì sao quyết định làm ở đây:** Đây là nơi duy nhất biết "chỗ khớp này đến từ pattern con nào" — đẩy logic
lên `Classification` sẽ mất thông tin đó (module domain chỉ thấy `ScanMatch.Severity` đã tính xong).

### 1.2 `negation_filter.action` — phân biệt `suppress` và `downgrade_to_gate`

**Thay đổi gì:** Trước đây bất kể `action` khai gì, gặp từ phủ định gần đó là loại HẲN chỗ khớp
(`suppress`). Giờ `action=downgrade_to_gate` giữ lại chỗ khớp nhưng hạ thẳng xuống `level_gate.fallback_level`
(không loại khỏi kết quả, không chờ xét corroboration).

**Lý do:** Đây là **bug thật**, không phải tính năng mới — nhiều rule JSON (`otp-table-header.json`,
`vip-customer.json`,...) đã khai `"action": "downgrade_to_gate"` từ trước nhưng engine cũ bỏ qua trường này
và luôn xử lý như suppress. Hệ quả: những chỗ khớp có ngữ cảnh mơ hồ (ví dụ "Số thẻ/CVV/OTP" xuất hiện
trong bảng nhưng gần đó có từ phủ định) bị mất trắng khỏi kết quả thay vì được hạ mức và vẫn hiển thị cho
người xem xét.

**Tác động vào đâu:**
- `Scanning/NegationRule.cs` — thêm `required string Action`, hàm `AppliesTo(patIdx)`.
- `Scanning/RuleLoader.cs` — chuẩn hoá `Action` (không hợp lệ/rỗng → mặc định `suppress`, an toàn nhất); thêm
  ràng buộc nạp: `downgrade_to_gate` bắt buộc rule phải có `level_gate.require_corroboration=true`, thiếu
  thì bỏ qua cả rule (tránh hạ về đâu không xác định).
- `Scanning/RegexScanner.cs` — tách `negationDowngrade` (cờ) khỏi việc loại bỏ; nhánh `suppress` vẫn
  `continue`, nhánh `downgrade_to_gate` giữ lại và đánh dấu để vòng 2 hạ Severity.

**Vì sao quyết định làm ở đây:** Negation filter vốn đã nằm trong vòng quét của `RegexScanner` — sửa đúng
tại nguồn thay vì để `Classification` "đoán lại" ý định qua Severity đã sai.

### 1.3 `level_gate` / `negation_filter` theo phạm vi pattern con (`applies_to_patterns`/`exempt_patterns`)

**Thay đổi gì:** Gate/negation filter có thể chỉ áp dụng cho MỘT SỐ pattern con cụ thể trong rule (ví dụ
`pii/cccd.json`: gate áp cho pattern CCCD/CMND số thường nhưng KHÔNG áp cho pattern MRZ — số trong dải MRZ
đã tự đủ tin cậy).

**Lý do:** Không phải mọi pattern con trong 1 rule có độ tin cậy như nhau — pattern có định dạng đặc trưng
cao (MRZ, mã ICD kèm keyword,...) không cần "chờ củng cố" giống pattern chung chung (12 chữ số trần).

**Tác động vào đâu:** `Scanning/LevelGateDefinition.cs`/`LevelGateRule.cs` thêm `AppliesToPatterns`/
`ExemptPatterns` + hàm `AppliesToPattern(patIdx)` (ưu tiên: rỗng = áp cho mọi pattern; có
`AppliesToPatterns` thì chỉ áp cho đúng tập đó; `ExemptPatterns` luôn loại trừ thêm). `RegexScanner.cs` gọi
hàm này trước khi áp gate cho từng chỗ khớp.

---

## 2. Bậc 2 — Level Gate đủ 4 điều kiện, Compound Rules

### 2.1 Level Gate đầy đủ 4 điều kiện củng cố

**Thay đổi gì:** Bậc 1 chỉ có 1 điều kiện củng cố ("khớp ≥ 2 lần"). Bậc 2 thêm 3 điều kiện còn lại theo
đúng thiết kế PDF: (2) chỗ khớp đã qua validator thuật toán (Luhn,...), (3) có rule KHÁC trong cùng nội dung
đạt ≥ CONFIDENTIAL, (4) `required_corroboration_tags` — cần ít nhất 1 rule khác mang 1 trong các tag chỉ
định. `strict_tags_only=true` thì LOẠI HẲN 3 điều kiện mặc định (1)(2)(3), chỉ còn (4).

**Lý do:** Điều kiện "≥2 lần khớp" quá lỏng cho nhiều rule (dễ bị 2 lần trùng lặp ngẫu nhiên) và quá chặt
cho rule khác (1 lần khớp có bằng chứng mạnh từ nơi khác vẫn bị hạ oan). Ví dụ cụ thể đã verify thực nghiệm:
`pii/health.json` cần CHÍNH XÁC "có mặt 1 rule khác mang tag `personal_identifier`" (CCCD/tên/ngày sinh) mới
giữ mức thật — nếu chỉ dựa "≥2 lần khớp bệnh án", 184/204 file test thật (theo ghi chú trong rule) vẫn bị
false positive vì bệnh án/phiếu khám xuất hiện lặp trong văn bản công khai (bảng giá dịch vụ y tế, lịch tiêm
chủng...) không gắn với bệnh nhân cụ thể nào.

**Tác động vào đâu:**
- `Scanning/LevelGateDefinition.cs`/`LevelGateRule.cs` — thêm `RequiredCorroborationTags`, `StrictTagsOnly`.
- `Scanning/SensitivePattern.cs`, `Scanning/ScanMatch.cs` — thêm `Category`/`RuleId`/`Tags`/`Validated` để
  mang thông tin "rule nào tạo ra chỗ khớp này, có tag gì, đã qua validator chưa" xuyên suốt vòng quét.
- `Scanning/RuleLoader.cs` — ràng buộc nạp: `strict_tags_only=true` bắt buộc `required_corroboration_tags`
  không rỗng (nếu không, cấu hình vô nghĩa — không biết "chỉ dựa vào tag nào"), thiếu thì bỏ cả rule; gán
  `Category/RuleId/Tags` vào `SensitivePattern`.
- `Scanning/RegexScanner.cs` — thêm 1 vòng chuẩn bị TOÀN CỤC (chạy trước vòng gộp theo rule) tính
  `strongRuleNames` (rule nào có ≥1 chỗ khớp đạt CONFIDENTIAL+, đã tính cả negation) và `tagCounts` (đếm
  mọi tag trên toàn bộ chỗ khớp) — 2 giá trị này cần nhìn XUYÊN mọi rule trong cùng nội dung, không thể tính
  trong vòng gộp per-rule như "≥2 lần"/"đã qua validator" (chỉ cần thông tin của chính rule đó).
- `Classification/SensitivityLevel.cs` — thêm `SensitivityLevelExtensions.ParseSensitivityLevel()` dùng
  chung cho 4 module domain (trước đó mỗi module tự có 1 bản `ParseSeverity` trùng lặp).

**Vì sao quyết định làm ở đây (và không đặt `SeverityRank` dùng chung với Classification):** `RegexScanner`
cần so sánh mức nghiêm trọng ("có rule khác đạt ≥ CONFIDENTIAL không") NGAY TRONG tầng `Scanning`, nhưng
`Scanning` không được phép biết về `Classification.SensitivityLevel` (chiều phụ thuộc chỉ được đi 1 hướng:
`Classification` → `Scanning`, không ngược lại — đúng nguyên tắc "Core không được biết module cụ thể" trong
CLAUDE.md). Giải pháp: viết 1 hàm `SeverityRank()` nhỏ, tự chứa, riêng trong `RegexScanner.cs`, trùng thứ tự
nhưng KHÔNG dùng chung code với `SensitivityLevelExtensions` bên `Classification`.

### 2.2 Compound Rules (kết hợp nhiều category/rule qua tag)

**Thay đổi gì:** Thêm khả năng "1 rule ảo" kích hoạt khi TẬP HỢP nhiều rule/category cùng xuất hiện trong 1
file — ví dụ Hợp đồng (`org_contract`) + 1 trong 5 rule PII "mạnh" (`pii_strong`) = CONFIDENTIAL, dù từng
thứ riêng lẻ có thể chỉ INTERNAL. Đưa đúng 17 compound rule từ `rules.yaml` gốc vào `rules/compound.json`.

**Lý do:** Nhiều tình huống rò rỉ dữ liệu chỉ thực sự nghiêm trọng khi CÓ NHIỀU LOẠI dữ liệu cùng xuất hiện
(ví dụ credentials + IP nội bộ = đủ để xâm nhập hệ thống — SECRET; từng cái riêng chỉ SECRET/INTERNAL không
liên quan nhau). Không có compound rule, engine chỉ lấy `Max()` mức của từng rule đơn lẻ, bỏ lỡ tín hiệu
"kết hợp".

**Tác động vào đâu (thiết kế THUẦN CỘNG THÊM, không sửa gì đã có):**
- `Classification/CompoundRuleDefinition.cs` (mới) — khuôn JSON thô: `conditions`, `min_component_level`,
  `context_conditions`, `exclude_context_patterns`, `result_level`, `violation_type`, `alert_priority`.
- `Classification/CompoundRule.cs` (mới) — dạng "sẵn sàng" (regex loại trừ đã biên dịch).
- `Classification/Modules/CompoundRuleModule.cs` (mới) — module thứ 5, tự nạp `rules/compound.json`; với
  mỗi `ScanMatch` dựng 5 nguồn tag: `category` ("pii"), `ruleId` đầy đủ ("vn_id_001"), `prefix` (ruleId bỏ
  hậu tố "_NNN" → "vn_id"), `category_prefix` ("pii_vn_id"), và tag tuỳ chỉnh trong allowlist CHỈ GỒM
  `pii_strong`/`financial_personal` (không dùng mọi `tags[]` khai trong rule — nếu dùng hết, tag "pii" dùng
  chung cho toàn bộ 11 rule PII kể cả rule yếu như tên người/ngày sinh sẽ khiến compound rule kích hoạt sai,
  ví dụ 1 hợp đồng mẫu chỉ có 1 tên minh hoạ cũng bị tính là "Contract + PII" dù không có PII thật).
- `Scanning/RuleLoader.cs` — thêm 4 dòng bỏ qua `compound.json` khi quét `rules/` (file này là MẢNG nhiều
  compound rule, không đúng khuôn 1-rule-1-file mà `RuleLoader` xử lý — nếu không bỏ qua sẽ bị log nhầm
  thành "JSON sai cú pháp" mỗi lần khởi động).
- `rules/compound.json` (mới) — 17 compound rule.

**Vì sao quyết định làm ở đây (đặt ở `Classification`, không phải `Scanning`):** Compound rule không quét
regex trực tiếp lên nội dung file — nó chỉ xét TẬP TAG dựng từ các `ScanMatch` đã có sẵn, đúng bản chất công
việc "phân loại", không phải "quét". Thiết kế là module thứ 5 độc lập (không sửa 4 module cũ, không sửa
`ClassificationEngine.cs` — engine tự tìm class này qua reflection) để giảm rủi ro breaking change: nếu
`CompoundRuleModule` lỗi, `ClassificationEngine.Classify()` đã có sẵn try/catch riêng cho từng module, không
ảnh hưởng 4 module còn lại.

**Nguyên tắc "không hạ mức" (no-downgrade):** Không cần code riêng — `ClassificationEngine.Classify()` vốn
đã lấy `Max()` mức giữa mọi module (`results.Max(r => r.Level)`), nên nếu `CompoundRuleModule` trả về mức
THẤP HƠN 1 module domain khác, kết quả tổng hợp vẫn giữ mức cao hơn đó. Đã verify thực nghiệm (mục 3).

**Phát hiện quan trọng khi làm:** `id`/`category`/`tags` của 63 rule JSON viết ở các buổi trước (PII/
Financial/Org) đã sẵn khớp CHÍNH XÁC với tên tag mà `compound_rules` gốc tham chiếu (`org_contract`,
`pii_vn_id`, `pii_strong` đúng 5 rule, `financial_personal` đúng 8 rule...) — không cần sửa file rule nào.

---

## 3. Kết quả kiểm thử (console app scratch, 63 rule + `compound.json`, đã xoá sau khi test)

| Kịch bản | Nội dung | Kỳ vọng | Kết quả |
|---|---|---|---|
| A | Chỉ "Bệnh án..." không có PII nào khác | `strict_tags_only` chặn → PUBLIC | ✅ PUBLIC |
| B | "Bệnh án..." + CCCD trong cùng đoạn | tag `personal_identifier` có mặt → CONFIDENTIAL | ✅ CONFIDENTIAL |
| C | Hợp đồng + CCCD | Compound "Contract + PII" = CONFIDENTIAL | ✅ đúng, không tự nhảy SECRET |
| D | `password:` + IP `192.168.x.x` | Compound "Credentials + Internal IP" = SECRET | ✅ SECRET |
| E | Hợp đồng + số thẻ tín dụng (Luhn hợp lệ) | Compound chỉ tính CONFIDENTIAL nhưng Finance module đã SECRET → tổng hợp KHÔNG bị kéo xuống | ✅ SECRET (no-downgrade xác nhận) |

`dotnet build`: 0 Warning / 0 Error sau mỗi lần sửa.

---

## 4. Bậc 3 — Chưa giải quyết & hướng cải thiện tiếp theo

> **Cập nhật buổi 5:** #1, #2, #3, #4, #5 đã làm xong; #7 làm giai đoạn 7a (`ScanMatch.Confidence` + log điểm) và
> đã đánh giá ngưỡng `min_confidence` (đề xuất 0.40, chưa áp dụng); #6 và #8 chưa làm. Chi tiết thiết kế, quyết định
> và kết quả test xem `docs/buoi-05-tom-tat.md` (mục 5b-5g), việc còn lại xem mục 7 của file đó.

Các trường sau đã được khai báo sẵn trong rule JSON (ghi rõ trong `notes[]` của từng file là
`"CHƯA được engine C# đọc (dead field)"`) nhưng engine hiện tại **chưa đọc**, nên hành vi thực tế lỏng hơn
thiết kế PDF gốc cho tới khi mở rộng. Xếp theo mức ảnh hưởng ước lượng:

| # | Trường | Số rule đã khai | Việc cần làm | Vì sao nên làm |
|---|---|---|---|---|
| 1 | `escalation` (keyword kề bên → nâng mức, VD CCCD+"mật khẩu"→SECRET, IP+"password"→CONFIDENTIAL) | 3 file | Thêm `EscalationDefinition` (keywords, escalate_to) vào `RuleDefinition`, áp trong `RegexScanner` sau khi có `baseSeverity`, TRƯỚC khi áp `level_gate` | Compound rule chỉ xét tổ hợp CẢ FILE; escalation xét ngữ cảnh CỤC BỘ quanh 1 chỗ khớp — 2 cơ chế bổ sung nhau, thiếu escalation thì tín hiệu "CCCD đứng cạnh mật khẩu" (rất nguy hiểm) bị bỏ sót |
| 2 | `proximity_window` (từ khoá phải nằm trong X ký tự / CÙNG CÂU với chỗ khớp, chặt hơn `context_required` hiện tại vốn xét cả đoạn) | 7 file | Thêm `ProximityWindowDefinition` vào `PatternDefinition`, thay `context_required` chunk-wide bằng kiểm tra khoảng cách/ranh giới câu khi có khai | Giảm false positive cho pattern có định dạng chung chung (VD CCCD 9-12 số) — hiện tại 1 từ khoá ở ĐẦU đoạn 200 ký tự vẫn tính là "có ngữ cảnh" dù cách xa chỗ khớp |
| 3 | `min_confidence_bypass` (1 chỗ khớp có `confidence` ≥ ngưỡng thì bỏ qua yêu cầu corroboration của gate) | 8 file | Đọc `confidence` (đã có trong `PatternDefinition` nhưng chỉ để tham khảo) + `min_confidence_bypass` trong `LevelGateDefinition`, thêm nhánh bypass trong `RegexScanner` | Rule như `internal_ip` (gate fallback PUBLIC) đang hạ SAI 1 IP thật đứng kèm ngữ cảnh mạnh ("server production: 10.20.30.40") xuống PUBLIC vì chỉ khớp 1 lần, không có rule khác hỗ trợ |
| 4 | `volume_escalation` (>50 bản ghi CÙNG rule trong 1 file → escalate SECRET, đếm riêng theo `rule.Id`) | 5 file | Thêm bước đếm số lượng UNIQUE match theo `RuleId` sau vòng quét, escalate nếu vượt ngưỡng | Phát hiện rò rỉ dữ liệu HÀNG LOẠT (VD file chứa 200 số CCCD) — hiện tại dù 200 lần khớp, mức vẫn chỉ dừng ở mức base của rule |
| 5 | `placeholder_exclusion` (loại giá trị mẫu như `changeme`, `your_password`, `***`) | 14 file | Thêm danh sách blocklist vào `Validator`/bước lọc riêng cho rule `credentials`, `otp`,... | Giảm false positive rất phổ biến trong tài liệu hướng dẫn/SOP kỹ thuật (chứa sẵn ví dụ `password=changeme123`) |
| 6 | `doc_context_discount_keywords` (giảm độ tin cậy nếu gần đó có "hướng dẫn/ví dụ/template/tutorial") | 5 file | Tương tự negation_filter nhưng "giảm điểm" thay vì suppress/downgrade nhị phân — cần cơ chế confidence trước (xem #7) mới áp dụng trọn vẹn | Phân biệt "tài liệu MÔ TẢ cách cấu hình" với "dữ liệu THẬT của hệ thống" |
| 7 | `confidence`/`weight` — chấm điểm tin cậy thay vì khớp nhị phân | 62 file có khai | Việc lớn nhất, ảnh hưởng kiến trúc: engine hiện tại "khớp là tính", không cộng dồn điểm tin cậy; cần thiết kế lại `ScanMatch` mang theo `Confidence` và ngưỡng quyết định theo điểm | Nền tảng cho #3/#6 hoạt động đúng thiết kế gốc; nếu không làm trước, #3/#6 chỉ vá được một phần |
| 8 (kiến trúc) | So khớp nhiều regex tuần tự (mỗi rule 1 lượt `Regex.Matches` riêng) | — (toàn bộ 200+ pattern) | Cân nhắc tiền lọc bằng Aho-Corasick (nhiều chuỗi cố định) trước khi chạy regex đắt, như thiết kế `aho_corasick.go` tham khảo | Hiệu năng — hiện mỗi file quét phải chạy tuần tự ~220 regex; với thư mục lớn/file dài có thể là nút thắt cổ chai. CHƯA đo hiệu năng thật, nên xếp cuối danh sách ưu tiên |

**Khuyến nghị thứ tự làm Bậc 3:** #1 (escalation) và #3 (min_confidence_bypass) trước — tác động trực tiếp
đến độ chính xác của các rule đã có sẵn `level_gate`, và không đòi hỏi thay đổi kiến trúc (`Confidence`
scoring). #7 (confidence scoring) nên làm SAU CÙNG trong nhóm vì #6 phụ thuộc vào nó, còn #8 chỉ nên làm khi
có số liệu hiệu năng thật cho thấy cần thiết (tránh tối ưu sớm khi chưa đo).
