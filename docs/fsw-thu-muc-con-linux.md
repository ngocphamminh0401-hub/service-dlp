# Mất sự kiện file trong thư mục con mới trên Linux — phân tích và so sánh phương án

> Bước 7 tách lõi (06/11/2026). Liên quan: `DlpService.Core/Platform/FileSystemWatcherSource.cs`,
> `DlpService.Tests/Platform/FileChangeSourceContractTests.cs`, `docs/nghien-cuu-chuyen-doi-linux.md` (mục 3.6).

## 1. Vấn đề

Test hợp đồng `File_trong_thu_muc_con_cung_duoc_bao` fail trên Ubuntu (đạt trên Windows).

**Vì sao?** Windows có sẵn chế độ "theo dõi cả cây thư mục" ngay trong hệ điều hành (`ReadDirectoryChangesW` với cờ đệ quy).
Linux (`inotify`) thì không: mỗi thư mục phải đăng ký theo dõi **riêng**. Khi có thư mục con mới, .NET nhận sự kiện
"có thư mục mới" rồi mới đăng ký theo dõi nó — giữa hai việc đó là một **khoảng hở**. File ghi vào trong khoảng hở
không bao giờ được báo.

### Đo thực tế (Ubuntu 18.04, kernel 4.15, .NET 10.0.403)

Tạo `con/chau`, chờ X ms, ghi `d.txt`; sau 1,5 s ghi thêm `e.txt`:

| Chờ sau khi tạo thư mục | `d.txt` (ghi ngay) | `e.txt` (ghi sau 1,5 s) |
|---|---|---|
| 0 ms (3 lần) | ❌ mất | ✅ thấy |
| 50 ms | ✅ thấy | ✅ thấy |
| 200 ms / 1000 ms | ✅ thấy | ✅ thấy |

→ khoảng hở < 50 ms, nhưng đủ để mất file.

### Ảnh hưởng thực tế với DLP

| Thao tác của người dùng | Code cũ trên Linux |
|---|---|
| `cp -r`, giải nén `.zip`, `git clone` vào thư mục theo dõi | Mất một phần file (file ghi ngay sau khi tạo thư mục) |
| `mv` 1 thư mục **đã có sẵn file** từ ngoài vào | **Mất toàn bộ** — không có sự kiện tạo file nào, chỉ có 1 sự kiện cho thư mục |
| Ghi/sửa file trong thư mục đã có từ trước | Bình thường |

Hàng thứ 2 nghiêm trọng nhất: đây là cách "lách" DLP rất dễ (chuẩn bị thư mục ở `/tmp` rồi `mv` vào).
Trên Windows, `mv` trong cùng ổ đĩa cũng chỉ phát 1 sự kiện Renamed cho thư mục → **code cũ trên Windows cũng bỏ sót
trường hợp này** (chưa kiểm trên Windows, cần xác nhận).

## 2. Các phương án

### A. Quét bù khi có thư mục mới — ✅ ĐÃ CHỌN, đã cài

Khi watcher báo "có thư mục mới" (Created) hoặc "thư mục được đổi tên / chuyển vào" (Renamed), liệt kê mọi file đã có
bên trong (đệ quy) và báo `FileChanged` cho từng file đúng đuôi.

Chi tiết cài đặt:
- **Bỏ `watcher.Filters`, tự lọc đuôi.** `Filters = "*.txt"` áp cho cả tên thư mục → thư mục `con` không khớp → không
  có sự kiện thư mục nào để quét bù. Tự so đuôi bằng `HashSet` không phân biệt hoa/thường.
- **Quét chạy nền (`Task.Run`)** để không chặn luồng watcher khi thư mục có hàng nghìn file.
- **Báo sự kiện lần lượt (`_raiseLock`)** vì `FileWatcherService.ScheduleProcess` không an toàn khi bị gọi song song
  cho cùng 1 đường dẫn (xem mục 4).
- **Báo trùng là vô hại:** file vừa có trong lần quét bù vừa có sự kiện thường → `FileWatcherService` gộp bằng debounce
  300 ms theo đường dẫn; ngoài ra `file_state` (size, mtime, hash) bỏ qua file không đổi.

