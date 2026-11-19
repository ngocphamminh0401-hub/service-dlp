#!/usr/bin/env bash
# [Thay đổi] File mới (Benchmark — buổi đo baseline): chạy TOÀN BỘ phép đo của kế hoạch theo thứ tự cố định, lưu kết quả vào 1 thư mục,
# rồi trả máy về như cũ. Chạy ở TERMINAL CỦA BẠN (không qua Claude Code — bớt nhiễu CPU), ~5–6 giờ, có thể để qua đêm.
#
#   tools/perf/run-baseline-session.sh [--dry-run] [--no-sudo] [--reps 3] [--only s1,s4] [--data /data] [--rawdata ~/dlp-rawdata]
#     --dry-run  bộ smoke + rút ngắn mọi pha (~10–15 phút) — kiểm script trước buổi thật
#     --no-sudo  không dừng service / không swapoff / P-prod chạy như P-dev (nhãn prod-sim) — CHỈ để chạy thử
#     --only     chỉ chạy các bước liệt kê (s1..s8)
#
# Các bước (kế hoạch docs/ke-hoach-benchmark-hieu-nang.md):
#   s1 T03 guard     DlpBaseline đủ rawdata × 2 + diff; fingerprint bộ full + 2.000 file rawdata × 2 + diff
#   s2 T04 regex     DlpRegexBench rules bộ full + 2.000 file rawdata (5 lượt, trần 10 s như service) + D-csv không trần (1 lượt)
#   s3 T05 extractor DlpExtractorBench stages bộ full + 200 file rawdata (5 lượt) + count-opens.sh mọi file bộ full
#   s4 T08 E2E ngắn  seq, rates, batches, burst, after, restart — P-dev + dotnet-counters, [Metric] 5 s — × reps
#   s5 T09 + T10     cache (× reps) · overflow 15.000 + mixed + restartburst · overflow với trần hàng chờ 500 (Q3)
#   s6 T08 P-prod    large, burst, after, idle-long 30 phút — systemd-run CPUQuota 30 %, MemoryLimit 512M (kiểm 3 chỉ tiêu)
#   s7 T08 S4        sustained 60 phút, 20 file/s, cứ 30 s 1 file lớn — P-dev + counters
#   s8 T11 trace     trace-hotspots.sh bộ full + E2E large với heap dump
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
export DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
export PATH="$(dirname "$DOTNET"):$PATH"
dotnet="$DOTNET"

dry=0; nosudo=0; reps=3; only=""; data="/data"; rawdata="$HOME/dlp-rawdata"
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run) dry=1; shift ;;
    --no-sudo) nosudo=1; shift ;;
    --reps) reps="$2"; shift 2 ;;
    --only) only="$2"; shift 2 ;;
    --data) data="$2"; shift 2 ;;
    --rawdata) rawdata="$2"; shift 2 ;;
    *) echo "Tham số lạ: $1" >&2; exit 1 ;;
  esac
done
[ "$dry" = 1 ] && [ "$reps" = 3 ] && reps=1

if [ "$dry" = 1 ]; then
  dataset="$HOME/dlp-benchdata/smoke"; [ -d "$data/dlp-benchdata/smoke" ] && dataset="$data/dlp-benchdata/smoke"
else
  dataset="$data/dlp-benchdata/full"
fi
[ -f "$dataset/manifest.csv" ] || { echo "Không thấy dataset $dataset (sinh bằng DlpBenchData gen)" >&2; exit 1; }
commit="$(git -C "$repo" rev-parse --short=7 HEAD)"
out="$data/bench-results/$(date +%Y%m%d-%H%M)-$commit$( [ "$dry" = 1 ] && echo -dryrun )"
mkdir -p "$out"
status="$out/status.md"
log() { echo "[$(date +%H:%M:%S)] $*" | tee -a "$out/session.log"; }

# ---------------------------------------------------------------- sudo + trả máy về như cũ ----------------------------------------------------------------
noise=(dlp-service unattended-upgrades apt-daily.timer apt-daily-upgrade.timer cron atd)
was_active=()
swap_was_on=0
keepalive_pid=""
restore() {
  local code=$?
  log "Trả máy về như cũ ..."
  [ -n "$keepalive_pid" ] && kill "$keepalive_pid" 2>/dev/null
  if [ "$nosudo" = 0 ]; then
    [ "$swap_was_on" = 1 ] && sudo -n swapon -a && log "  swapon -a"
    for s in "${was_active[@]}"; do sudo -n systemctl start "$s" && log "  start $s"; done
  fi
  log "Kết thúc (mã $code). Kết quả: $out"
}
trap restore EXIT
trap 'log "Bị ngắt (Ctrl+C / TERM)"; exit 130' INT TERM

