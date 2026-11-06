# Mục lục code — DlpService

Bảng tra cứu nhanh: file nào chứa class nào, class đó làm gì. Chỉ liệt kê, không giải thích chi tiết (xem comment `///` trong code hoặc `docs/buoi-XX-tom-tat.md` để biết lý do thiết kế).

Cập nhật khi thêm/xoá file, không cần cập nhật khi chỉ sửa logic bên trong hàm đã có.

## Watching/
| File | Class | Việc chính |
|---|---|---|
| FileWatcherService.cs | FileWatcherService | Theo dõi thư mục (kể cả thư mục con; mọi đuôi `FileReaderService` hỗ trợ) bằng `FileSystemWatcher`, debounce 300ms, giới hạn số file xử lý cùng lúc (`Watch:MaxConcurrency`, mặc định 6, `SemaphoreSlim`), quét file có sẵn khi khởi động, bỏ qua file nội dung không đổi (hash SHA256), quét bù khi buffer tràn (`OnWatcherError`); gọi Reader → Scanner → Engine rồi log kết quả; có rule timeout thì log "CHƯA KẾT LUẬN ĐƯỢC", không gắn mức |

## Reading/
| File | Class | Việc chính |
|---|---|---|
| IContentExtractor.cs | IContentExtractor (interface) | Hợp đồng mọi bộ trích xuất phải theo: `Extensions`, `MaxFileSizeBytes`, `Extract(path)` → text |
| FileReaderService.cs | FileReaderService | Bộ điều phối: tự nạp mọi extractor (reflection), `IsSupported(path)` (bỏ qua file khoá `~$`), `TryReadFile(path)` — chọn extractor theo đuôi rồi **chọn lại theo magic bytes** (`ResolveByContent`, file đổi đuôi → đúng parser + cảnh báo), kiểm tra kích thước, bắt mọi lỗi, chuẩn hoá Unicode NFC, cắt ở 2 triệu ký tự; `FindExtractor` (Pass 2 LLM) chọn theo cùng logic |
| FileTypeSniffer.cs | FileTypeSniffer, DetectedFormat | Nhận diện định dạng thật từ byte đầu file: zip → docx/xlsx/pptx (theo phần chính bên trong), OLE → doc/xls/ppt (theo tên luồng trong thư mục OLE), pdf, rtf, văn bản (BOM / không có byte 0), nhị phân lạ |
| FileReaderService.cs | FileReadResult | Kết quả 1 lần đọc: `Success`, `Content`, `ErrorMessage`, `Warning` |

## Reading/Extractors/
| File | Class | Việc chính |
|---|---|---|
| ExtractorHelpers.cs | ExtractorHelpers (internal) | `OpenShared` (mở file cho phép chương trình khác đang mở), `JoinRow` (nối ô bảng bằng ` \| `) |
| TextFileReader.cs | TextFileReader (internal) | Đọc file text: UTF-8 (có/không BOM), gặp ký tự lỗi thì thử lại Windows-1258 |
| TxtExtractor.cs | TxtExtractor | `.txt` — đọc trực tiếp (≤ 10MB) |
| CsvExtractor.cs | CsvExtractor | `.csv` — giữ nguyên từng dòng (≤ 10MB) |
| DocxExtractor.cs | DocxExtractor | `.docx` (OpenXml) — đoạn, bảng, header/footer, chú thích, comment, chữ đã xoá khi Track Changes (≤ 50MB) |
| PptxExtractor.cs | PptxExtractor | `.pptx` (OpenXml) — chữ + bảng từng slide (kể cả slide ẩn), ghi chú người thuyết trình (≤ 50MB) |
| ExcelExtractor.cs | ExcelExtractor | `.xlsx`, `.xls` (ExcelDataReader) — mọi sheet kể cả sheet ẩn, mỗi hàng 1 dòng; khôi phục số 0 đầu, ngày `dd/MM/yyyy` (≤ 50MB) |
| PdfExtractor.cs | PdfExtractor | `.pdf` (PdfPig) — text theo thứ tự đọc từng trang; PDF scan trả rỗng (≤ 50MB) |
| TikaNativeExtractor.cs | TikaNativeExtractor | `.doc`, `.ppt`, `.rtf` (Tika native qua P/Invoke) — mỗi dòng text 1 block (cắt ở ranh giới dòng, không cắt giữa số thẻ/CCCD); mặc định TẮT, tắt/không nạp được thì `Extensions` rỗng (≤ 50MB) |

