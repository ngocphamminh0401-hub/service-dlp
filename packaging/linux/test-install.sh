#!/bin/bash
# [Thay đổi] File mới (GĐ4): kiểm thử CÀI THẬT gói .deb trên máy Ubuntu/Debian — tiêu chí "Đạt" GĐ4 (ke-hoach-trien-khai-linux.md mục 7).
# CẦN ROOT và SẼ ĐỔI MÁY (cài gói, tạo user dlp, nâng sysctl) → chạy trên máy thử. Cuối cùng tự "apt purge" để gỡ sạch
# (riêng giá trị sysctl đã nâng vẫn giữ tới lần khởi động lại).
#
#   sudo packaging/linux/test-install.sh dist/dlp-service_0.4.0_amd64.deb
#
# Để KHÔNG quét thư mục thật của người dùng trên máy: tạo sẵn /etc/dlp-service/appsettings.json chỉ trỏ vào /home/dlp-test-*/Documents
# rồi cài với --force-confold (giữ file có sẵn). Kết quả ghi vào ./dlp-package-test-<thời gian>.log (đọc được không cần root).
set -u

DEB="${1:?Cách dùng: sudo $0 <đường dẫn file .deb>}"
DEB="$(readlink -f "$DEB")"
OUT="$(pwd)/dlp-package-test-$(date +%Y%m%d-%H%M%S).log"
TEST_HOME=/home/dlp-test-user
PASS=0; FAIL=0

[ "$(id -u)" -eq 0 ] || { echo "Cần chạy bằng sudo"; exit 1; }
[ -f "$DEB" ] || { echo "Không thấy $DEB"; exit 1; }

# [Thay đổi] Sửa sau lần chạy đầu: tạo file log + đổi chủ sở hữu TRƯỚC khi tee ghi vào (trước đây chmod/chown chạy trước khi tee kịp tạo file
# → lỗi "cannot access", file log thuộc root)
: > "$OUT"; chmod 0644 "$OUT"; [ -n "${SUDO_USER:-}" ] && chown "$SUDO_USER" "$OUT"
exec > >(tee -a "$OUT") 2>&1

