#!/usr/bin/env bash
# [Thay đổi] File mới (Benchmark T00): checklist "máy đã sẵn sàng đo chưa" — chạy TRƯỚC mỗi buổi đo (T03–T10).
# Chỉ KIỂM TRA, không tự đổi gì và không bao giờ gọi sudo: mục nào chưa đạt thì in đúng lệnh để bạn tự chạy ở terminal riêng.
#   PASS = đạt · WARN = nên sửa, vẫn cho đo (ghi vào environment.md) · FAIL = không được đo (thoát mã 1)
#
# Cách dùng: tools/perf/bench-prep.sh [--need-gb 10] [--data-dir ~/dlp-benchdata] [--idle-seconds 5]
#   --need-gb: dung lượng trống tối thiểu ở ổ chứa dataset (bộ full + kết quả ~10 GB; bộ smoke đặt 1)
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
need_gb=10; data_dir="$HOME/dlp-benchdata"; idle_seconds=5
while [ $# -gt 0 ]; do
  case "$1" in
    --need-gb) need_gb="$2"; shift 2 ;;
    --data-dir) data_dir="$2"; shift 2 ;;
    --idle-seconds) idle_seconds="$2"; shift 2 ;;
    *) echo "Tham số lạ: $1" >&2; exit 1 ;;
  esac
done

fails=0; warns=0
fixes=()      # lệnh cần chạy để sửa (in ở cuối)
restores=()   # lệnh trả máy về như cũ sau khi đo xong
# Luôn return 0: nếu không, "[ ... ] && warn X || pass Y" sẽ chạy cả pass khi warn không có tham số thứ 2
pass() { printf '  \e[32mPASS\e[0m %s\n' "$1"; return 0; }
warn() { printf '  \e[33mWARN\e[0m %s\n' "$1"; warns=$((warns + 1)); [ -n "${2:-}" ] && fixes+=("$2"); return 0; }
fail() { printf '  \e[31mFAIL\e[0m %s\n' "$1"; fails=$((fails + 1)); [ -n "${2:-}" ] && fixes+=("$2"); return 0; }

echo "== 1. Nguồn nhiễu"
if [ "$(systemctl is-active dlp-service 2>/dev/null)" = active ]; then
  fail "dlp-service (bản đã cài) đang chạy — cùng quét thư mục, tranh CPU/đĩa" "sudo systemctl stop dlp-service"
  restores+=("sudo systemctl start dlp-service")
else
  pass "dlp-service không chạy"
fi
for s in unattended-upgrades apt-daily.timer apt-daily-upgrade.timer; do
  if [ "$(systemctl is-active "$s" 2>/dev/null)" = active ]; then
    fail "$s đang bật — apt có thể chạy nặng giữa lượt đo" "sudo systemctl stop $s"
    restores+=("sudo systemctl start $s")
  else
    pass "$s không chạy"
  fi
done
for s in cron atd; do
  if [ "$(systemctl is-active "$s" 2>/dev/null)" = active ]; then
    warn "$s đang chạy — job định kỳ có thể chen vào lượt đo dài (S4 60 phút)" "sudo systemctl stop $s"
    restores+=("sudo systemctl start $s")
  else
    pass "$s không chạy"
  fi
done
others=$(pgrep -f 'dotnet' | xargs -r ps -o pid=,args= -p | grep -v -E 'bench-prep|pgrep' || true)
if [ -n "$others" ]; then
  warn "còn $(echo "$others" | wc -l) tiến trình dotnet khác (MSBuild node giữ RAM sau khi build...)" "$dotnet build-server shutdown"
else
  pass "không có tiến trình dotnet nào khác"
fi

echo "== 2. Bộ nhớ"
swap_mb=$(free -m | awk '/^Swap:/ {print $2}')
if [ "$swap_mb" -gt 0 ]; then
  fail "swap đang bật (${swap_mb} MB) — RSS đo được có thể thấp hơn thật vì trang bị đẩy ra swap" "sudo swapoff -a"
  restores+=("sudo swapon -a")
else
  pass "swap tắt"
fi
ram_gb=$(free -m | awk '/^Mem:/ {printf "%.1f", $2/1024}')
avail_mb=$(free -m | awk '/^Mem:/ {print $7}')
awk -v r="$ram_gb" 'BEGIN {exit !(r >= 7.5)}' && pass "RAM ${ram_gb} GB" \
  || warn "RAM ${ram_gb} GB < 8 GB khuyến nghị — đủ cho service (trần 512 MB) nhưng tool đo + page cache dataset bị chật (nâng RAM của VM)"
[ "$avail_mb" -ge 2048 ] && pass "RAM khả dụng ${avail_mb} MB" || fail "RAM khả dụng chỉ ${avail_mb} MB (< 2 GB)" "đóng bớt chương trình / $dotnet build-server shutdown"

