# Báo cáo GĐ4 — Đóng gói .deb/.rpm + systemd (06/11/2026)

Nhánh `feature/linux`, máy build Ubuntu 18.04 (systemd 237, có `dpkg-deb`, **không có** `rpmbuild`), .NET SDK 10.0.403.
Kế hoạch: `docs/ke-hoach-trien-khai-linux.md` mục 5.4 (bố cục + hardening), mục 7 (GĐ4), quyết định D4–D7, rủi ro R3.

## 1. Kết quả

| Việc | Trạng thái |
|---|---|
| Bản .NET tự chứa linux-x64 (không cần cài .NET trên máy đích) | ✅ |
| Gói `.deb` (`dist/dlp-service_0.4.0_amd64.deb`, 29 MB) | ✅ build được, đã kiểm nội dung + chạy thử chương trình trong gói |
| Gói `.rpm` | ⚠️ spec viết xong, **chưa build** — máy không có `rpmbuild` |
| systemd unit bản mới (≥ 232) + bản máy cũ (219–231) | ✅ mọi thiết lập được systemd 237 nhận (kiểm bằng `systemd --dump-configuration-items`) |
| Script cài/gỡ dùng chung `dlp-setup.sh` | ✅ chạy thử (DRYRUN) cả nhánh systemd mới lẫn 219 |
| **Cài thật + kiểm hardening** (tiêu chí "Đạt" GĐ4) | ✅ **34/34 PASS** trên Ubuntu 18.04 / systemd 237 (`test-install.sh`, người dùng chạy bằng sudo — mục 5) |
| Build solution / test | 0 lỗi, 0 cảnh báo / 233/233 |

**Quyết định mặc định đã dùng** (chưa có ý kiến người dùng): D9 = chỉ build **net10** (chưa làm net6 cho Ubuntu 16.04 / CentOS 7 —
.NET 10 không hỗ trợ CentOS 7, glibc 2.17). Gói `.deb` đòi `libc6 >= 2.27`.

## 2. Cấu trúc

```
packaging/linux/
├── build-packages.sh            publish tự chứa → dựng cây thư mục → dpkg-deb (+ rpmbuild nếu có) → dist/
├── dlp-setup.sh                 MỌI việc cài/gỡ (configure | stop | remove | purge); DLP_SETUP_DRYRUN=1 để chạy thử
├── deb/  postinst prerm postrm control.in conffiles
├── rpm/  dlp-service.spec.in
├── systemd/  dlp-service.service (systemd ≥ 232)  ·  dlp-service-legacy.service (219–231)
├── etc/  appsettings.json (→ /etc/dlp-service/)  ·  sysctl-60-dlp-service.conf (→ /etc/sysctl.d/)
├── bin/dlp-service              /usr/bin/dlp-service — chỉ cho "sudo dlp-service explain <id>"
├── README                       → /usr/share/doc/dlp-service/README
└── test-install.sh              kiểm thử cài thật (cần root)
```

Bố cục trên máy đích (mục 5.4):

| Đường dẫn | Nội dung | Quyền |
|---|---|---|
| `/opt/dlp-service/` | chương trình tự chứa (file chạy `dlp-service`) | root, 0755 / 0644 |
| `/etc/dlp-service/appsettings.json` | cấu hình máy — **conffile** (nâng cấp không ghi đè chỗ admin sửa) | root:dlp 0640 |
| `/etc/dlp-service/rules/` | rule — **không** phải conffile (cập nhật rule = cập nhật gói, D7) | root:dlp 0750/0640 |
| `/var/lib/dlp-service/` | DB | dlp 0700 |
| `/var/log/dlp-service/` | log | dlp 0700 |
| `/etc/sysctl.d/60-dlp-service.conf` | `fs.inotify.max_user_watches = 524288` | root 0644 |
| `/lib/systemd/system/dlp-service.service` | unit đã chọn theo phiên bản systemd (do `dlp-setup.sh` chép) | root 0644 |

Mặc định theo dõi `/home/*/Documents`, `/home/*/Desktop`, `/home/*/Downloads`.

## 3. Hardening systemd

