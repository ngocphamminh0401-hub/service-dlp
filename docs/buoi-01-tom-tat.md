# Buổi 1 — Tóm tắt: Setup, đọc file, FileSystemWatcher, Debounce

## 1. Những thay đổi đã thực hiện

| Bước | File | Nội dung |
|---|---|---|
| 1. Setup project | `DlpService.sln`, `DlpService/*` | Tạo Worker Service .NET, chạy nền được, log "Worker running at: ..." |
| 2. Đọc file an toàn | `DlpService/FileReaderService.cs` | Hàm `TryReadTextFile` — kiểm tra tồn tại, kích thước, bắt lỗi khoá file/quyền truy cập, không crash service |
| 3. Theo dõi thư mục | `DlpService/FileWatcherService.cs` | Dùng `FileSystemWatcher` lắng nghe sự kiện `Created`/`Changed` trên `watch-folder`, tự động đọc file khi có thay đổi |
| 3b. Debounce | `DlpService/FileWatcherService.cs` | Gộp các sự kiện liên tiếp của cùng 1 file, chỉ đọc sau 300ms yên tĩnh — tránh đọc trùng và đọc trúng lúc file bị khoá |
| Kết nối | `DlpService/Worker.cs` | `ExecuteAsync` khởi động `FileWatcherService.StartWatching("watch-folder")` khi service chạy |

**Pipeline hiện tại:** File thay đổi trong `watch-folder` → `FileSystemWatcher` phát hiện → debounce 300ms → `FileReaderService` đọc nội dung → log ra console.

(Chưa có: quét regex, phân loại dữ liệu nhạy cảm, đăng ký thành Windows Service thật.)

---

## 2. Bảng kiến thức C# đã học kèm ví dụ

| Khái niệm | Giải thích ngắn gọn | Ví dụ trong code hôm nay |
|---|---|---|
| `class` | Khuôn mẫu gom dữ liệu + hành vi liên quan | `class FileReadResult { Success, Content, ErrorMessage }` |
| Kiểu trả về của hàm | Từ đầu tiên trước tên hàm là kiểu dữ liệu hàm trả ra | `public FileReadResult TryReadTextFile(...)` → trả về 1 `FileReadResult` |
| `try` / `catch` | Bắt lỗi (exception) để xử lý êm đẹp, tránh crash | `catch (IOException) { ... }` khi file đang bị khoá |
| Nhiều `catch` | Mỗi loại lỗi có thể xử lý khác nhau | `catch (IOException)` và `catch (UnauthorizedAccessException)` riêng biệt |
| Object initializer | Gán giá trị thuộc tính ngay lúc tạo object, dùng `=` (không phải `==`) | `new FileReadResult { Success = false, ErrorMessage = "..." }` |
| Dấu `!` (NOT) | Phủ định điều kiện boolean | `if (!File.Exists(filePath))` |
| Verbatim string `@"..."` | Chuỗi không cần escape dấu `\` | `@"D:\Service-DLP\watch-folder"` |
| Event (sự kiện) + `+=` | Đăng ký 1 hàm để tự động chạy khi sự kiện xảy ra | `watcher.Created += OnFileChanged;` |
| Event handler signature | Chữ ký bắt buộc `(object sender, TEventArgs e)` | `void OnFileChanged(object sender, FileSystemEventArgs e)` |
| `Dictionary<TKey, TValue>` | "Từ điển" tra cứu theo khoá | `Dictionary<string, CancellationTokenSource> _pendingTimers` |
| `TryGetValue` | Tra từ điển an toàn, không lỗi khi thiếu khoá | `_pendingTimers.TryGetValue(filePath, out var oldCts)` |
| `CancellationTokenSource` | "Công tắc huỷ" cho 1 tác vụ đang chờ | Huỷ lịch đọc file cũ khi có sự kiện mới cho cùng file |
| `async` / `await` | Chờ 1 việc mất thời gian mà không chặn cả chương trình | `private async Task ProcessAfterDebounceAsync(...)` + `await Task.Delay(...)` |
| `Task.Delay(ms, token)` | Chờ X mili-giây, có thể bị huỷ giữa chừng qua token | `await Task.Delay(300, cancellationToken)` |
| Fire-and-forget `_ = ...` | Gọi hàm async nhưng không chờ kết quả, chạy ngầm | `_ = ProcessAfterDebounceAsync(filePath, newCts.Token);` |
| Debounce | Kỹ thuật gộp nhiều sự kiện dồn dập thành 1 lần xử lý duy nhất, sau khi "yên tĩnh" | Huỷ lịch hẹn cũ + đặt lịch hẹn mới mỗi khi có sự kiện |

### Các lỗi cú pháp hay gặp hôm nay (để nhớ, tránh lặp lại)
- Thiếu dấu `;` cuối mỗi câu lệnh — lỗi gặp nhiều nhất.
- Thiếu `()` khi gọi constructor: `new FileReaderService` (sai) → `new FileReaderService()` (đúng).
- Điều kiện `if` phải có ngoặc đơn: `if (...)`.
- Dùng `=` để gán, `==` để so sánh — không được lẫn lộn.
- Chuỗi ký tự dùng `"..."` (kép), không dùng `'...'` (đơn — đó là ký tự của ngôn ngữ khác).
- C# không có từ khoá `and`/`or` — dùng `&&`/`||`.

---

## 3. Việc cần làm tiếp theo

1. **Dọn dẹp code thừa:** xoá các khối comment cũ trùng lặp còn sót lại trong `FileWatcherService.cs` (bản nháp không dùng nữa) cho file gọn gàng, dễ đọc.
2. **Bước 4 — Regex Engine:** viết module quét nội dung file bằng regex để nhận diện dữ liệu nhạy cảm (ví dụ số CCCD, số thẻ tín dụng...).
3. **Bước 5 — Classification & plugin theo domain:** thiết kế interface (ví dụ `IClassifierModule`) để mỗi domain (bảo hiểm, y tế, tài chính, HR...) là 1 module riêng, thêm domain mới không cần sửa core.
4. **Bước 6 — Đăng ký thành Windows Service thật:** cài đặt, start/stop qua Service Control Manager (SCM), thay vì chỉ chạy bằng `dotnet run`.
5. **Cân nhắc sau:** mở rộng `FileWatcherService` hỗ trợ nhiều loại file hơn (`.docx`, `.pdf`...), không chỉ `.txt`.
