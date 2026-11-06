# Buổi 19 (06/11/2026): GĐ4 — đóng gói .deb/.rpm + systemd

Chi tiết: **`docs/bao-cao-gd4-dong-goi-linux.md`**.

| Hạng mục | Trạng thái |
|---|---|
| `packaging/linux/`: build-packages.sh, dlp-setup.sh (cài/gỡ dùng chung), deb/, rpm/, 2 unit systemd, /etc config, sysctl, wrapper explain, README | ✅ |
| Gói `.deb` 0.4.0 (29 MB, tự chứa) | ✅ build + kiểm nội dung + chạy thử chương trình trong gói |
| Gói `.rpm` | ⚠️ spec xong, chưa build (thiếu rpmbuild) |
| `Directory.Build.targets`: thay libe_sqlite3.so cả lúc publish | ✅ |
| `DOTNET_EnableDiagnostics=0` trong unit (thêm ngoài kế hoạch) | ✅ đã kiểm |
| Cài thật + kiểm hardening (`test-install.sh`, người dùng chạy sudo) | ✅ 34/34 PASS |
| Commit | ✅ commit GĐ4 |

## Ghi chú
- D9 mặc định: chỉ net10 → yêu cầu tối thiểu thực tế Ubuntu 18.04 (glibc 2.27). CentOS 7 cần quyết định D9.
- Không dùng logrotate: FileLogger đã tự xoay vòng.
