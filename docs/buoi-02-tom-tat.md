# Buổi 2 — Tóm tắt Bước 4 & Bước 5: Regex Engine, Classification

Nạp rule tự động từ file JSON, quét nội dung file, kiểm tra Luhn cho thẻ tín dụng.

## 1. Kết quả đạt được

Service phát hiện được dữ liệu nhạy cảm trong file `.txt` thả vào `watch-folder`. Các rule không nằm trong code mà nằm trong các file JSON ở thư mục `rules/`, được nạp tự động khi service khởi động.

**Luồng chạy hiện tại:**

```
Khởi động:
  rules/*.json -> RuleLoader -> List<SensitivePattern> -> RegexScanner

Khi có file mới/đổi:
  FileSystemWatcher -> debounce 300ms -> FileReaderService đọc file
     -> RegexScanner.Scan(content) -> List<ScanMatch>
     -> log kết quả (giá trị đã được che bằng Mask)
```

**Cấu trúc thư mục:**

```
DlpService/
|-- Program.cs, Worker.cs
|-- Watching/   FileWatcherService.cs
|-- Reading/    FileReaderService.cs
|-- Scanning/   RegexScanner.cs, RuleLoader.cs, RuleDefinition.cs,
|               SensitivePattern.cs, ScanMatch.cs, Validators.cs
`-- rules/      cccd.json, credit-card.json, email.json, phone-vn.json
```

---

## 2. Những thay đổi đã thực hiện

| Việc | File | Nội dung |
|---|---|---|
| Dọn dẹp | `Worker.cs`, `FileReaderService.cs`, `FileWatcherService.cs` | Xoá các khối comment cũ trùng lặp với code thật, xoá `using` thừa. |
| Tổ chức folder | `Watching/`, `Reading/`, `Scanning/`, `rules/` | Chia file theo trách nhiệm, thêm namespace theo folder (ví dụ `DlpService.Scanning`) và `using` ở nơi dùng. |
| 4.1 Class dữ liệu | `Scanning/SensitivePattern.cs`, `Scanning/ScanMatch.cs` | `SensitivePattern` = một rule (tên, regex, mức độ). `ScanMatch` = một chỗ đã bắt được (tên rule, giá trị, vị trí). |
| 4.2 Bộ quét | `Scanning/RegexScanner.cs` | Hàm `Scan(content)` duyệt từng rule, dùng `Regex.Matches` lấy mọi chỗ khớp, bắt `RegexMatchTimeoutException` để một rule treo không làm chết service. |
| Nạp rule tự động (hướng A) | `RuleDefinition.cs`, `RuleLoader.cs`, `rules/cccd.json`, `DlpService.csproj` | `RuleDefinition` là khuôn mẫu đọc JSON. `RuleLoader.LoadFromFolder` đọc mọi `*.json`, kiểm tra, tạo `Regex` biên dịch sẵn có timeout 1 giây. File hỏng chỉ bị bỏ qua và ghi log. `.csproj` copy `rules/*.json` ra thư mục chạy. |
| Đổi cách tạo scanner | `RegexScanner.cs` | Constructor nhận `List<SensitivePattern>` từ ngoài, bỏ rule CCCD viết cứng. |
| Nối vào luồng chạy | `Worker.cs`, `Watching/FileWatcherService.cs` | `Worker` nạp rule từ `AppContext.BaseDirectory/rules` rồi tạo scanner. `FileWatcherService` nhận scanner, quét thay vì in nội dung file, thêm hàm `Mask` che giá trị (`012******901`). |
| 4.3 Thêm rule | `rules/credit-card.json`, `email.json`, `phone-vn.json` | Thêm 3 rule chỉ bằng file JSON, không sửa dòng code nào. |
| 4.5 Validator Luhn | `Validators.cs`, `RuleDefinition.cs`, `SensitivePattern.cs`, `RuleLoader.cs`, `RegexScanner.cs`, `rules/credit-card.json` | Rule có thêm trường `"validator": "luhn"`. Sau khi regex khớp, số còn phải qua kiểm tra Luhn mới được tính là thẻ tín dụng. Tên validator sai thì bỏ qua cả rule và ghi cảnh báo. |

### Các rule hiện có

| Rule | Regex | Mức | Ghi chú |
|---|---|---|---|
| CCCD | `(?<!\d)\d{12}(?!\d)` | High | Đúng 12 chữ số, không dính số ở hai đầu |
| CreditCard | `(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)` | High | 13-19 chữ số, cho phép dấu cách/gạch nối. Có validator `luhn` |
| Email | `[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}` | Medium | Dạng tên@miền.đuôi |
| PhoneVN | `(?<!\d)(?:\+84\|84\|0)(?:3[2-9]\|5[25689]\|7[06-9]\|8[1-9]\|9\d)\d{7}(?!\d)` | Medium | Số di động Việt Nam. Chưa bắt số bàn |

### Kết quả test

| Nội dung trong file test | Kết quả |
|---|---|
| `CCCD: 012345678901` | Bị bắt, log dạng `012******901` |
| `4111 1111 1111 1111` (Luhn hợp lệ) | Bị bắt |
| `4111 1111 1111 1112` (sai 1 số) | Bị loại nhờ Luhn |
| `1234567890123456` (số ngẫu nhiên 16 chữ số) | Bị loại nhờ Luhn |
| `nguyen.van@congty.com.vn` | Bị bắt (Email) |
| `0912345678` và `+84912345678` | Cả hai đều bị bắt (PhoneVN) |
| `12345` | Không bị bắt |

---

## 3. Bước 5: Classification — module theo domain

Sau khi biết file có chứa gì (Bước 4), Bước 5 trả lời câu hỏi "theo nghiệp vụ, file này nhạy cảm cỡ nào?". Mỗi domain (Finance, HR, ...) là một class riêng, engine tự tìm và gọi tất cả module mà không cần biết trước có bao nhiêu domain.

**Luồng chạy:**

```
Khởi động:
  ClassificationEngine tự tìm mọi class implement IClassifierModule (reflection)
     -> tạo đối tượng từng module -> giữ trong _modules

Khi có file (sau khi RegexScanner.Scan xong, nếu matches không rỗng):
  đóng gói ScanContext (đường dẫn, nội dung, matches)
     -> engine.Classify(context): hỏi từng module, gộp lại
     -> log mức cao nhất + lý do từng domain
```

**Cấu trúc thư mục thêm:**

```
DlpService/
`-- Classification/
    |-- SensitivityLevel.cs      (enum: Public, Internal, Confidential, Restricted)
    |-- ScanContext.cs           (đầu vào cho module)
    |-- ClassificationResult.cs  (đầu ra của 1 module)
    |-- IClassifierModule.cs     (hợp đồng chung)
    |-- ClassificationEngine.cs  (tự nạp module, gộp kết quả)
    `-- Modules/
        |-- FinanceModule.cs     (CreditCard/CCCD -> Restricted)
        `-- HrModule.cs          (CCCD/Email -> Confidential)
```

### Những thay đổi đã thực hiện

| Việc | File | Nội dung |
|---|---|---|
| 5.1 Kiểu dữ liệu | `SensitivityLevel.cs`, `ScanContext.cs`, `ClassificationResult.cs`, `IClassifierModule.cs` | `enum` 4 mức tăng dần; `ScanContext` gói dữ liệu đưa cho module; `ClassificationResult` là ý kiến của 1 module; `IClassifierModule` là hợp đồng (`Domain`, `Classify`). |
| 5.2 Engine | `ClassificationEngine.cs` | Constructor tự tìm mọi class implement `IClassifierModule` bằng reflection (`Assembly.GetTypes()`, `Activator.CreateInstance`), nạp 1 lần lúc khởi động. `Classify(context)` gọi từng module (bắt lỗi riêng từng module), gộp lấy mức cao nhất bằng `results.Max(r => r.Level)`. |
| 5.3 Module Finance | `Modules/FinanceModule.cs` | Có `CreditCard` hoặc `CCCD` trong `context.Matches` → `Restricted`. Không có gì liên quan → trả về `null`. |
| 5.4 Nối vào luồng chạy | `Worker.cs`, `Watching/FileWatcherService.cs` | `Worker` tạo `engine = new ClassificationEngine(logger)`, truyền vào `FileWatcherService` cùng `scanner`. Sau khi có `matches`, đóng gói `ScanContext` rồi gọi `engine.Classify`, log `mức {level}` và lý do từng domain. |
| 5.5 Module thứ hai | `Modules/HrModule.cs` | Có `CCCD` hoặc `Email` → `Confidential`. Chỉ thêm 1 file, không sửa `ClassificationEngine`/`Worker`/`FileWatcherService` — xác nhận đúng mục tiêu "thêm domain không sửa core". |

### Kết quả test

| Nội dung file test | Kết quả |
|---|---|
| Chỉ có CCCD + thẻ hợp lệ | `Đã nạp 1 module: Finance` lúc chưa có HR; mức `Restricted`, lý do "Có số thẻ tín dụng, Có số CCCD" |
| CCCD + thẻ hợp lệ + email, sau khi thêm `HrModule` | `Đã nạp 2 module: Finance, HR`; mức `Restricted` (cao nhất); log 2 dòng lý do — 1 từ Finance, 1 từ HR |
| File chỉ có Email (không có CCCD/thẻ) | `FinanceModule` trả `null` (không lên tiếng); `HrModule` báo `Confidential` |

### Đánh giá

- Đạt mục tiêu ban đầu: thêm domain mới (`HrModule`) chỉ cần 1 file, engine tự nạp qua reflection, không sửa core.
- Cả `FinanceModule` và `HrModule` cùng kiểm tra `CCCD` — chấp nhận được ở quy mô 2 module, nhưng nếu có domain thứ 3 cũng lặp lại, nên cân nhắc tách phần chung (chưa cần làm ngay).
- Rule (JSON) có `"enabled": false` để tắt, nhưng module thì chưa có cơ chế bật/tắt tương tự — muốn tắt phải xoá hoặc đổi tên file.
- Chưa test thật sự trường hợp 1 module tự `throw` để xác nhận engine không sập (đã có `try/catch` nhưng chưa kiểm chứng bằng test).

---

## 4. Bảng kiến thức cần nhớ

| Khái niệm | Giải thích ngắn gọn | Ví dụ trong code |
|---|---|---|
| `{ get; set; }` | Thuộc tính đọc và gán lại được bất cứ lúc nào. Hợp với class chỉ để hứng dữ liệu thô. | `RuleDefinition.Name` |
| `{ get; init; }` + `required` | `init`: chỉ gán 1 lần lúc tạo object. `required`: bắt buộc gán khi tạo. Dùng cho dữ liệu đã kiểm tra, cần giữ nguyên. | `SensitivePattern.Regex` |
| `string?` | Kiểu chuỗi cho phép `null`. Dùng khi dữ liệu có thể thiếu. | `RuleDefinition.Pattern` |
| File-scoped namespace | `namespace X;` có dấu `;` ở cuối, áp dụng cho cả file. | `namespace DlpService.Scanning;` |
| Lookaround trong regex | `(?<!\d)` = phía trước không phải số; `(?!\d)` = phía sau không phải số. Không tiêu tốn ký tự. Dùng thay `\b` vì `\b` coi chữ cái là ranh giới từ. | `(?<!\d)\d{12}(?!\d)` |
| `RegexOptions.Compiled` | Biên dịch regex một lần cho nhanh, nên tạo sẵn thay vì tạo lại mỗi lần quét. | `new Regex(p, RegexOptions.Compiled, timeout)` |
| Timeout của regex (ReDoS) | Regex viết dở có thể chạy vô hạn làm treo CPU. Đặt timeout, khi quá hạn thì ném `RegexMatchTimeoutException`. | timeout 1 giây trong `RuleLoader` |
| `Regex.Matches` | Trả về tất cả chỗ khớp; mỗi `Match` có `.Value` và `.Index`. | `foreach (Match match in ...)` |
| Primary constructor | Đặt tham số ngay sau tên class, dùng được ở mọi hàm trong class. | `class RuleLoader(ILogger logger)` |
| `JsonSerializer.Deserialize<T>` | Biến chuỗi JSON thành object kiểu `T`. Tên trường phải khớp tên thuộc tính. | `Deserialize<RuleDefinition>(json, JsonOptions)` |
| `PropertyNameCaseInsensitive` | Cho phép `"name"` trong JSON khớp thuộc tính `Name`. | `JsonSerializerOptions` |
| `Directory.GetFiles` | Lấy đường dẫn mọi file khớp mẫu tên trong thư mục. | `GetFiles(folder, "*.json")` |
| Lambda và `Any()` | `p => điều kiện` đọc là "với mỗi p, xét điều kiện". `Any` kiểm tra có phần tử nào thoả không. Dùng `()`, không dùng `<>`. | `patterns.Any(p => p.Name == rule.Name)` |
| `continue` | Bỏ phần còn lại của vòng lặp, sang phần tử kế. | Bỏ qua rule lỗi trong `RuleLoader` |
| `Func<string, bool>` | Kiểu "một hàm". Phần tử cuối là kiểu trả về, các phần tử trước là kiểu tham số. `?` cho phép chưa có hàm (`null`). | `SensitivePattern.Validator` |
| `TryGetValue` + `out` | Tra từ điển an toàn; `out` trả giá trị ra qua biến truyền vào. | `Validators.ByName.TryGetValue(name, out validator)` |
| `&&` ngắn mạch | Vế trái sai thì vế phải không được tính. Nhờ vậy kiểm tra `!= null` trước rồi mới gọi hàm thì không bị lỗi. | `pattern.Validator != null && !pattern.Validator(v)` |
| `??` | Nếu vế trái là `null` thì dùng vế phải. | `result.Content ?? string.Empty` |
| Range `[..3]`, `[^3..]` | `[..3]` lấy 3 ký tự đầu; `[^3..]` lấy 3 ký tự cuối. | `Mask`: `value[..3] + "***" + value[^3..]` |
| `AppContext.BaseDirectory` | Thư mục chứa file `.exe` đang chạy. Dùng thay đường dẫn tương đối vì khi chạy thành Windows Service thư mục làm việc khác. | `Path.Combine(AppContext.BaseDirectory, "rules")` |
| `CopyToOutputDirectory` | Khai báo trong `.csproj` để file được copy ra thư mục chạy mỗi lần build. | `<None Update="rules\*.json" ... />` |
| Thuật toán Luhn | Duyệt từ phải sang trái, nhân đôi chữ số ở vị trí lẻ (trừ 9 nếu > 9), cộng lại; chia hết cho 10 thì hợp lệ. | `Validators.Luhn` |
| Che dữ liệu khi log | Service DLP không được tự ghi dữ liệu nhạy cảm nguyên văn ra log. | `Mask("012345678901")` -> `012******901` |
| Tách dữ liệu thô và đã kiểm tra | Class thô hứng JSON, class đã kiểm tra dùng để quét. Scanner không phải kiểm tra `null` hay regex lỗi. | `RuleDefinition` -> `SensitivePattern` |
| `SensitivePattern` khác `ScanMatch` | `SensitivePattern` là công thức tìm (ít, nạp một lần). `ScanMatch` là kết quả tìm được (nhiều, sinh mỗi lần quét). | `ScanMatch.PatternName` liên kết hai bên |
| `interface` | "Hợp đồng": liệt kê hàm/thuộc tính bắt buộc phải có, không có thân hàm. Core chỉ biết interface, không biết class cụ thể nào. | `IClassifierModule` |
| Polymorphism (đa hình) | Cùng một lời gọi (`module.Classify(...)`) chạy ra hành vi khác nhau tuỳ object thực sự là gì. | `ClassificationEngine.Classify` gọi qua `IClassifierModule` |
| Reflection (`Assembly.GetTypes`) | Lấy mọi kiểu đã biên dịch trong chương trình lúc chạy, để tự tìm class implement 1 interface mà không cần liệt kê tay. | `LoadModules` trong `ClassificationEngine` |
| `Activator.CreateInstance` + `is ... module` | Tạo object từ 1 kiểu biết lúc chạy (cần constructor không tham số); `is` vừa kiểm tra vừa ép kiểu. | `Activator.CreateInstance(type) is IClassifierModule module` |
| Tuple trả về nhiều giá trị | `(SensitivityLevel Level, List<...> Results)` trả 2 giá trị cùng lúc, có tên để gọi `.Level`, `.Results`. | `ClassificationEngine.Classify` |
| `enum` có thứ tự | Giá trị `enum` tăng dần theo thứ tự khai báo, so sánh được bằng `>`/`<`, dùng `Max()` lấy mức cao nhất. | `results.Max(r => r.Level)` |
| `static` hàm khởi tạo field | Field initializer chạy trước khi `this` sẵn sàng nên không gọi được hàm thường; hàm `static` không cần `this` nên gọi được. | `LoadModules` gọi từ field initializer của `_modules` |
| `<inheritdoc/>` | Kế thừa mô tả `///` từ interface/class cha, tránh viết lại 2 lần. | `FinanceModule.Domain`, `HrModule.Classify` |
| `///` khác `//` | `//` giải thích 1 dòng code (vì sao). `///` mô tả hợp đồng của hàm/class (nhận gì, trả gì) cho người gọi, hiện trong tooltip IDE. | `/// <summary>...</summary>` trên các hàm public |

### Các lỗi hay gặp trong bước này

- `{ get, init; }`: các phần trong khối thuộc tính ngăn cách bằng `;` chứ không phải dấu phẩy. Đúng là `{ get; init; }`.
- `namespace DlpService.Scanning` thiếu `;` ở cuối khi dùng file-scoped namespace.
- `patterns.Any<p => ...>` sai; đúng là `patterns.Any(p => ...)`.
- `foreach (m in matches)` thiếu kiểu; đúng là `foreach (var m in matches)`.
- Dùng nhầm biến: `Mask(matches.Value)` thay vì `Mask(m.Value)` (`matches` là cả danh sách). Tương tự `ruleName` thay vì `rule.Name`.
- Thiếu `}` đóng hàm làm dấu `}` cuối bị hiểu là đóng hàm chứ không đóng class.
- Tên thuộc tính lệch với tên trường JSON (`Enable` so với `enabled`) làm giá trị trong file bị bỏ qua.
- Trong file JSON, dấu `\` phải viết đôi: `\\d` thay cho `\d`.
- Để lại khối code cũ bị comment ngay cạnh code mới. Nên xoá đi, code cũ đã có lịch sử phiên bản lo.
- `ClassificationEngine(logger)` thiếu `new`: tạo object trong C# luôn cần `new TênClass(...)`, không viết được như gọi hàm.
- Constructor chưa thêm tham số mới (`engine`) nhưng thân hàm đã dùng biến đó — biến không tồn tại vì chưa khai báo trong danh sách tham số.
- `Console.WriteLine($"...", giá_trị)` sai: khác với `logger.LogInformation`, `Console.WriteLine` không nhận tham số rời; phải nhét biến ngay trong `{}` của chuỗi nội suy.
- `ScanContext(...)` gọi như hàm thường: `ScanContext` không có constructor nhận tham số (chỉ có `required` property), phải tạo bằng `new ScanContext { FilePath = ..., Content = ..., Matches = ... }`.

---

## 5. Việc có thể triển khai tiếp theo

### Theo lộ trình đã đặt

| Bước | Việc | Trạng thái |
|---|---|---|
| 5 | Classification và module theo domain | **Đã xong** — `IClassifierModule`, `ClassificationEngine`, `FinanceModule`, `HrModule` |
| 6 | Đăng ký thành Windows Service thật | Tiếp theo. Thêm `Microsoft.Extensions.Hosting.WindowsServices`, gọi `UseWindowsService()`, cài bằng `sc.exe`, quản lý qua Service Control Manager. Cần lưu ý đường dẫn và quyền khi chạy dưới tài khoản service. |
| 7 | Hỗ trợ nhiều loại file | Mở rộng `FileWatcherService` và `FileReaderService` cho `.docx`, `.pdf`, không chỉ `.txt`. |

### Cải tiến cho engine hiện tại

| Việc | Lý do / cách làm |
|---|---|
| Sửa an toàn luồng cho `_pendingTimers` | `Dictionary` không an toàn khi nhiều luồng dùng cùng lúc, mà sự kiện của `FileSystemWatcher` có thể đến từ nhiều luồng. Nên đổi sang `ConcurrentDictionary` hoặc dùng `lock`. Đây là rủi ro thật cần xử lý sớm. |
| Đưa đường dẫn `watch-folder` ra cấu hình | Hiện đường dẫn `D:\Service-DLP\watch-folder` viết cứng trong `Worker.cs`. Nên đọc từ `appsettings.json`. |
| Nạp lại rule khi file thay đổi (hot reload) | Theo dõi thư mục `rules/` giống `FileWatcherService`, không cần khởi động lại service khi thêm hoặc sửa rule. |
| Thêm validator khác | Ví dụ kiểm tra mã tỉnh của CCCD để giảm báo nhầm. Chỉ cần thêm hàm và một dòng vào `Validators.ByName`. |
| Giảm báo nhầm cho các rule còn lại | Rule CCCD và PhoneVN vẫn khớp cả số ngẫu nhiên đúng độ dài. Có thể thêm từ khoá ngữ cảnh gần đó (ví dụ "CCCD", "SĐT"). |
| Mở rộng rule | Số điện thoại bàn, mã số thuế, số tài khoản ngân hàng, hộ chiếu. |
| Dùng `enum` cho mức độ nghiêm trọng | `Severity` hiện là chuỗi tự do nên gõ sai chính tả không bị phát hiện. `enum` (`Low`, `Medium`, `High`) an toàn hơn. |
| Ghi log ra file thay vì `Console.WriteLine` | Service chạy nền không có cửa sổ console, cần log bền vững để điều tra sự cố. |
| Viết unit test | Test `Luhn`, `Mask` và các regex với dữ liệu đúng/sai. Đây là những hàm thuần, dễ test. |
| Khởi tạo Git | Thư mục dự án chưa là repository, nên chưa có lịch sử thay đổi để quay lại khi cần. |