## Reading/Native/
Tích hợp Tika native (tika_native.dll biên dịch bằng GraalVM). Cấu hình ở `appsettings.json` mục `TikaNative`, mặc định `Enabled = false`.
| File | Class | Việc chính |
|---|---|---|
| TikaNativeOptions.cs | TikaNativeOptions | Cấu hình: `Enabled`, `DllPath`, `Sha256` (ghim mã băm), `RequireSha256`, `TimeoutMs`, `MaxChars` |
| TikaNativeLibrary.cs | TikaNativeLibrary (internal) | Bọc DLL bằng P/Invoke: kiểm tra SHA-256 trước khi nạp, tạo isolate 1 lần, mỗi lần gọi attach/detach luồng, `Extract`, `Detect`, `Dispose` (huỷ parser + isolate) |
| TikaNativeRuntime.cs | TikaNativeRuntime (static) | Giữ DUY NHẤT 1 thư viện cho cả tiến trình: `Configure` (Worker gọi khi khởi động), `IsAvailable`, `Shutdown`; nạp thất bại chỉ ghi cảnh báo, không dừng service |
| TikaNativeException.cs | TikaNativeException, TikaNativeTimeoutException, TikaNativeEncryptedException | Các loại lỗi (log chỉ ghi tên loại, không ghi nội dung) |

## Scanning/
| File | Class | Việc chính |
|---|---|---|
| CandidateScorer.cs | CandidateScorer | Điểm cấp candidate `Score` 0-1: điểm gốc + boost từ khoá + thưởng checksum − phạt negation, × hệ số tài liệu hướng dẫn/mẫu |
| ScanResult.cs | ScanResult | Kết quả 1 lần quét: `Matches`, `TimedOutPatterns` (rule bị timeout), `IsComplete` |
| RegexScanner.cs | RegexScanner | `Scan(content)` → `ScanResult`, quét nội dung theo mọi rule đã nạp (rule bị timeout được ghi lại, không nuốt im lặng); áp validator, placeholder, proximity (+ chế độ bảng), negation, escalation, volume, level gate (+ bypass), tính điểm tin cậy |
| RuleLoader.cs | RuleLoader | `LoadFromFolder(path)` → `List<SensitivePattern>`, đọc mọi file `*.json` trong `rules/` (bỏ qua `compound.json`) |
| RuleDefinition.cs | RuleDefinition | Khuôn mẫu đọc 1 file rule JSON (dữ liệu thô, có thể thiếu trường) |
| PatternDefinition.cs | PatternDefinition | 1 pattern con trong mảng `patterns` của rule nhiều-pattern (dữ liệu thô) |
| KeywordGroup.cs | KeywordGroup | Nhóm từ khoá của rule: `Primary` (chính) và `Secondary` (phụ) |
| KeywordGroupJsonConverter.cs | KeywordGroupJsonConverter | Cho phép trường `keywords` trong JSON là mảng (rule cũ) hoặc object `{primary, secondary}` (rule mới) |
| NegationFilterDefinition.cs | NegationFilterDefinition | Cấu hình thô của trường `negation_filter` |
| NegationRule.cs | NegationRule | Negation filter đã sẵn sàng: gần chỗ khớp có từ phủ định thì xử lý theo `Action` |
| LevelGateDefinition.cs | LevelGateDefinition | Cấu hình thô của trường `level_gate` |
| LevelGateRule.cs | LevelGateRule | Level gate đã sẵn sàng: đủ bằng chứng củng cố mới giữ mức gốc, không thì hạ xuống `FallbackLevel` |
| EscalationDefinition.cs | EscalationDefinition | Cấu hình thô của trường `escalation` (`keywords`, `escalate_to`) |
| EscalationRule.cs | EscalationRule | Escalation đã sẵn sàng: gần chỗ khớp có từ khoá thì nâng mức lên `EscalateTo` (chỉ nâng, không hạ) |
| ProximityWindowDefinition.cs | ProximityWindowDefinition | Cấu hình thô của trường `proximity_window` cấp rule (`max_chars`, `same_sentence_required`, `keyword_scope`) |
| ProximityRule.cs | ProximityRule | Proximity đã sẵn sàng (từ `proximity_window` cấp rule hoặc `require_keyword_within` cấp pattern): từ khoá phải nằm gần chỗ khớp, cùng câu nếu yêu cầu |
| VolumeEscalationDefinition.cs | VolumeEscalationDefinition, VolumeThresholdDefinition | Cấu hình thô của trường `volume_escalation` (danh sách bậc `min_count` → `escalate_to`) |
| VolumeEscalationRule.cs | VolumeEscalationRule | Volume escalation đã sẵn sàng: `LevelFor(số giá trị khác nhau)` trả mức của bậc cao nhất đạt được |
| PlaceholderExclusionDefinition.cs | PlaceholderExclusionDefinition | Cấu hình thô của trường `placeholder_exclusion` (`value_blocklist_patterns`, `value_blocklist_full_match`, `known_test_values`) |
| PlaceholderRule.cs | PlaceholderRule | Placeholder đã sẵn sàng: regex blocklist đã biên dịch + tập số test đã chuẩn hoá |
| FalsePositiveReductionDefinition.cs | FalsePositiveReductionDefinition | Cấu hình thô của trường `false_positive_reduction` |
| SensitivePattern.cs | SensitivePattern | 1 rule (hoặc 1 pattern con) đã kiểm tra hợp lệ, sẵn sàng để quét (regex đã biên dịch, kèm negation/level gate/tags) |
| ScanMatch.cs | ScanMatch | 1 chỗ đã khớp trong nội dung file (tên rule, giá trị, vị trí, mức đã qua gate, category/id/tags, điểm tin cậy `Confidence`) |
| Validators.cs | Validators, ValidatorEntry | `Luhn(value)` kiểm tra checksum thẻ tín dụng; bảng `ByName` tra tên validator → `ValidatorEntry` (hàm + cờ `IsChecksum`: chỉ checksum mới là bằng chứng cho level_gate) |

