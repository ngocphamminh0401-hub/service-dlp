#!/usr/bin/env bash
# [Thay đổi] File mới (Benchmark T05 / F7 — giả thuyết B1 "mỗi file mở 3–4 lần"): đếm số syscall open/stat trỏ tới ĐÚNG file đích khi
# FileReaderService.TryReadFile đọc nó 1 lần (DlpExtractorBench once). Chạy dưới strace -f (theo cả luồng con), lọc theo đường dẫn tuyệt đối.
# Chỉ đếm syscall theo TÊN file (openat/stat/statx/newfstatat/access); lần đọc qua fd đã mở (read/pread/lseek) đếm riêng theo fd.
# Ghi chú: chỉ là TryReadFile — FileProcessor còn stat thêm (FileInfo trước/sau khi đọc, change token); đo phần đó ở E2E (T08).
#
# Cách dùng: tools/perf/count-opens.sh <file> [<file>...]          (cần strace; không cần root vì chỉ trace tiến trình con của mình)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
dll="$here/DlpExtractorBench/bin/Release/net10.0/DlpExtractorBench.dll"
[ -f "$dll" ] || { echo "Chưa build: $dotnet build $here/DlpExtractorBench -c Release" >&2; exit 1; }
command -v strace >/dev/null && command -v gawk >/dev/null || { echo "Cần strace và gawk" >&2; exit 1; }
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

printf 'file,openat,stat_family,read_calls,lseek_calls,bytes_read,file_bytes\n'
for f in "$@"; do
  abs="$(readlink -f "$f")"
  log="$tmp/trace.log"
  strace -f -qq -e trace=openat,open,stat,lstat,newfstatat,statx,access,faccessat,read,pread64,lseek,close -o "$log" \
    "$dotnet" "$dll" once "$abs" >/dev/null
  opens=$(grep -F "\"$abs\"" "$log" | grep -cE 'open(at)?\(' || true)
  stats=$(grep -F "\"$abs\"" "$log" | grep -cE '(stat|statx|newfstatat|access|faccessat)\(' || true)
  # fd của các lần open file đích → đếm read/lseek trên các fd đó (fd có thể bị dùng lại sau close: chỉ tính trong khoảng open..close)
  read_stats=$(gawk -v p="\"$abs\"" '
    $0 ~ /open(at)?\(/ && index($0, p) { n=split($0,a,"= "); fd=a[n]+0; if (fd>=0) live[fd]=1; next }
    {
      if (match($0, /(read|pread64|lseek|close)\(([0-9]+)/, m)) {
        key=m[2]   # [Thay đổi] khoá theo fd (các luồng cùng tiến trình dùng chung fd; cột đầu của strace -f là TID)
        if (key in live) {
          if (m[1]=="close") { delete live[key] }
          else if (m[1]=="lseek") { seeks++ }
          else { reads++; n=split($0,a,"= "); if (a[n]+0>0) bytes+=a[n] }
        }
      }
    }
    END { printf "%d,%d,%d", reads, seeks, bytes }' "$log")
  printf '%s,%s,%s,%s,%s\n' "$f" "$opens" "$stats" "$read_stats" "$(stat -c %s "$abs")"
done