| Ưu | Nhược |
|---|---|
| Sửa tận gốc, file được quét **ngay** (không trễ) | File đang ghi dở có thể bị liệt kê sớm → dựa vào debounce + sự kiện Changed sau đó |
| Bắt được cả trường hợp `mv` thư mục vào | Thư mục rất lớn → 1 đợt dồn sự kiện (đã chạy nền, có giới hạn đồng thời `_gate` phía sau) |
| Chỉ 1 file code, ~90 dòng; dùng chung Windows/Linux | Không cứu được khi tràn hàng đợi inotify hoặc hết `max_user_watches` |
| Chi phí gần như 0: .NET vốn lọc tên file ở tầng managed trên cả hai OS, bỏ `Filters` không làm kernel gửi thêm sự kiện* | Thay đổi nhỏ về hành vi (mục 3) |

\* Theo hiểu biết về cài đặt `FileSystemWatcher` của .NET (inotify và `ReadDirectoryChangesW` đều không lọc theo tên); chưa đo.

### B. Thêm watcher thứ 2 chỉ để bắt thư mục — ❌ không chọn

Giữ nguyên watcher lọc đuôi, thêm 1 watcher `NotifyFilter = DirectoryName` để nhận sự kiện thư mục rồi quét bù.

| Ưu | Nhược |
|---|---|
| Không đổi watcher cũ | Trên Linux **gấp đôi** số inotify watch (mỗi watcher đăng ký riêng cho từng thư mục con). Máy này `max_user_watches = 8192` → cây > ~4000 thư mục là hết |
| | 2 watcher, 2 luồng sự kiện → khó suy luận thứ tự hơn |

### C. Quét đối soát định kỳ (reconciliation) — ⏳ nên làm thêm ở GĐ2, KẾT HỢP với A

Cứ N phút liệt kê toàn bộ thư mục theo dõi, so với `file_state` trong DB, file mới/đổi thì đưa vào hàng đợi.

| Ưu | Nhược |
|---|---|
| Lưới an toàn cho **mọi** loại mất sự kiện: tràn hàng đợi inotify, hết `max_user_watches`, service tắt lúc file được ghi, ổ mạng | Trễ tới N phút |
| Không phụ thuộc OS | Tốn I/O khi cây lớn (giảm được nhờ so size+mtime trước, đã có trong `file_state`) |
| Một phần đã có: lần quét lúc khởi động service | Cần lập lịch, tránh chạy chồng với quét khởi động |

### D. Adapter inotify riêng (gọi thẳng API Linux) — ⏳ GĐ2 nếu A + C chưa đủ

Tự viết `IFileChangeSource` cho Linux bằng P/Invoke `inotify_init1` / `inotify_add_watch`: nhận `IN_CREATE | IN_ISDIR`
→ đăng ký watch → quét bù, tất cả trên cùng 1 luồng, tự kiểm soát thứ tự.

| Ưu | Nhược |
|---|---|
| Kiểm soát hoàn toàn: đọc được `IN_Q_OVERFLOW`, biết chính xác khi nào watch đã đăng ký xong | Nhiều code native, khó test, dễ lỗi (đúng mức "adapter riêng" kế hoạch đã hoãn sang GĐ2) |
| Báo lỗi rõ khi hết `max_user_watches` | Vẫn cần quét bù như A — khoảng hở là bản chất của inotify, không mất đi |

### E. fanotify — ❌ không dùng được trên máy này

Theo dõi cả hệ thống file một lần (không cần watch từng thư mục), nhưng sự kiện tạo/xoá/đổi tên (`FAN_CREATE`…) cần
kernel **5.1+** và quyền root. Máy này kernel **4.15** → không có. Đáng xem lại nếu nâng lên Ubuntu 22.04+ (kernel 5.15).

### F. Sửa test: chờ 50 ms trước khi ghi file — ❌ loại

Test xanh nhưng lỗi thật vẫn còn — người dùng thật không chờ 50 ms.

### Bảng tổng hợp