## Scanning/Validation/
| File | Class | Việc chính (tên đăng ký trong `Validators.ByName`) |
|---|---|---|
| ValidatorText.cs | ValidatorText (internal) | `DigitsOnly`, `ValueAfterLabel` — tách giá trị khỏi nhãn trong chuỗi regex đã khớp |
| CccdValidator.cs | CccdValidator | `vn_cccd_prefix` (checksum): 63 mã tỉnh + mã giới tính/thế kỷ 0-3 + năm sinh không ở tương lai |
| TaxCodeValidator.cs | TaxCodeValidator | `mst` (checksum): chữ số kiểm tra thứ 10 (trọng số 31,29,23,19,17,13,7,5,3, mod 11), nhận 10 hoặc 13 số |
| IbanValidator.cs | IbanValidator | `iban` (checksum): ISO 7064 mod-97 = 1 |
| SecretEntropyValidator.cs | SecretEntropyValidator | `secret_entropy` (bộ lọc): dài ≥ 8, không phải biến mẫu, entropy Shannon ≥ 3,0, ≥ 2 loại ký tự |
| EmailValidator.cs | EmailValidator | `email_personal` (bộ lọc): đúng cấu trúc + không phải hộp thư chung (tuyendung@, phongnhansu@...) |
| PhoneValidator.cs | PhoneValidator | `vn_phone` (bộ lọc): đầu số nhà mạng / mã vùng hợp lệ, loại số giả (0900000000, 0912345678) |

## Classification/
| File | Class | Việc chính |
|---|---|---|
| SensitivityLevel.cs | SensitivityLevel (enum) | 4 mức: Public, Internal, Confidential, Restricted |
| SensitivityLevel.cs | SensitivityLevelExtensions | Chuyển chuỗi Severity của rule (cả kiểu cũ "High" lẫn kiểu "SECRET") thành `SensitivityLevel` |
| ScanContext.cs | ScanContext | Gói dữ liệu đưa cho module: đường dẫn, nội dung, danh sách ScanMatch |
| ClassificationResult.cs | ClassificationResult | Kết luận của 1 module: domain, mức, lý do |
| IClassifierModule.cs | IClassifierModule (interface) | Hợp đồng mọi module domain phải theo: `Domain`, `Classify(context)` |
| ClassificationEngine.cs | ClassificationEngine | Tự nạp mọi module qua reflection, gọi từng module, gộp mức cao nhất |
| ScoreRouter.cs | ScoreRouter, RouterOptions, RouteDecision, RouteZone | Router ba vùng: độ tin cậy = S1 của mức regex gán; ≥ T_high chắc chắn, T_low..T_high vùng xám (cần LLM), < T_low tín hiệu yếu. Ngưỡng từ `appsettings.json` mục `Score` |
| EscalationPolicy.cs | EscalationPolicy, EscalationOptions, EscalationDecision, EscalationGroup | Eligibility (≥ 200 ký tự, đường dẫn loại trừ) + phân nhóm P1 (vùng xám) / P2 (tên file/thư mục đáng nghi, so theo từ không dấu) / P3 (lấy mẫu theo `file_hash`). Cấu hình `appsettings.json` mục `Llm` |
| FileScorer.cs | FileScorer, FileScore, RuleScore, S1Thresholds | Điểm S1 cấp file: gộp theo rule (bỏ trùng + noisy-OR top 5) × mật độ → noisy-OR theo mức; `LevelFor` đổi S1 thành mức theo ngưỡng. Đang chạy song song (shadow), chưa quyết định mức |
| CompoundRuleDefinition.cs | CompoundRuleDefinition | Khuôn mẫu 1 compound rule trong `rules/compound.json` (dữ liệu thô) |
| CompoundRule.cs | CompoundRule | 1 compound rule đã kiểm tra hợp lệ, sẵn sàng để đánh giá |

