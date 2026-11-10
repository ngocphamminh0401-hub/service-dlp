# Báo cáo tổng hợp — Chuyển DlpService sang Linux (06/11/2026)

Một ngày làm việc trên nhánh `feature/linux`: đóng Bước 7 của kế hoạch tách lõi, rồi làm GĐ2 → GĐ4 của kế hoạch triển khai Linux.
Máy làm việc: Ubuntu 18.04.6 (kernel 4.15, glibc 2.27, systemd 237, Python 3.6), .NET SDK 10.0.403.

Báo cáo chi tiết từng phần (file này chỉ tổng hợp):

| Phần | Báo cáo chi tiết |
|---|---|
| Bước 7 + GĐ2 | `docs/bao-cao-thay-doi-2026-11-06.md` |
| Mất sự kiện thư mục con (so sánh 6 phương án) | `docs/fsw-thu-muc-con-linux.md` |
| GĐ3 | `docs/bao-cao-gd3-watcher-linux.md` |
| GĐ4 | `docs/bao-cao-gd4-dong-goi-linux.md` |
| Tóm tắt buổi | `docs/buoi-17-tom-tat.md`, `buoi-18-tom-tat.md`, `buoi-19-tom-tat.md` |

## 1. Tóm tắt cho người duyệt

- **DlpService chạy được trên Linux như một service thật**: cài bằng `apt install ./dlp-service_0.4.0_amd64.deb`, chạy dưới systemd với
  user riêng `dlp`, chỉ có quyền **đọc** file, **không có mạng**, CPU tối đa 30 %, RAM 512 MB. Cài thật trên Ubuntu 18.04: **34/34 PASS**.
- **Theo dõi thư mục home đáng tin cậy**: giải nén 10.000 file → 0 sót, 0 quét trùng; xoá / đổi tên / chuyển thư mục cập nhật DB;
  tự theo dõi lại khi thư mục bị xoá rồi tạo lại; người dùng mới được thêm trong 60 giây. CPU lúc rảnh **0,47 %**.
- **Không lộ dữ liệu trong log**: kiểm canary bằng service thật ở mức Information và Debug — PASS (cách che cũ: 24 chỗ lộ).
- **Test**: 143 → **233**, tất cả PASS trên Linux. Build 0 lỗi / 0 cảnh báo.
- **Chưa xong**: chưa chạy lại test trên **Windows** sau các thay đổi ở Core; chưa so kết quả quét Linux ↔ Windows (thiếu dữ liệu mẫu
  trên máy Linux); chưa build `.rpm`; chưa quyết định hỗ trợ CentOS 7 (D9).

## 2. Các commit trong ngày

| Commit | Nội dung | Thay đổi |
|---|---|---|
| `0c63f04` | **Bước 7 tách lõi**: test kiến trúc Core, vá mất sự kiện thư mục con trên Linux, SQLite cho glibc cũ | 6 file, +466 / −9 |
| `5e69690` | **GĐ2 bước 2.1**: khung host Linux `DlpService.Host.Linux` | 4 file, +96 |
| `3bcdde0` | **GĐ2 bước 2.2–2.5**: adapter Linux, chạy thật, che log (R7), mẫu thư mục (R8), tắt FileConfidence (R11), đo needs_review (R12) | 27 file, +1.083 / −73 |
| `265e003` | **GĐ3**: nguồn sự kiện inotify riêng, xoá/đổi tên (R4), quét song song (R5), tự theo dõi lại (R6), loại trừ thư mục, người dùng mới, metric | 24 file, +2.334 / −86 |
| `47dc7c5` | **GĐ4**: gói `.deb`/`.rpm`, systemd unit có hardening, script cài/gỡ, kiểm thử cài thật | 19 file, +758 |

Tổng từ Bước 6 (`19c0d82`): 62 file, +4.685 / −116.

## 3. Tiến độ theo kế hoạch