| Nhóm | Bản mới (≥ 232) | Bản máy cũ (219–231) |
|---|---|---|
| Quyền (D5, R3) | `User=dlp`, `NoNewPrivileges`, `CapabilityBoundingSet` = `AmbientCapabilities` = `CAP_DAC_READ_SEARCH` | `User=dlp`, `CapabilityBoundingSet=CAP_DAC_READ_SEARCH`, **setcap** trên file chạy (không bật `NoNewPrivileges` vì nó chặn capability từ file) |
| File | `ProtectSystem=strict`, `ProtectHome=read-only`, `ReadWritePaths` = DB + log, `PrivateTmp`, `PrivateDevices`, `ProtectKernel*`, `ProtectControlGroups`, `UMask=0077` | `ProtectSystem=full`, `ProtectHome=read-only`, `ReadWriteDirectories`, `PrivateTmp`, `PrivateDevices`, `UMask=0077` |
| Mạng | `PrivateNetwork=yes`, `RestrictAddressFamilies=AF_UNIX` | giống |
| Khác | `RestrictRealtime`, `RestrictNamespaces`, `LockPersonality`, `DOTNET_EnableDiagnostics=0` | `DOTNET_EnableDiagnostics=0` |
| Tài nguyên (D6) | `Nice=10`, `IOSchedulingClass=idle`, `CPUQuota=30%`, `CPUWeight=20`, `MemoryMax=512M` | `CPUShares=205`, `MemoryLimit=512M` |
| Vòng đời | `Type=notify` (AddSystemd báo READY), `Restart=on-failure`, `RestartSec=5` | giống |

Cố ý KHÔNG bật: `MemoryDenyWriteExecute` (JIT của .NET cần), `SystemCallFilter=@system-service` (nhóm này có từ systemd 239 — bản 237 lỗi).

`DOTNET_EnableDiagnostics=0` (thêm ngoài kế hoạch): .NET mặc định mở socket chẩn đoán cho phép chương trình **cùng user** đọc bộ nhớ /
dump tiến trình — bộ nhớ có nội dung file vừa quét. Đã kiểm: bật → 1 socket `dotnet-diagnostic`, tắt → 0.

## 4. Đã kiểm trên máy dev (không cần root)

| Kiểm | Kết quả |
|---|---|
| Cú pháp mọi script (`sh -n`, `bash -n`) | ✅ |
| `dlp-setup.sh` DRYRUN: configure (systemd 237 → unit mới; giả lập 219 → unit cũ + setcap), stop, remove, purge | ✅ thứ tự lệnh đúng |
| `dpkg-deb --info/--contents`: metadata, phụ thuộc, mọi file thuộc root, cấu hình 0640, không mang `confidence/` (R11), rule ở `/etc` | ✅ |
| `libe_sqlite3.so` trong gói là bản tự biên dịch (glibc 2.27) | ✅ (sửa `Directory.Build.targets` — trước đó publish lấy bản NuGet cần glibc 2.34) |
| Mọi thiết lập của 2 unit có trong danh sách systemd 237 biết | ✅ (`systemd-analyze verify` cần root trên 237 — đưa vào test-install.sh) |
| Giải nén gói vào gốc giả, chạy `/opt/dlp-service/dlp-service` **không có .NET trong PATH**, chế độ System: nạp rule từ `/etc`, cấu hình `/etc`, quét file, `explain` | ✅ |

## 5. Việc cần người dùng chạy (root)

```
sudo packaging/linux/test-install.sh dist/dlp-service_0.4.0_amd64.deb
```

Script (chạy ở terminal thật, ghi `./dlp-package-test-<thời gian>.log`) kiểm đủ tiêu chí "Đạt" GĐ4 rồi **tự purge**:
cài · user dlp · active/enabled/READY · chạy dưới dlp · quyền 0700/0750/0640 · DB + log 0600 (UMask) · `CapEff` chỉ có `cap_dac_read_search` ·
không có mạng · đọc được file 0600 của user khác · không ghi được `/etc` · cổng chẩn đoán tắt · `systemd-analyze verify` (+ `security` nếu
systemd ≥ 240) · user thường không `systemctl stop` được · `kill -9` → tự khởi động lại + `integrity_check` = ok · `explain` (root được, user
thường bị từ chối, không để lại file của root) · sysctl · cài lại giữ DB + cấu hình · remove giữ dữ liệu · purge xoá sạch.
Không quét `Documents` thật: tạo sẵn `/etc/dlp-service/appsettings.json` chỉ trỏ vào `/home/dlp-test-*/Documents` (cài với `--force-confold`).
Lưu ý: giá trị sysctl đã nâng còn giữ tới lần khởi động lại.