{
  echo "# Buổi đo baseline — $(date '+%Y-%m-%d %H:%M')"
  echo
  echo "- Commit \`$commit\` · dataset \`$dataset\` (sha256 manifest \`$(sha256sum "$dataset/manifest.csv" | cut -c1-16)…\`) · reps $reps$( [ "$dry" = 1 ] && echo ' · **DRY-RUN**' )$( [ "$nosudo" = 1 ] && echo ' · **--no-sudo** (không dừng nguồn nhiễu, P-prod chạy như P-dev)' )"
  echo
  echo "| Bước | Mô tả | Mã thoát | Thời gian | Kết quả |"
  echo "|---|---|---|---|---|"
} > "$status"

if [ "$nosudo" = 0 ]; then
  log "Xác thực sudo (1 lần cho cả buổi) ..."
  sudo -v || { echo "Cần sudo" >&2; exit 1; }
  ( while kill -0 $$ 2>/dev/null; do sudo -n -v 2>/dev/null; sleep 60; done ) &
  keepalive_pid=$!
  for s in "${noise[@]}"; do
    if [ "$(systemctl is-active "$s" 2>/dev/null)" = active ]; then
      was_active+=("$s"); sudo -n systemctl stop "$s" && log "  stop $s"
    fi
  done
  if [ "$(free -m | awk '/^Swap:/ {print $2}')" -gt 0 ]; then
    swap_was_on=1; sudo -n swapoff -a && log "  swapoff -a"
  fi
fi

# ---------------------------------------------------------------- build + chờ máy rảnh ----------------------------------------------------------------
log "Build Release (service + công cụ đo) ..."
for p in DlpService.Host.Linux tools/eval/DlpBaseline tools/perf/DlpRuleInventory tools/perf/DlpBenchData tools/perf/DlpRegexBench tools/perf/DlpExtractorBench; do
  "$dotnet" build "$repo/$p" -c Release --nologo -v q >/dev/null 2>&1 || { log "BUILD LỖI: $p"; exit 1; }
done
"$dotnet" build-server shutdown >/dev/null 2>&1   # MSBuild node giữ RAM/CPU nền
(cd "$here" && "$dotnet" tool restore >/dev/null)

settle=$( [ "$dry" = 1 ] && echo 10 || echo 180 )
log "Chờ máy lắng $settle s ..."
sleep "$settle"
tries=$( [ "$dry" = 1 ] && echo 1 || echo 5 )  # dry-run: thường FAIL vì nguồn nhiễu còn chạy — không chờ
for i in $(seq 1 "$tries"); do
  if "$here/bench-prep.sh" --data-dir "$data/dlp-benchdata" --need-gb 3 > "$out/bench-prep.txt" 2>&1; then
    log "bench-prep: SẴN SÀNG"; break
  fi
  log "bench-prep chưa đạt (lần $i/$tries): $(grep -c FAIL "$out/bench-prep.txt") FAIL$( [ "$i" -lt "$tries" ] && echo " — chờ 60 s" )"
  [ "$i" = "$tries" ] && log "  ! vẫn chưa đạt sau $tries lần — VẪN ĐO, ghi cảnh báo vào status.md" && echo "| — | bench-prep chưa đạt khi bắt đầu đo (xem bench-prep.txt) | | | ⚠ |" >> "$status" && break
  sleep 60
done
"$here/collect-env.sh" "$out" --profile session --note "đầu buổi" >/dev/null