| | Hết mất file khi `cp -r` | Hết mất khi `mv` thư mục vào | Chống tràn / hết watch | Độ trễ | Công sức | Chạy trên máy này |
|---|---|---|---|---|---|---|
| **A (đã làm)** | ✅ | ✅ | ❌ | Ngay | Thấp | ✅ |
| B | ✅ | ✅ | ❌ (tệ hơn: gấp đôi watch) | Ngay | Thấp | ✅ |
| C | ✅ (trễ) | ✅ (trễ) | ✅ | N phút | Trung bình | ✅ |
| D | ✅ | ✅ | Phát hiện được, không tự cứu | Ngay | Cao | ✅ |
| E | ✅ | ✅ | ✅ (ít watch) | Ngay | Cao | ❌ kernel 4.15 |
| **A + C (khuyến nghị lâu dài)** | ✅ | ✅ | ✅ | Ngay (C chỉ là lưới) | Trung bình | ✅ |

**Kết luận:** A là lựa chọn tối ưu cho GĐ này — rẻ, sửa đúng chỗ, không thêm watch. Phương án tối ưu **lâu dài** là
**A + C**: A lo tốc độ, C lo những gì inotify chắc chắn sẽ đánh rơi (tràn, hết watch, service tắt). D chỉ cần khi số
liệu thực tế cho thấy A + C không đủ.

## 3. Hành vi đã thay đổi so với code cũ (cần biết)

| Tình huống | Trước | Sau |
|---|---|---|
| File trong thư mục con vừa tạo (Linux) | Có thể mất | Được báo |
| `mv` thư mục có sẵn file vào | Không báo file nào | Báo mọi file đúng đuôi bên trong |
| Đuôi viết hoa `A.TXT` (Linux) | Không báo (Linux so đuôi phân biệt hoa/thường) | Được báo (giống Windows) |
| Đổi tên `a.txt` → `a.bak` | Báo `FileChanged(a.bak)` (phía sau tự lọc bỏ) | Chỉ báo `FileDeleted(a.txt)` |
| Đổi tên thư mục bên trong vùng theo dõi | Không báo gì cho file bên trong | Báo lại mọi file với đường dẫn mới |
| Thư mục cũng phát `Changed` khi nội dung đổi | (bị Filters chặn) | Bỏ qua bằng `Directory.Exists` |

## 4. Còn tồn đọng

1. **Hết `max_user_watches`** (máy này 8192): cây thư mục lớn sẽ không được theo dõi đủ. Tăng bằng
   `sysctl fs.inotify.max_user_watches=524288` khi triển khai (đưa vào gói cài đặt GĐ2), và cần C để lưới lại.
2. **`ScheduleProcess` không an toàn đa luồng** (đã có từ trước): bước "lấy CTS cũ ra" và "ghi CTS mới vào" là 2 thao
   tác rời → 2 luồng gọi cùng lúc cho cùng 1 file có thể xử lý file 2 lần. `_raiseLock` trong source đã chặn trường hợp
   này cho 1 source; sửa tận gốc nên dùng `AddOrUpdate` trong `FileWatcherService` (cần Change Brief riêng).
3. **Đổi tên thư mục không báo `FileDeleted` cho đường dẫn cũ** của từng file bên trong → DB còn bản ghi đường dẫn cũ.
   Gộp vào việc xử lý `FileDeleted` (rủi ro R4).
4. **Chưa chạy lại test trên Windows** sau thay đổi này (đặc biệt mục "`mv` thư mục" và "đuôi viết hoa").

## 5. Cách kiểm chứng

- Test mới trong `FileChangeSourceContractTests`: `Chep_ca_cay_thu_muc_thi_bao_du_moi_file` (3 cấp × 10 file ghi dồn dập),
  `Chuyen_thu_muc_tu_ngoai_vao_thi_bao_file_ben_trong`.
- Chạy với **code cũ**: 3 test fail (2 test mới + test thư mục con). Với **code mới**: 8/8 test watcher pass, 5 lần liên tiếp.
- Toàn bộ: 148/148 test pass trên Ubuntu 18.04.