### Kết quả lần chạy 06/11/2026 20:50 (Ubuntu 18.04.6, kernel 4.15, systemd 237): **34 PASS, 0 FAIL**

| Nhóm | Kết quả |
|---|---|
| Cài | apt install · user dlp (nologin) · active + enabled · READY (Type=notify) · chạy dưới dlp |
| Quyền | `/var/lib`, `/var/log` 700 dlp · `/etc/dlp-service` 750 root:dlp · rule 640 · DB 600 · log 600 (UMask) |
| Hardening | `CapEff` chỉ `cap_dac_read_search` · không mạng ngoài lo · đọc được file 0600 của user khác · không ghi được `/etc` · cổng chẩn đoán tắt · `CPUQuota` 300 ms/s · `MemoryMax` 512 MiB · Nice 10 · IO idle |
| An toàn | user thường không `systemctl stop` được · `kill -9` → PID mới trong 30 s, `integrity_check` = ok |
| Chẩn đoán | `sudo dlp-service explain 1` được · user thường bị từ chối · không để lại file root trong `/var/lib` |
| Vòng đời | sysctl ≥ 524288 · cài lại giữ DB + cấu hình admin · remove giữ dữ liệu + user · purge xoá sạch `/var/lib`, `/var/log`, `/etc`, `/opt`, user + group |

Ghi chú: (1) `systemd-analyze verify` trên 237 in "Attempted to remove disk file system, and we can't allow that." — thông báo của chính
công cụ verify, không có lỗi thiết lập nào được báo và service chạy đúng mọi thiết lập; chưa tra được nguyên nhân cụ thể. (2) Lỗi
`chmod/chown: cannot access` đầu log là lỗi của script test (đổi quyền file log trước khi `tee` tạo file) — đã sửa.
`systemd-analyze security` chưa có trên 237 (cần ≥ 240) → kiểm theo danh sách thiết lập (mục 3), đúng như kế hoạch cho phép.

## 6. Lệch so với kế hoạch

| Kế hoạch | Thực tế | Lý do |
|---|---|---|
| logrotate | **Không dùng** | FileLogger đã tự xoay vòng (5 MB/phần, xoá sau 7 ngày) — logrotate đè lên sẽ đụng nhau |
| Chọn unit theo `systemctl --version` | Làm lúc cài (`dlp-setup.sh` chép unit đã chọn) thay vì trong gói | 1 gói dùng cho mọi phiên bản systemd |
| `.rpm` | Chưa build | Thiếu `rpmbuild` (cần `sudo apt install rpm` hoặc máy CentOS) |

## 7. Còn lại

| # | Việc |
|---|---|
| 1 | ~~Chạy `test-install.sh`~~ — xong, 34/34 PASS. Nên chạy thêm trên Ubuntu 22.04/24.04 (systemd ≥ 240 có `systemd-analyze security`) |
| 2 | Build + thử `.rpm` trên máy có `rpmbuild`; thử unit cũ trên systemd 219 thật (CentOS 7) — thuộc GĐ6 nếu không có máy |
| 3 | **D9**: hỗ trợ CentOS 7 / Ubuntu 16.04 cần bản net6 (Core đang dùng `Lock` .NET 9+). Không làm D9 → nên sửa yêu cầu thành Ubuntu 18.04+ / RHEL 8+ |
| 4 | Gói build trên máy KHÔNG có SQLite tự biên dịch (VD CI ubuntu-latest) sẽ mang bản NuGet (glibc ≥ 2.34) → không chạy trên 18.04. Cần bước build SQLite trong CI hoặc build gói trên máy cũ |
| 5 | Chạy lại test trên Windows (tồn đọng GĐ3) |