check() { # check "<tên>" <lệnh...>
  local name="$1"; shift
  if "$@" >/dev/null 2>&1; then echo "  [PASS] $name"; PASS=$((PASS+1)); else echo "  [FAIL] $name"; FAIL=$((FAIL+1)); fi
}
wait_for() { # wait_for <giây> <lệnh...>
  local t="$1"; shift
  for _ in $(seq "$t"); do "$@" >/dev/null 2>&1 && return 0; sleep 1; done; return 1
}
mainpid() { systemctl show -p MainPID --value dlp-service; }
logfile() { ls -t /var/log/dlp-service/*.log 2>/dev/null | head -1; }

echo "== Máy: $(. /etc/os-release; echo "$PRETTY_NAME") · kernel $(uname -r) · $(systemctl --version | head -1)"
echo "== Gói: $DEB"

echo "== 0. Chuẩn bị (cấu hình thử, thư mục người dùng thử 0700 của nobody)"
install -d -m 0755 /etc/dlp-service
cat > /etc/dlp-service/appsettings.json <<'EOF'
{ "Watch": { "Folders": [ "/home/dlp-test-*/Documents" ], "MetricsIntervalSeconds": 10, "PatternRefreshSeconds": 5 } }
EOF
install -d -o nobody -m 0700 "$TEST_HOME" "$TEST_HOME/Documents"
printf 'Hồ sơ nhân viên\nSố CCCD: 001099012345\n' > "$TEST_HOME/Documents/ho-so.txt"
chown nobody "$TEST_HOME/Documents/ho-so.txt"; chmod 0600 "$TEST_HOME/Documents/ho-so.txt"

echo "== 1. Cài"
check "apt install" env DEBIAN_FRONTEND=noninteractive apt-get install -y -o Dpkg::Options::=--force-confold "$DEB"
check "user dlp tồn tại, không đăng nhập được" sh -c 'getent passwd dlp | grep -Eq "nologin|false"'
check "service active + enabled" sh -c 'systemctl is-active -q dlp-service && systemctl is-enabled -q dlp-service'
check "Type=notify đã báo READY (ActiveState=active, SubState=running)" sh -c '[ "$(systemctl show -p SubState --value dlp-service)" = running ]'
check "chạy dưới user dlp" sh -c '[ "$(ps -o user= -p '"$(mainpid)"')" = dlp ]'

echo "== 2. Quyền thư mục"
check "/var/lib/dlp-service 700 dlp:dlp" sh -c '[ "$(stat -c %a:%U:%G /var/lib/dlp-service)" = 700:dlp:dlp ]'
check "/var/log/dlp-service 700 dlp:dlp" sh -c '[ "$(stat -c %a:%U:%G /var/log/dlp-service)" = 700:dlp:dlp ]'
check "/etc/dlp-service 750 root:dlp" sh -c '[ "$(stat -c %a:%U:%G /etc/dlp-service)" = 750:root:dlp ]'
check "rule 640 root:dlp" sh -c '[ -z "$(find /etc/dlp-service/rules -type f ! -perm 0640)" ] && [ -z "$(find /etc/dlp-service -! -group dlp)" ]'
wait_for 30 test -f /var/lib/dlp-service/dlp.db
check "DB 600 (UMask=0077)" sh -c '[ "$(stat -c %a /var/lib/dlp-service/dlp.db)" = 600 ]'
check "file log 600 (UMask=0077)" sh -c '[ "$(stat -c %a "$(ls -t /var/log/dlp-service/*.log | head -1)")" = 600 ]'

echo "== 3. Hardening systemd"
systemctl show dlp-service -p User -p NoNewPrivileges -p AmbientCapabilities -p CapabilityBoundingSet -p ProtectSystem -p ProtectHome \
  -p PrivateNetwork -p PrivateTmp -p RestrictAddressFamilies -p CPUQuotaPerSecUSec -p MemoryMax -p MemoryLimit -p Nice -p IOSchedulingClass -p UMask | sed 's/^/     /'
PID="$(mainpid)"
check "chỉ có quyền cap_dac_read_search (CapEff)" sh -c 'grep -q "^CapEff:[[:space:]]*0000000000000004$" /proc/'"$PID"'/status'
check "không có mạng ngoài loopback (PrivateNetwork)" sh -c '[ "$(tail -n +3 /proc/'"$PID"'/net/dev | grep -vc "^ *lo:")" = 0 ]'
check "đọc được file 0600 của người khác ($TEST_HOME, CAP_DAC_READ_SEARCH)" wait_for 60 sh -c 'grep -q "$0/Documents/ho-so.txt: phát hiện" "$(ls -t /var/log/dlp-service/*.log | head -1)"' "$TEST_HOME"
check "không ghi được ra ngoài DB/log (ProtectSystem)" sh -c 'nsenter -t '"$PID"' -m sh -c "touch /etc/dlp-thu-ghi" 2>/dev/null; [ ! -e /etc/dlp-thu-ghi ]'
rm -f /etc/dlp-thu-ghi
# /proc/<PID>/net/unix = socket trong namespace mạng RIÊNG của service (PrivateNetwork) → không lẫn socket của chương trình .NET khác trên máy
check "cổng chẩn đoán .NET đã tắt (DOTNET_EnableDiagnostics=0)" sh -c '! grep -q dotnet-diagnostic /proc/'"$PID"'/net/unix'
if systemd-analyze --help 2>/dev/null | grep -q security; then
  echo "     systemd-analyze security:"; systemd-analyze security dlp-service 2>/dev/null | tail -1 | sed 's/^/     /'
fi
systemd-analyze verify /lib/systemd/system/dlp-service.service 2>&1 | sed 's/^/     verify: /' | head -5

echo "== 4. Người dùng thường không dừng được service"
runuser -u nobody -- systemctl stop dlp-service >/dev/null 2>&1
check "nobody: systemctl stop bị từ chối, service vẫn chạy" systemctl is-active -q dlp-service

echo "== 5. kill -9 → tự khởi động lại, DB không hỏng"
OLD="$(mainpid)"; kill -9 "$OLD"
check "khởi động lại với PID mới trong 30 s" wait_for 30 sh -c 'p=$(systemctl show -p MainPID --value dlp-service); [ "$p" != 0 ] && [ "$p" != '"$OLD"' ] && systemctl is-active -q dlp-service'
check "DB integrity_check = ok" sh -c 'runuser -u dlp -- python3 -c "import sqlite3;c=sqlite3.connect(\"/var/lib/dlp-service/dlp.db\");import sys;sys.exit(0 if c.execute(\"PRAGMA integrity_check\").fetchone()[0]==\"ok\" else 1)"'

echo "== 6. Lệnh chẩn đoán"
check "sudo dlp-service explain 1" sh -c 'dlp-service explain 1 | grep -q "file_id"'
check "user thường chạy explain bị từ chối rõ ràng" sh -c '! runuser -u nobody -- dlp-service explain 1'
check "explain không để lại file của root trong /var/lib/dlp-service" sh -c '[ -z "$(find /var/lib/dlp-service -user root)" ]'

echo "== 7. sysctl"
check "fs.inotify.max_user_watches >= 524288" sh -c '[ "$(cat /proc/sys/fs/inotify/max_user_watches)" -ge 524288 ]'

echo "== 8. Nâng cấp (cài lại) giữ dữ liệu + cấu hình"
SIZE_BEFORE="$(stat -c %s /var/lib/dlp-service/dlp.db)"
check "cài lại gói" env DEBIAN_FRONTEND=noninteractive apt-get install -y --reinstall -o Dpkg::Options::=--force-confold "$DEB"
check "service chạy sau nâng cấp" wait_for 30 systemctl is-active -q dlp-service
check "DB còn nguyên" test -s /var/lib/dlp-service/dlp.db
check "cấu hình admin không bị ghi đè" grep -q dlp-test /etc/dlp-service/appsettings.json
echo "     (DB trước: $SIZE_BEFORE byte)"

echo "== 9. Gỡ (remove) giữ dữ liệu"
check "apt remove" env DEBIAN_FRONTEND=noninteractive apt-get remove -y dlp-service
check "service không còn chạy" sh -c '! systemctl is-active -q dlp-service'
check "unit đã xoá" sh -c '[ ! -e /lib/systemd/system/dlp-service.service ] && [ ! -e /usr/lib/systemd/system/dlp-service.service ]'
check "dữ liệu + user dlp còn giữ" sh -c 'test -s /var/lib/dlp-service/dlp.db && getent passwd dlp'

echo "== 10. Gỡ sạch (purge)"
check "apt purge" env DEBIAN_FRONTEND=noninteractive apt-get purge -y dlp-service
check "đã xoá /var/lib, /var/log, /etc/dlp-service, /opt/dlp-service" sh -c '[ ! -e /var/lib/dlp-service ] && [ ! -e /var/log/dlp-service ] && [ ! -e /etc/dlp-service ] && [ ! -e /opt/dlp-service ]'
check "đã xoá user + group dlp" sh -c '! getent passwd dlp && ! getent group dlp'

rm -rf "$TEST_HOME"
echo "== KẾT QUẢ: $PASS PASS, $FAIL FAIL · log: $OUT"
[ "$FAIL" -eq 0 ]
