#!/bin/sh
# [Thay đổi] File mới (GĐ4): MỌI việc cài/gỡ của gói .deb và .rpm dồn vào 1 script — script của gói (postinst/prerm/postrm, %post/%preun/%postun)
# chỉ gọi script này → 2 loại gói làm giống hệt nhau, sửa 1 chỗ. Cài vào /usr/lib/dlp-service/dlp-setup.sh.
#
#   dlp-setup.sh configure   sau khi giải nén gói (cài mới HOẶC nâng cấp): user dlp, thư mục + quyền, chọn unit systemd, capability,
#                            sysctl, bật + (khởi động lại) service
#   dlp-setup.sh stop        trước khi gỡ: dừng + tắt tự khởi động
#   dlp-setup.sh remove      sau khi gỡ: xoá unit đã chép, daemon-reload. GIỮ dữ liệu (/var/lib, /var/log) và user dlp
#   dlp-setup.sh purge       gỡ sạch: thêm xoá /var/lib/dlp-service, /var/log/dlp-service, /etc/dlp-service, user + group dlp
#
# DLP_SETUP_DRYRUN=1 → chỉ IN các lệnh sẽ chạy (kiểm tra script trên máy dev không có sudo). Script phải là /bin/sh thuần (dash trên Ubuntu).
set -e

APP_DIR=/opt/dlp-service
BIN="$APP_DIR/dlp-service"
CONF_DIR=/etc/dlp-service
DATA_DIR=/var/lib/dlp-service
LOG_DIR=/var/log/dlp-service
SHARE_DIR=/usr/lib/dlp-service
SYSCTL_FILE=/etc/sysctl.d/60-dlp-service.conf
USER_NAME=dlp
UNIT=dlp-service.service
MODERN_MIN_SYSTEMD=232   # ProtectSystem=strict; thấp hơn → dlp-service-legacy.service

run() {
  if [ "${DLP_SETUP_DRYRUN:-0}" = 1 ]; then
    echo "+ $*"
  else
    "$@"
  fi
}

log() { echo "dlp-setup: $*"; }

# Thư mục unit của hệ thống: Debian/Ubuntu /lib/systemd/system, RHEL/CentOS /usr/lib/systemd/system
unit_dir() {
  if [ -d /lib/systemd/system ] && [ ! -L /lib ]; then echo /lib/systemd/system; else echo /usr/lib/systemd/system; fi
}

systemd_version() {
  # DLP_SETUP_SYSTEMD_VERSION: chỉ để thử nhánh máy cũ khi chạy thử (DRYRUN) trên máy dev
  if [ -n "${DLP_SETUP_SYSTEMD_VERSION:-}" ]; then echo "$DLP_SETUP_SYSTEMD_VERSION"; return; fi
  systemctl --version 2>/dev/null | awk 'NR==1 {print $2+0}'
}

nologin_shell() {
  for s in /usr/sbin/nologin /sbin/nologin; do [ -x "$s" ] && { echo "$s"; return; }; done
  echo /bin/false
}

has_systemd() { [ -d /run/systemd/system ]; }

ensure_user() {
  if ! getent group "$USER_NAME" >/dev/null 2>&1; then
    run groupadd --system "$USER_NAME"
  fi
  if ! getent passwd "$USER_NAME" >/dev/null 2>&1; then
    # User hệ thống: không đăng nhập được, home = thư mục dữ liệu (không tạo), chỉ thuộc group dlp
    run useradd --system --gid "$USER_NAME" --home-dir "$DATA_DIR" --no-create-home --shell "$(nologin_shell)" \
      --comment "DLP Data Classification Service" "$USER_NAME"
  fi
}