## Classification/Modules/
| File | Class | Việc chính |
|---|---|---|
| FinanceModule.cs | FinanceModule | Domain tài chính: xét mọi rule tài chính đã nạp, mức lấy từ Severity thật của từng chỗ khớp (đã qua level gate) |
| HrModule.cs | HrModule | Domain nhân sự: xét rule trong `rules/hr/` cộng CCCD/Email làm tín hiệu chung |
| PiiModule.cs | PiiModule | Domain dữ liệu cá nhân: xét rule trong `rules/pii/` |
| OrgModule.cs | OrgModule | Domain tổ chức/nội bộ: xét rule trong `rules/org/` |
| CompoundRuleModule.cs | CompoundRuleModule | Tự nạp `rules/compound.json`; dựng tập tag từ mọi ScanMatch, nâng mức nếu đủ tổ hợp tag |

## Logging/
| File | Class | Việc chính |
|---|---|---|
| FileLogger.cs | FileLogger | Implement `ILogger`: ghi log ra file text theo ngày (`logs/dlpservice-YYYY-MM-DD.log`), có khoá dùng chung chống ghi chồng |
| FileLoggerProvider.cs | FileLoggerProvider | Implement `ILoggerProvider`: tạo `FileLogger` cho mỗi category, tự tạo thư mục `logs/` |

## Storage/
| File | Class | Việc chính |
|---|---|---|
| ResultStore.cs | ResultStore, ScanRecord, FindingRecord | Ghi kết quả quét vào SQLite (`data\dlp.db`): `file_state`, `scan_result` (provisional/final, needs_review), `finding` (theo rule, không có giá trị thật) — 1 transaction/file, an toàn đa luồng. Tạo sẵn đủ lược đồ mục 3.1 (`llm_job`, `llm_result`, `llm_cache`, `audit`, `user_version=1`); đọc lại `journal_mode`/`busy_timeout`, sai thì cảnh báo `[DB]`. Cache: `TryGetFileState` / `MarkUnchanged` (bỏ qua file không đổi theo size+mtime rồi file_hash; mtime lưu 100 ns) |
| ResultStore.LlmJobs.cs | ResultStore (partial), LlmJobStatus, LlmJobPriority, LlmJob, LlmResultRecord | Hàng đợi `llm_job` + máy trạng thái mục 3.2 (pending/running/done/failed/timeout/stale): `TryPickNextJob`, `CompleteJob` (upsert `llm_result`, idempotent), `FailJob`/`TimeoutJob` (backoff 1/5/20 phút, tối đa 3 lần thử lại), `MarkJobStale`, `RecoverRunningJobs`, `EnqueueMissingGreyJobs`, `PurgeOldJobs`; mỗi bước chuyển ghi `audit`. Hạn ngạch (`JobQuotaOptions`): P1 vượt/giờ → hoãn, P2/P3 vượt/ngày → bỏ, hàng đợi đầy → bỏ P2/P3 |
| ResultStore.LlmCache.cs | ResultStore (partial) | Cache hai tầng: `TryGetResultForContent` (theo `file_hash` + model/prompt, từ `llm_result`), `TryGetLlmCache` (theo `pack_hash`), `PurgeLlmCache`, `CountLlmCache`; `llm_cache` upsert (trùng khoá → ghi đè) trong `CompleteJob`; kiểm tra `json_out` là JSON hợp lệ |
| DataFolderSecurity.cs | DataFolderSecurity | Khoá ACL thư mục dữ liệu: tắt kế thừa, chỉ SYSTEM + Administrators + tài khoản đang chạy có Full Control |

