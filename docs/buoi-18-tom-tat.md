# Buổi 18 (06/11/2026): GĐ3 — watcher inotify cho thư mục home

Chi tiết + số đo: **`docs/bao-cao-gd3-watcher-linux.md`**. Nối tiếp buổi 17 (`docs/buoi-17-tom-tat.md`, GĐ2).

| Hạng mục | Trạng thái |
|---|---|
| Nguồn sự kiện inotify riêng `InotifyFileChangeSource` (Platform.Linux) — FileSystemWatcher không bỏ được thư mục khỏi việc theo dõi | ✅ |
| 3.1 Xoá / đổi tên / di chuyển (R4): xoá bản ghi DB, cả cây con | ✅ |
| 3.2 Bỏ thư mục ẩn + `ExcludeDirectoryNames`; cảnh báo hết watch (khởi động + lúc chạy) | ✅ |
| 3.3 Tự theo dõi lại khi lỗi (R6) + đối soát định kỳ + đối soát lúc khởi động | ✅ |
| 3.4 Quét khởi động song song (R5) + so size/mtime trước/sau khi đọc | ✅ |
| 3.5 Người dùng mới (làm mới mẫu `/home/*/Documents`) | ✅ |
| 3.6 Metric `[Metric]` + `scripts/watcher-scenarios.py` | ✅ 17/17 |
| Sửa lỗi GĐ2: `/etc/dlp-service/appsettings.json` bị file đi kèm ghi đè | ✅ + test |
| `ScheduleProcess` dùng `AddOrUpdate` (tồn đọng GĐ2 #5) | ✅ |
| Test | ✅ 233/233 |
| Commit | ✅ commit GĐ3 |

## Số đo chính
- Giải nén 10.000 file: 0 sót, 0 trùng, 0 tràn, ~4 s.
- 5.000 thư mục + 2.000 thư mục ẩn: 5.053/8.192 watch.
- CPU rảnh 0,47 %; RAM 130 MB; trễ p95 lúc bình thường 4 ms, trong đợt dồn 2,6 s.

## Cần quyết định
- Tiêu chí p95 ≤ 2 s có áp cho đợt dồn 10.000 file không (đề xuất: không — xem báo cáo mục 5).

## Bước sau
- Chạy lại test trên Windows (nhiều thay đổi Core: xoá bản ghi, đối soát, quét song song, R6).
- GĐ4: gói `.deb`/`.rpm` + systemd (kèm `/etc/sysctl.d/` nâng `max_user_watches`, `UMask=0077`).
