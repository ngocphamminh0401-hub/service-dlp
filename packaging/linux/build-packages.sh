#!/bin/bash
# [Thay đổi] File mới (GĐ4): build gói .deb (+ .rpm nếu máy có rpmbuild) cho bản Linux.
#
#   packaging/linux/build-packages.sh [phiên bản]      VD: packaging/linux/build-packages.sh 0.4.0
#   → dist/dlp-service_<ver>_amd64.deb, dist/dlp-service-<ver>-1.x86_64.rpm (nếu có rpmbuild)
#
# Không cần sudo: dpkg-deb --root-owner-group ghi chủ sở hữu root trong gói dù build bằng user thường.
# Lưu ý SQLite: trên máy build có ~/.local/lib/dlp-sqlite/libe_sqlite3.so (tự biên dịch, glibc thấp) thì bản đó được đóng gói
# (Directory.Build.targets) → chạy được cả máy cũ. Build trên máy KHÔNG có file đó → gói mang bản của NuGet (cần glibc >= 2.34).
set -euo pipefail

VERSION="${1:-0.4.0}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
DIST="$REPO/dist"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/dlp-pkg-XXXXXX")"
STAGE="$WORK/stage"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
MAINTAINER="$(git -C "$REPO" config user.name || echo DLP) <$(git -C "$REPO" config user.email || echo dlp@localhost)>"
trap 'rm -rf "$WORK"' EXIT

echo "== Publish bản tự chứa linux-x64 (phiên bản $VERSION)"
"$DOTNET" publish "$REPO/DlpService.Host.Linux" -c Release -r linux-x64 --self-contained \
  -p:Version="$VERSION" -o "$STAGE/opt/dlp-service" --nologo -v quiet

echo "== Dựng cây thư mục"
APP="$STAGE/opt/dlp-service"
# R11: bản Linux tắt FileConfidence → không mang model
rm -rf "$APP/confidence"
# Rule là CẤU HÌNH của máy → /etc/dlp-service/rules (LinuxAppLayout chế độ System), không để trong /opt
install -d "$STAGE/etc/dlp-service"
mv "$APP/rules" "$STAGE/etc/dlp-service/rules"
install -m 0644 "$HERE/etc/appsettings.json" "$STAGE/etc/dlp-service/appsettings.json"
install -D -m 0644 "$HERE/etc/sysctl-60-dlp-service.conf" "$STAGE/etc/sysctl.d/60-dlp-service.conf"
install -D -m 0755 "$HERE/bin/dlp-service" "$STAGE/usr/bin/dlp-service"
install -D -m 0755 "$HERE/dlp-setup.sh" "$STAGE/usr/lib/dlp-service/dlp-setup.sh"
install -D -m 0644 "$HERE/systemd/dlp-service.service" "$STAGE/usr/lib/dlp-service/systemd/dlp-service.service"
install -D -m 0644 "$HERE/systemd/dlp-service-legacy.service" "$STAGE/usr/lib/dlp-service/systemd/dlp-service-legacy.service"
install -D -m 0644 "$HERE/README" "$STAGE/usr/share/doc/dlp-service/README"
# Quyền chuẩn: thư mục 0755, file 0644, file chạy 0755. Cấu hình 0640 (dlp-setup.sh đổi group thành dlp lúc cài).
find "$STAGE" -type d -exec chmod 0755 {} +
find "$APP" -type f -exec chmod 0644 {} +
chmod 0755 "$APP/dlp-service" "$APP/createdump" "$STAGE/usr/bin/dlp-service" "$STAGE/usr/lib/dlp-service/dlp-setup.sh"
find "$APP" -name '*.so' -exec chmod 0755 {} +
find "$STAGE/etc/dlp-service" -type f -exec chmod 0640 {} +

mkdir -p "$DIST"

echo "== Gói .deb"
DEB="$WORK/deb"
cp -a "$STAGE" "$DEB"
install -d "$DEB/DEBIAN"
SIZE_KB="$(du -sk "$STAGE" | cut -f1)"
sed -e "s/@VERSION@/$VERSION/" -e "s/@SIZE_KB@/$SIZE_KB/" -e "s|@MAINTAINER@|$MAINTAINER|" "$HERE/deb/control.in" > "$DEB/DEBIAN/control"
install -m 0644 "$HERE/deb/conffiles" "$DEB/DEBIAN/conffiles"
for s in postinst prerm postrm; do install -m 0755 "$HERE/deb/$s" "$DEB/DEBIAN/$s"; done
DEB_OUT="$DIST/dlp-service_${VERSION}_amd64.deb"
dpkg-deb --root-owner-group -Zxz --build "$DEB" "$DEB_OUT" >/dev/null
echo "   $DEB_OUT ($(du -h "$DEB_OUT" | cut -f1))"

echo "== Gói .rpm"
if command -v rpmbuild >/dev/null 2>&1; then
  RPMTOP="$WORK/rpmbuild"
  mkdir -p "$RPMTOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
  sed -e "s/@VERSION@/$VERSION/" -e "s|@STAGE@|$STAGE|" "$HERE/rpm/dlp-service.spec.in" > "$RPMTOP/SPECS/dlp-service.spec"
  rpmbuild --define "_topdir $RPMTOP" -bb "$RPMTOP/SPECS/dlp-service.spec" >/dev/null
  cp "$RPMTOP"/RPMS/x86_64/*.rpm "$DIST/"
  echo "   $(ls "$DIST"/dlp-service-"$VERSION"-*.rpm)"
else
  echo "   BỎ QUA: máy này không có rpmbuild (Ubuntu: sudo apt install rpm; hoặc build trên máy CentOS/RHEL)"
fi