# ---------------------------------------------------------------- chạy 1 bước ----------------------------------------------------------------
want() { [ -z "$only" ] || [[ ",$only," == *",$1,"* ]]; }
# step <id> <mô tả> <timeout giây> <lệnh...>
step() {
  local id="$1" desc="$2" tmo="$3"; shift 3
  want "${id%%-*}" || return 0
  local dir="$out/$id"; mkdir -p "$dir"
  "$here/collect-env.sh" "$dir" --profile "$(case "$id" in s6*) [ "$nosudo" = 1 ] && echo prod-sim || echo P-prod ;; *) echo P-dev ;; esac)" >/dev/null
  log "== $id: $desc"
  local t0=$SECONDS
  # [Thay đổi] timeout chỉ chạy được CHƯƠNG TRÌNH, không gọi được hàm bash (guard/opens/e2e → mã 127 ở lần chạy thử đầu): chạy qua bash -c,
  # chuyển sang shell con mọi hàm (declare -f) + các biến cần (declare -p, giữ được cả mảng như BL_LIMIT)
  local ctx; ctx="$(declare -f); $(declare -p out dataset rawdata here repo dotnet data BL RX EX RAW_RX RAW_EX RUNS BL_LIMIT dry nosudo 2>/dev/null)"
  # [Thay đổi] Vòng 5 — lỗi tty của s4 (19/11/2026): "timeout" mặc định đặt lệnh vào NHÓM TIẾN TRÌNH NỀN; khi chạy trong terminal thật (có tty), tiến trình nào
  # chạm tty (service .NET / dotnet-counters) bị hệ điều hành DỪNG → s4 treo tới hết timeout (122 phút × 3 lượt), log service im lặng ngay sau khi khởi động.
  # Tái hiện bằng tty giả (script -qec) và sửa: --foreground (không tách nhóm tiến trình) + "< /dev/null" (tiến trình con không bao giờ đọc/đòi tty).
  timeout --foreground --signal=INT --kill-after=120 "$tmo" bash -c "$ctx"'; "$@"' _ "$@" < /dev/null > "$dir/run.log" 2>&1
  local code=$?
  local dur=$(( SECONDS - t0 ))
  local res; res=$(case $code in 0) echo "✅" ;; 124) echo "⏱ hết giờ" ;; *) echo "❌ xem $id/run.log" ;; esac)
  printf '| %s | %s | %s | %dm%02ds | %s |\n' "$id" "$desc" "$code" $((dur / 60)) $((dur % 60)) "$res" >> "$status"
  log "   → mã $code sau $((dur / 60)) phút $((dur % 60)) s"
}

e2e() { python3 "$here/do-e2e-linux.py" --no-build --work-dir "$data/e2e-work" --rawdata "$rawdata" --dataset "$dataset" "$@"; }

# Kích thước theo chế độ
if [ "$dry" = 1 ]; then
  BL_LIMIT=(--limit 100); RAW_RX=20; RAW_EX=10; RUNS=2
  E2E_SHORT=(--rates 5 --step-seconds 10 --batches 1,10,100 --burst-limit 300 --idle-after 10)
  CACHE_N=40; OVF_N=300; OVF3_N=300; OVF3_CAP=100; MIX_N=100; IDLE_S=60; SUST_S=60; SUST_RATE=5
  PROD_BURST=(--burst-limit 300 --idle-after 10)
else
  BL_LIMIT=(); RAW_RX=2000; RAW_EX=200; RUNS=5
  E2E_SHORT=()
  CACHE_N=500; OVF_N=15000; OVF3_N=5000; OVF3_CAP=500; MIX_N=1000; IDLE_S=1800; SUST_S=3600; SUST_RATE=20
  PROD_BURST=()
fi
BL="$repo/tools/eval/DlpBaseline/bin/Release/net10.0/DlpBaseline.dll"
RX="$here/DlpRegexBench/bin/Release/net10.0/DlpRegexBench.dll"
EX="$here/DlpExtractorBench/bin/Release/net10.0/DlpExtractorBench.dll"

# s1 — T03 guard: chạy 2 lần, diff phải bằng 0 (kết quả tất định); bản lần 1 là guard "Before"
guard() {
  local d="$out/s1-guard"
  "$dotnet" "$BL" "$rawdata" "${BL_LIMIT[@]}" --out "$d/bl1" && "$dotnet" "$BL" "$rawdata" "${BL_LIMIT[@]}" --out "$d/bl2" || return 1
  cp "$d"/bl1/baseline-*.csv "$d/classification.csv"
  python3 "$here/diff-classification.py" "$d"/bl1/baseline-*.csv "$d"/bl2/baseline-*.csv > "$d/diff-baseline.txt" || return 1
  "$dotnet" "$RX" fingerprint "$dataset" --rawdata "$rawdata" --raw-sample "$RAW_RX" --out "$d/fingerprint.csv" &&
  "$dotnet" "$RX" fingerprint "$dataset" --rawdata "$rawdata" --raw-sample "$RAW_RX" --out "$d/fingerprint-2.csv" || return 1
  python3 "$here/diff-classification.py" "$d/fingerprint.csv" "$d/fingerprint-2.csv" > "$d/diff-fingerprint.txt"
}
step s1-guard "T03 guard phân loại (DlpBaseline × 2 + fingerprint × 2)" 3600 guard