| Giai đoạn | Nội dung | Trạng thái |
|---|---|---|
| Tách lõi Bước 0–6 | (trước hôm nay) | ✅ |
| **Tách lõi Bước 7** | Core sạch + build/test trên Linux | ✅ Đạt |
| **GĐ2** | Host Linux tối thiểu | ✅ Đạt, **trừ 2.4** (đối chiếu Linux ↔ Windows — thiếu `rawdata`) |
| **GĐ3** | Watcher inotify cho thư mục home | ✅ Đạt, trừ p95 trong đợt dồn (cần quyết định) |
| **GĐ4** | Đóng gói + systemd | ✅ Đạt trên Ubuntu 18.04 / systemd 237; `.rpm` + systemd 219 chưa thử |
| GĐ5 | Tika native (tuỳ chọn) | ⏳ |
| GĐ6 | Kiểm thử máy thật Ubuntu 16.04 / 24.04 / CentOS 7 | ⏳ — cần chốt D9 trước |

## 4. Kiến trúc sau khi làm

```
DlpService.Core/              lõi dùng chung (không phụ thuộc Windows/LLM — có test kiến trúc giữ)
DlpService/                   host Windows (SCM, Event Log, ACL, LLM) — không đổi cách chạy
DlpService.Platform.Linux/    MỚI: LinuxPathPolicy, LinuxDataFolderGuard, LinuxAppLayout, LinuxHost, InotifyFileChangeSource
DlpService.Host.Linux/        MỚI: file chạy dlp-service (systemd, không LLM)
DlpService.Tests/             233 test (nay nằm trong DlpService.slnx)
packaging/linux/              MỚI: build gói, script cài/gỡ, unit systemd, kiểm thử cài
scripts/                      + canary-log.py, watcher-scenarios.py; snapshot-ket-qua.py chạy được trên Linux
```

Bố cục trên máy cài: chương trình `/opt/dlp-service`, cấu hình + rule `/etc/dlp-service`, DB `/var/lib/dlp-service`, log
`/var/log/dlp-service` (đều 0700 user dlp, file 0600).

## 5. Số đo chính

| Chỉ số | Kết quả | Mục tiêu |
|---|---|---|
| Giải nén 10.000 file | 0 sót, 0 trùng, 0 tràn hàng đợi, ~4 s | 0 sót, 0 trùng |
| CPU lúc rảnh (60 s) | 0,47 % | < 2 % |
| RAM | ~130 MB | ≤ 300 MB |
| Trễ sự kiện → quét, lúc bình thường | p95 4 ms | ≤ 2 s |
| Trễ trong đợt dồn 10.000 file | p95 2,6 s | ≤ 2 s ⚠️ |
| Watch cho 5.000 thư mục + 2.000 thư mục ẩn | 5.053 / 8.192 (thư mục ẩn không tốn watch) | ≤ 80 % |
| `needs_review` khi tắt LLM (320 file mẫu) | 5,9 % | — |
| Canary log (Information + Debug) | 0 lộ | 0 |
| Cài thật `.deb` | 34/34 PASS | — |

## 6. Lỗi đáng chú ý tìm ra trong ngày (đều đã sửa)

| Lỗi | Hậu quả nếu bỏ qua | Tìm ra khi |
|---|---|---|
| inotify không theo dõi đệ quy → file trong thư mục con mới bị mất; `mv` thư mục vào → **mất toàn bộ** file | Lách DLP dễ dàng (chuẩn bị ở `/tmp` rồi `mv` vào) | Bước 7, test hợp đồng |
| SQLite của NuGet cần glibc 2.34 | Service không ghi DB trên máy cũ; gói `.deb` cũng mang bản đó | Bước 7 (test), GĐ4 (publish) |
| Content root = thư mục làm việc | Dưới systemd **không đọc `appsettings.json`** → mọi cấu hình về mặc định | GĐ2, chạy thật |
| `/etc/dlp-service/appsettings.json` bị file đi kèm ghi đè | Cấu hình của admin không có tác dụng; canary "Debug" của GĐ2 thực ra chạy ở Information | GĐ3, chạy kịch bản |
| Che log giữ 3 ký tự đầu + 3 cuối | CCCD lộ 6/12 số trong log | GĐ2, canary |
| Hết inotify watch lúc đang chạy không có cảnh báo | Thư mục không được theo dõi mà không ai biết | GĐ3, thử vượt giới hạn |
| Sự kiện xoá xếp hàng chung với việc quét | p95 lên 2,8 s khi xoá hàng loạt | GĐ3, đo |
| Cổng chẩn đoán .NET mở mặc định | Chương trình cùng user đọc được bộ nhớ service | GĐ4, rà hardening |