ensure_dirs() {
  # Dữ liệu + log: chỉ dlp (0700) — DB và log chứa đường dẫn file của mọi người dùng (R7). Service tự siết lại 0700/0600 khi chạy.
  run install -d -o "$USER_NAME" -g "$USER_NAME" -m 0700 "$DATA_DIR" "$LOG_DIR"
  # Cấu hình + rule: root sở hữu (service không sửa được), group dlp đọc được, người khác không đọc được
  run chown -R root:"$USER_NAME" "$CONF_DIR"
  run chmod 0750 "$CONF_DIR"
  if [ "${DLP_SETUP_DRYRUN:-0}" = 1 ]; then
    echo "+ find $CONF_DIR -type d -exec chmod 0750 {} + ; find $CONF_DIR -type f -exec chmod 0640 {} +"
  else
    find "$CONF_DIR" -type d -exec chmod 0750 {} +
    find "$CONF_DIR" -type f -exec chmod 0640 {} +
  fi
}

install_unit() {
  ver="$(systemd_version)"
  dir="$(unit_dir)"
  if [ -n "$ver" ] && [ "$ver" -ge "$MODERN_MIN_SYSTEMD" ]; then
    src="$SHARE_DIR/systemd/dlp-service.service"
    log "systemd $ver → unit đầy đủ (AmbientCapabilities, ProtectSystem=strict)"
    # Bản mới dùng AmbientCapabilities → gỡ capability trên file chạy nếu còn sót từ lần cài bản cũ
    if command -v setcap >/dev/null 2>&1; then run setcap -r "$BIN" 2>/dev/null || true; fi
  else
    src="$SHARE_DIR/systemd/dlp-service-legacy.service"
    log "systemd ${ver:-?} (< $MODERN_MIN_SYSTEMD) → unit cho máy cũ + setcap trên file chạy"
    if ! command -v setcap >/dev/null 2>&1; then
      log "LỖI: thiếu lệnh setcap (Debian/Ubuntu: libcap2-bin; CentOS: libcap) — service sẽ không đọc được file của người dùng"
      exit 1
    fi
    run setcap cap_dac_read_search+ep "$BIN"
  fi
  run install -m 0644 "$src" "$dir/$UNIT"
}

apply_sysctl() {
  [ -f "$SYSCTL_FILE" ] || return 0
  want="$(awk -F= '/max_user_watches/ {gsub(/ /,"",$2); print $2}' "$SYSCTL_FILE")"
  cur="$(cat /proc/sys/fs/inotify/max_user_watches 2>/dev/null || echo 0)"
  # Chỉ NÂNG, không hạ giá trị admin đã đặt cao hơn
  if [ -n "$want" ] && [ "$cur" -lt "$want" ]; then
    log "fs.inotify.max_user_watches $cur → $want"
    run sysctl -q -w "fs.inotify.max_user_watches=$want" || log "cảnh báo: không đặt được sysctl (container?) — sẽ có hiệu lực sau khi khởi động lại"
  fi
}

case "${1:-}" in
  configure)
    ensure_user
    ensure_dirs
    install_unit
    apply_sysctl
    if has_systemd; then
      run systemctl daemon-reload
      run systemctl enable "$UNIT"
      # Cài mới: start; nâng cấp: restart để chạy bản mới (restart cũng start nếu đang dừng)
      run systemctl restart "$UNIT"
      log "đã bật và khởi động $UNIT"
    else
      log "không thấy systemd đang chạy (container/chroot?) — chưa khởi động service"
    fi
    ;;
  stop)
    if has_systemd; then
      run systemctl stop "$UNIT" || true
      run systemctl disable "$UNIT" || true
    fi
    ;;
  remove|purge)
    run rm -f "$(unit_dir)/$UNIT"
    if has_systemd; then run systemctl daemon-reload || true; fi
    if [ "$1" = purge ]; then
      run rm -rf "$DATA_DIR" "$LOG_DIR" "$CONF_DIR"
      if getent passwd "$USER_NAME" >/dev/null 2>&1; then run userdel "$USER_NAME" || true; fi
      if getent group "$USER_NAME" >/dev/null 2>&1; then run groupdel "$USER_NAME" || true; fi
      log "đã xoá dữ liệu, cấu hình và user $USER_NAME"
    else
      log "đã gỡ service; GIỮ dữ liệu $DATA_DIR, log $LOG_DIR, cấu hình $CONF_DIR (gỡ sạch: apt purge dlp-service)"
    fi
    ;;
  *)
    echo "Cách dùng: $0 configure|stop|remove|purge" >&2
    exit 2
    ;;
esac