step s2-regex "T04 regex theo rule (bộ đủ + rawdata, $RUNS lượt, trần 10 s)" 7200 \
  "$dotnet" "$RX" rules "$dataset" --rawdata "$rawdata" --raw-sample "$RAW_RX" --runs "$RUNS" --out "$out/s2-regex"
step s2-regexcsv "T04 R3 D-csv KHÔNG trần (1 lượt, đo độ dốc thật)" 5400 \
  "$dotnet" "$RX" rules "$dataset" --sets D-csv --runs 1 --warmup 1 --max-scan-seconds 900 --out "$out/s2-regexcsv"

opens() {
  mkdir -p "$out/s3-extractor"
  local files=()
  while IFS=, read -r _ rel _; do files+=("$dataset/$rel"); done < <(tail -n +2 "$dataset/manifest.csv")
  "$here/count-opens.sh" "${files[@]}" > "$out/s3-extractor/count-opens.csv"
}
step s3-extractor "T05 extractor theo bước ($RUNS lượt)" 3600 \
  "$dotnet" "$EX" stages "$dataset" --rawdata "$rawdata" --raw-sample "$RAW_EX" --runs "$RUNS" --out "$out/s3-extractor"
step s3-opens "T05 F7 đếm open/stat/byte đọc (strace)" 1800 opens

for r in $(seq 1 "$reps"); do
  step "s4-e2e-r$r" "T08 E2E ngắn lượt $r/$reps (P-dev, counters)" 7200 \
    e2e --phases warmup,seq,rates,batches,burst,after,restart --metrics-seconds 5 --counters --label "short-r$r" --out "$out/s4-e2e-r$r" "${E2E_SHORT[@]}"
done

for r in $(seq 1 "$reps"); do
  step "s5-cache-r$r" "T09 cache C1–C5 lượt $r/$reps" 3600 \
    e2e --phases warmup,cache --debug-log --cache-count "$CACHE_N" --label "cache-r$r" --out "$out/s5-cache-r$r"
done
step s5-queue "T10 Q2 overflow $OVF_N + Q4 mixed + Q5 restartburst" 7200 \
  e2e --phases warmup,overflow,mixed,restartburst --metrics-seconds 5 --overflow-count "$OVF_N" --mixed-small "$MIX_N" --label queue --out "$out/s5-queue"
step s5-q3 "T10 Q3 trần hàng chờ $OVF3_CAP + $OVF3_N file" 3600 \
  e2e --phases warmup,overflow --metrics-seconds 5 --max-pending "$OVF3_CAP" --overflow-count "$OVF3_N" --label q3 --out "$out/s5-q3"

prod=(--prod-profile); [ "$nosudo" = 1 ] && prod=()
step s6-prod "T08 P-prod: large + burst + idle $((IDLE_S / 60)) phút (3 chỉ tiêu)" 14400 \
  e2e --phases warmup,large,burst,after,idle-long "${prod[@]}" --idle-long-seconds "$IDLE_S" --label "$( [ "$nosudo" = 1 ] && echo prod-sim || echo prod )" --out "$out/s6-prod" "${PROD_BURST[@]}"

step s7-sustained "T08 S4 sustained $((SUST_S / 60)) phút" $(( SUST_S + 3600 )) \
  e2e --phases warmup,sustained --sustained-seconds "$SUST_S" --sustained-rate "$SUST_RATE" --counters --label sustained --out "$out/s7-sustained"

step s8-trace "T11 trace hàm nóng (in-process)" 7200 \
  "$here/trace-hotspots.sh" "$dataset" "$out/s8-trace" $( [ "$dry" = 1 ] && echo --quick )
step s8-gcdump "T11 heap dump lúc large-all" 3600 \
  e2e --phases warmup,large --counters --gcdump-delay 3 --label gcdump --out "$out/s8-gcdump"

rm -rf "$data/e2e-work"
"$here/collect-env.sh" "$out/end" --profile session --note "cuối buổi" >/dev/null 2>&1  # thư mục riêng — không ghi đè environment.md đầu buổi
log "Xong mọi bước — xem $status"