echo "== 3. CPU"
cpus=$(nproc)
[ "$cpus" -ge 4 ] && pass "$cpus CPU logic" || warn "$cpus CPU logic < 4 khuyến nghị — tool đo chạy chung nhân với service (nâng vCPU của VM)"
hv=$(lscpu 2>/dev/null | awk -F: '/Hypervisor vendor/ {gsub(/^ +/,"",$2); print $2}')
[ -n "$hv" ] && warn "chạy trong VM ($hv), không chỉnh được xung CPU — chỉ tin so sánh A/B xen kẽ trong cùng buổi" || pass "máy thật"
load1=$(cut -d' ' -f1 /proc/loadavg)
awk -v l="$load1" 'BEGIN {exit !(l < 0.10)}' && pass "load 1 phút = $load1" \
  || fail "load 1 phút = $load1 (≥ 0,10) — máy chưa rảnh" "chờ vài phút rồi chạy lại bench-prep.sh"
# Đo % CPU bận của cả máy trong idle_seconds giây (từ /proc/stat)
read -r _ u1 n1 s1 i1 w1 q1 sq1 st1 _ < /proc/stat
sleep "$idle_seconds"
read -r _ u2 n2 s2 i2 w2 q2 sq2 st2 _ < /proc/stat
busy=$(( (u2+n2+s2+q2+sq2+st2) - (u1+n1+s1+q1+sq1+st1) )); total=$(( busy + (i2+w2) - (i1+w1) ))
busy_pct=$(awk -v b="$busy" -v t="$total" 'BEGIN {printf "%.1f", (t > 0 ? 100*b/t : 0)}')
awk -v p="$busy_pct" 'BEGIN {exit !(p < 3)}' && pass "CPU bận ${busy_pct} % trong ${idle_seconds} s" \
  || fail "CPU bận ${busy_pct} % trong ${idle_seconds} s (≥ 3 %) — có chương trình khác đang chạy" "xem: ps -eo pid,pcpu,comm --sort=-pcpu | head"
steal=$(( st2 - st1 ))
[ "$steal" -gt 0 ] && warn "có CPU steal ($steal tick) — máy thật đang tranh CPU với VM (đóng app trên máy thật)" || pass "không có CPU steal"

echo "== 4. Đĩa"
mkdir -p "$data_dir" 2>/dev/null
free_gb=$(df -BG --output=avail "$data_dir" 2>/dev/null | tail -1 | tr -dc '0-9')
[ "${free_gb:-0}" -ge "$need_gb" ] && pass "trống ${free_gb} GB ở $data_dir" \
  || fail "chỉ trống ${free_gb:-?} GB ở $data_dir (cần ≥ $need_gb GB)" "thêm đĩa ảo cho VM rồi trỏ --data-dir / DlpBenchData --out vào đó"

echo "== 5. Công cụ"
[ -x "$dotnet" ] && pass ".NET SDK $("$dotnet" --version)" || fail "không thấy $dotnet" "cài .NET 10 SDK vào ~/.dotnet"
for t in dotnet-counters dotnet-trace dotnet-gcdump; do
  (cd "$here" && "$dotnet" tool run "$t" --version >/dev/null 2>&1) && pass "$t" || fail "$t chưa restore" "cd $here && $dotnet tool restore"
done
for c in strace gawk; do
  command -v "$c" >/dev/null && pass "$c" || fail "thiếu $c" "sudo apt-get install $c"
done
command -v systemd-run >/dev/null && pass "systemd-run (cho profile P-prod, cần sudo)" || warn "không có systemd-run — không chạy được profile P-prod"
for p in DlpRuleInventory DlpBenchData DlpRegexBench DlpExtractorBench; do
  [ -f "$here/$p/bin/Release/net10.0/$p.dll" ] && pass "$p (Release) đã build" \
    || warn "$p chưa build Release" "$dotnet build $here/$p -c Release"
done

echo
if [ ${#fixes[@]} -gt 0 ]; then
  echo "Cần làm trước khi đo (lệnh sudo: chạy ở terminal riêng):"
  printf '  %s\n' "${fixes[@]}" | awk '!seen[$0]++'
fi
if [ ${#restores[@]} -gt 0 ]; then
  echo "Đo xong, trả máy về như cũ:"
  printf '  %s\n' "${restores[@]}"
fi
echo "Kết quả: $fails FAIL, $warns WARN"
[ "$fails" -eq 0 ] && { echo "→ SẴN SÀNG đo (ghi environment.md bằng tools/perf/collect-env.sh)"; exit 0; }
echo "→ CHƯA được đo"; exit 1