## Gốc project
| File | Class | Việc chính |
|---|---|---|
| Worker.cs | Worker | Điểm khởi động luồng nền: nạp rule, tạo scanner + engine, bắt đầu theo dõi thư mục |
| Program.cs | — | Khởi tạo Generic Host, đăng ký `Worker`, `AddWindowsService()` (chạy dưới SCM), Event Log + File Logger |

## rules/ (dữ liệu, không phải code)
| Thư mục/File | Nội dung |
|---|---|
| `rules/financial/` | Rule tài chính: thẻ tín dụng các loại, tài khoản ngân hàng, IBAN/SWIFT, OTP, CVV, mã số thuế... |
| `rules/hr/` | Rule nhân sự: thư mời làm việc, kỷ luật, khiếu nại quấy rối, tai nạn lao động... |
| `rules/pii/` | Rule dữ liệu cá nhân: CCCD, hộ chiếu, email, SĐT, ngày sinh, sức khoẻ, sinh trắc học... |
| `rules/org/` | Rule tổ chức/nội bộ: hợp đồng, credentials, IP nội bộ, tài liệu mật, watermark... |
| `rules/compound.json` | Compound rule (tổ hợp tag), do `CompoundRuleModule` nạp riêng, `RuleLoader` bỏ qua |

## scripts/
| File | Việc chính |
|---|---|
| install-service.ps1 | `sc.exe create` đăng ký service (tự khởi động cùng Windows) + `sc.exe failure` tự restart khi crash |
| uninstall-service.ps1 | Dừng service + `sc.exe delete` |

## tools/perf/
| File | Việc chính |
|---|---|
| Measure-DlpPerformance.ps1 | Đo hiệu năng service đang chạy từ bên ngoài: kịch bản tuần tự (độ trễ từng file) + dồn dập (thông lượng, CPU/RAM, số lần buffer tràn); bấm giờ bằng cách đọc log mỗi 50ms. Lưu dạng UTF-8 có BOM |
| results/perf-*.csv | Kết quả chi tiết từng lượt đo (tên file + số đo, không có nội dung) |

## tools/eval/DlpBaseline/ (console app, tham chiếu DlpService)
| File | Class | Việc chính |
|---|---|---|
| Program.cs | BaselineRunner | Chạy pipeline của service (đọc → quét → phân loại) trên từng file của rawdata, bấm giờ từng bước |
| SampleLoader.cs | Sample, SampleLoader | Đọc nhãn từ `_metadata.jsonl`, ghép nhãn → `SensitivityLevel`, lấy mẫu chia đều (`--limit`) |
| Metrics.cs | FileResult, BinaryScore, ConfusionMatrix, TimingSummary | Precision/recall/F1, ma trận nhầm lẫn 4 mức, p50/p95 |
| Options.cs | Options | Tham số dòng lệnh: thư mục rawdata, `--limit`, `--format`, `--out` |
| Report.cs | RunInfo, Report | Ghi CSV từng file (chỉ mã rule + số đo) và báo cáo Markdown (máy, độ chính xác A/B/C, tốc độ, S1) |
| S1Tuning.cs | S1Tuning, TuningResult | Chia train/test theo id mẫu, dò lưới ngưỡng S1, so với cách hiện tại trên test |

Kết quả ghi vào `tools/eval/results/baseline-<ngày giờ>.csv` và `.md`.

## tools/resilience/DlpResilience/ (console app, tham chiếu DlpService)
| File | Class | Việc chính |
|---|---|---|
| Program.cs | ServiceProcess, FakeWorker, Wait, Report | Test chịu lỗi: chạy `DlpService.exe` thật với DB tạm (`--Storage:DbPath`), **kill thật** giữa lúc quét (A) và giữa lúc job đang chạy (B, worker giả chạy process riêng, 30% job hoàn thành 2 lần), khởi động lại, kiểm tra bất biến (integrity, không mất/trùng, giống lần chạy sạch, mỗi job 1 `llm_result`, số lần lấy job = 1 + số lần khôi phục) |
| Db.cs | Db | Truy vấn DB của process khác (không pooling, busy_timeout) |

Chạy: `dotnet run -c Release --project tools/resilience/DlpResilience [-- --kills 3 --keep]` (vài phút). Báo cáo: `tools/resilience/results/resilience-<ngày giờ>.md`.
