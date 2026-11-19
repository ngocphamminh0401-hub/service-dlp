#!/usr/bin/env bash
# [Thay đổi] File mới (Benchmark T00): ghi environment.md — mô tả ĐẦY ĐỦ môi trường của 1 lượt đo, để Before/After chỉ được so khi trùng
# môi trường (kế hoạch §8). Chỉ ĐỌC hệ thống, không đổi gì. Không ghi dữ liệu người dùng (chỉ cấu hình, phiên bản, số đo tải máy).
#
# Cách dùng: tools/perf/collect-env.sh <thư mục kết quả> [--profile P-dev|P-prod] [--cache warm|cold] [--note "ghi chú"]
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"

out="${1:?Cần thư mục kết quả}"; shift || true
profile="P-dev"; cache="warm"; note=""
while [ $# -gt 0 ]; do
  case "$1" in
    --profile) profile="$2"; shift 2 ;;
    --cache) cache="$2"; shift 2 ;;
    --note) note="$2"; shift 2 ;;
    *) echo "Tham số lạ: $1" >&2; exit 1 ;;
  esac
done
mkdir -p "$out"
f="$out/environment.md"

kv() { printf '| %s | %s |\n' "$1" "$2"; }
val() { "$@" 2>/dev/null | head -1; }

# rules_ver: lấy đúng thuật toán RuleLoader.ComputeRulesVersion qua DlpRuleInventory (đã build Release)
rules_ver="(chưa build DlpRuleInventory)"
inv="$here/DlpRuleInventory/bin/Release/net10.0/DlpRuleInventory.dll"
if [ -f "$inv" ]; then
  tmpmd="$(mktemp)"
  "$dotnet" "$inv" --out "$tmpmd" >/dev/null 2>&1 && rules_ver="$(grep -o 'rules_ver: `[^`]*`' "$tmpmd" | tr -d '`' | cut -d' ' -f2)"
  rm -f "$tmpmd"
fi

# Lấy giá trị 1 khoá trong appsettings.json (file có chú thích // nên dùng grep, không parse JSON)
setting() { grep -E "\"$1\"[[:space:]]*:" "$repo/DlpService.Host.Linux/appsettings.json" | head -1 | sed -E 's/.*:[[:space:]]*//; s/,?[[:space:]]*$//'; }

dirty="sạch"
[ -n "$(git -C "$repo" status --porcelain --untracked-files=no 2>/dev/null)" ] && dirty="**CÓ thay đổi chưa commit** (số đo gắn với working tree, không chỉ commit)"

{
  echo "# Môi trường đo"
  echo
  echo "- Thời điểm: $(date '+%Y-%m-%d %H:%M:%S %z') · profile **$profile** · page cache **$cache**"
  [ -n "$note" ] && echo "- Ghi chú: $note"
  echo
  echo "## Máy"
  echo
  echo "| Mục | Giá trị |"
  echo "|---|---|"
  kv "Máy" "$(hostname)"
  kv "Hypervisor" "$(lscpu 2>/dev/null | awk -F: '/Hypervisor vendor/ {gsub(/^ +/,"",$2); print $2}' || true)$( [ -z "$(lscpu 2>/dev/null | grep Hypervisor)" ] && echo 'không (máy thật)')"
  kv "CPU" "$(lscpu 2>/dev/null | awk -F: '/Model name/ {gsub(/^ +/,"",$2); print $2}') · $(nproc) CPU logic · $(lscpu 2>/dev/null | awk -F: '/Thread\(s\) per core/ {gsub(/ /,"",$2); print $2}') luồng/nhân"
  kv "Điều chỉnh xung CPU" "$( [ -d /sys/devices/system/cpu/cpu0/cpufreq ] && cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor || echo 'không có cpufreq (VM / không chỉnh được trong máy khách)')"
  kv "RAM" "$(free -m | awk '/^Mem:/ {printf "%.1f GB tổng, %.1f GB khả dụng", $2/1024, $7/1024}')"
  kv "Swap" "$(free -m | awk '/^Swap:/ {if ($2==0) print "tắt"; else printf "%.1f GB (đang dùng %d MB)", $2/1024, $3}')"
  kv "Kernel / OS" "$(uname -r) · $(. /etc/os-release 2>/dev/null; echo "$PRETTY_NAME")"
  kv "cgroup" "$( [ "$(stat -fc %T /sys/fs/cgroup)" = cgroup2fs ] && echo v2 || echo 'v1 (giới hạn RAM dùng MemoryLimit, không phải MemoryMax)')"
  kv "Transparent huge pages" "$(cat /sys/kernel/mm/transparent_hugepage/enabled 2>/dev/null)"
  kv "Ổ chứa repo" "$(df -h "$repo" | awk 'NR==2 {print $1 " · trống " $4 " / " $2}') · $(stat -fc %T "$repo")"
  echo
  echo "## Phần mềm"
  echo
  echo "| Mục | Giá trị |"
  echo "|---|---|"
  kv ".NET SDK" "$(val "$dotnet" --version)"
  kv ".NET runtime" "$("$dotnet" --list-runtimes 2>/dev/null | awk '/Microsoft.NETCore.App/ {v=$2} END {print v}')"
  for t in dotnet-counters dotnet-trace dotnet-gcdump; do
    kv "$t" "$(cd "$here" && "$dotnet" tool run "$t" --version 2>/dev/null | head -1 | cut -d+ -f1 || echo 'chưa restore')"
  done
  kv "strace / gawk" "$(val strace -V | awk '{print $NF}') / $(val gawk --version | awk '{print $3}' | tr -d ,)"
  echo
  echo "## Mã nguồn & cấu hình"
  echo
  echo "| Mục | Giá trị |"
  echo "|---|---|"
  kv "Nhánh / commit" "$(git -C "$repo" rev-parse --abbrev-ref HEAD) / $(git -C "$repo" rev-parse --short=10 HEAD)"
  kv "Working tree" "$dirty"
  kv "rules_ver" "\`$rules_ver\`"
  kv "GC" "Workstation + concurrent (mặc định; runtimeconfig không đặt gì) — kiểm lại nếu đổi"
  for k in MaxConcurrency MaxPendingFiles MinMillisecondsBetweenFiles MaxSecondsPerFile MinConfidence ReconcileIntervalMinutes MetricsIntervalSeconds; do
    kv "appsettings $k" "$(setting "$k")"
  done
  env | grep -E '^(Watch|Scan|Storage|Llm|DOTNET)_' | sort | sed 's/^/| env | /; s/$/ |/'
  echo
  echo "## Trạng thái máy lúc ghi"
  echo
  echo "| Mục | Giá trị |"
  echo "|---|---|"
  kv "Uptime / load (1, 5, 15 phút)" "$(uptime -p) / $(cut -d' ' -f1-3 /proc/loadavg)"
  for s in dlp-service unattended-upgrades apt-daily.timer apt-daily-upgrade.timer cron atd; do
    kv "service $s" "$(systemctl is-active "$s" 2>/dev/null)"
  done
  kv "Tiến trình dotnet khác" "$(pgrep -fa dotnet | grep -v -E 'collect-env|pgrep' | wc -l)"
  echo
  echo "Top 5 tiến trình theo CPU:"
  echo
  echo '```'
  ps -eo pid,pcpu,rss,comm --sort=-pcpu | head -6
  echo '```'
} > "$f"
echo "Đã ghi $f"