## 7. Thay đổi ảnh hưởng tới bản Windows — **cần chạy lại test trên Windows**

| Thay đổi trong Core | Từ |
|---|---|
| Che log mạnh hơn; log Information chỉ ghi tên rule × số lần | GĐ2 (R7) |
| Câu log "Quyền thư mục dữ liệu", tên lệnh trong `explain` | GĐ2 |
| Theo dõi: bỏ `Filters`, tự lọc đuôi (không phân biệt hoa/thường), quét bù thư mục mới | Bước 7 |
| Xoá / đổi tên / chuyển thư mục → **xoá bản ghi DB**; khởi động đối soát xoá bản ghi file không còn | GĐ3 |
| Quét khởi động song song; không đi theo junction/symlink; bỏ thư mục không đọc được | GĐ3 |
| Watcher lỗi → tự theo dõi lại; file đọc trúng lúc đang ghi → xử lý lại | GĐ3 |

Các tính năng chỉ bật bằng cấu hình (loại trừ thư mục, đối soát định kỳ, làm mới mẫu, metric, tắt FileConfidence) **tắt** trên Windows.

## 8. Việc còn lại và quyết định cần có

| # | Việc / quyết định | Đề xuất |
|---|---|---|
| 1 | **Chạy toàn bộ test trên Windows** trước khi gộp `main` | Làm trước tiên |
| 2 | **D9** — hỗ trợ CentOS 7 / Ubuntu 16.04 (cần net6; Core dùng `Lock` .NET 9+) | Nếu không bắt buộc: đổi yêu cầu tối thiểu thành Ubuntu 18.04+ / RHEL 8+ |
| 3 | Tiêu chí p95 ≤ 2 s có áp cho đợt dồn 10.000 file không | Không: giữ ≤ 2 s cho lúc bình thường + "0 sót, 0 trùng" cho đợt dồn |
| 4 | GĐ2 bước 2.4: đối chiếu Linux ↔ Windows | Chép `rawdata` sang `~/dlp-rawdata`, chạy `scripts/snapshot-ket-qua.py` |
| 5 | Build + thử `.rpm`; thử unit máy cũ trên systemd 219 | Máy CentOS/RHEL hoặc `sudo apt install rpm` |
| 6 | Gói build trên máy không có SQLite tự biên dịch sẽ cần glibc ≥ 2.34 | Thêm bước build SQLite vào CI, hoặc build gói trên máy cũ |
| 7 | Rule họ tên bắt lan sang dòng sau ("Họ tên…" nuốt "Số CCCD") | Xem lại rule (có thể cả bản Windows) |
| 8 | 6/80 file `top_secret` và 8/80 file `public` rơi vùng xám (R12) | Xem từng file khi có dữ liệu |

## 9. Môi trường dev cần biết

- .NET SDK nằm ở `~/.dotnet` (chưa có trong PATH — thêm `DOTNET_ROOT` + `PATH` vào `~/.bashrc`).
- SQLite tự biên dịch ở `~/.local/lib/dlp-sqlite/libe_sqlite3.so`; `Directory.Build.targets` tự chép vào lúc build và publish.
- `sudo` cần mật khẩu và terminal thật — lệnh cần root (cài gói, `test-install.sh`) phải chạy ở terminal riêng.
