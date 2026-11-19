#!/usr/bin/env bash
# [Thay đổi] File mới (Benchmark T11): chụp trace để biết HÀM nào tốn thời gian / cấp phát — xác nhận cơ chế của các giả thuyết A2-b (vòng
# deferred), A4 (CountUnstructuredNumericHits), B3/B4/B5 (extractor). Chạy công cụ đo in-process dưới `dotnet-trace collect -- <lệnh>`:
#   - profile dotnet-sampled-thread-time: lấy mẫu stack .NET ~100 Hz (bench 1 luồng → xấp xỉ thời gian CPU theo hàm)
#   - profile gc-verbose: lấy mẫu cấp phát (kiểu object + stack) + từng lần GC
# Mỗi trace ra: .nettrace (mở bằng PerfView), .speedscope.json (mở ở https://www.speedscope.app — file nằm trên máy, không tải lên đâu cả nếu
# dùng bản offline), topN-exclusive.txt / topN-inclusive.txt (đọc được ngay bằng mắt).
# Số THỜI GIAN của lượt có trace KHÔNG dùng làm baseline (trace làm chậm chương trình) — chỉ dùng để xếp hạng hàm.
#
# Lưu ý: "dotnet tool run dotnet-trace -- …" — dấu -- đầu bị dotnet tool run nuốt, thiếu nó thì "collect -- <lệnh>" không nhận lệnh con.
# Cách dùng: tools/perf/trace-hotspots.sh <dataset DlpBenchData> <thư mục ra> [--quick]
#   --quick: bộ smoke / chạy thử (file nhỏ hơn)
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dotnet="${DOTNET:-$HOME/.dotnet/dotnet}"
dataset="${1:?Cần thư mục dataset}"; out="${2:?Cần thư mục ra}"; quick="${3:-}"
mkdir -p "$out"
regex_dll="$here/DlpRegexBench/bin/Release/net10.0/DlpRegexBench.dll"
extr_dll="$here/DlpExtractorBench/bin/Release/net10.0/DlpExtractorBench.dll"

# Dataset con: manifest.csv lọc theo tên file + symlink tới file gốc (không chép)
subset() {
  local dir="$out/subset-$1"; shift
  rm -rf "$dir"; mkdir -p "$dir"
  head -1 "$dataset/manifest.csv" > "$dir/manifest.csv"
  for f in "$@"; do
    if grep -q ",$f," "$dataset/manifest.csv"; then
      grep ",$f," "$dataset/manifest.csv" >> "$dir/manifest.csv"
      mkdir -p "$dir/$(dirname "$f")"; ln -s "$dataset/$f" "$dir/$f"
    fi
  done
  echo "$dir"
}

# trace <tên> <profile> <dll> <tham số...>
trace() {
  local name="$1" profile="$2"; shift 2
  local nt="$out/$name.nettrace"
  echo "== trace $name ($profile)"
  (cd "$here" && "$dotnet" tool run dotnet-trace -- collect --profile "$profile" -o "$nt" -- "$dotnet" "$@") > "$out/$name.log" 2>&1
  if [ ! -s "$nt" ]; then echo "  ! không có trace — xem $out/$name.log"; return 1; fi
  (cd "$here" && "$dotnet" tool run dotnet-trace -- report "$nt" topN -n 40) > "$out/$name.topN-exclusive.txt" 2>&1
  (cd "$here" && "$dotnet" tool run dotnet-trace -- report "$nt" topN -n 40 --inclusive) > "$out/$name.topN-inclusive.txt" 2>&1
  (cd "$here" && "$dotnet" tool run dotnet-trace -- convert "$nt" --format speedscope -o "$out/$name") >/dev/null 2>&1
  echo "  $(du -h "$nt" | cut -f1) · top hàm (exclusive):"
  grep -E '^\s*[0-9]+\.' "$out/$name.topN-exclusive.txt" | head -8 | sed 's/^/    /'
}

if [ "$quick" = "--quick" ]; then
  csv=("D-csv/csv-1krows.csv"); kw=("D-kw/kw-20k.txt"); big=("D-pdf/pdf-text-10p.pdf" "D-office/docx-xml2mb.docx" "D-txt/txt-1mb-utf8.txt")
else
  csv=("D-csv/csv-10krows.csv"); kw=("D-kw/kw-2M.txt" "D-match/match-2M-50kv.txt")
  big=("D-pdf/pdf-text-500p.pdf" "D-office/docx-xml20mb.docx" "D-office/pptx-500slides.pptx" "D-txt/txt-9.5mb-utf8.txt" "D-csv/csv-30krows.csv")
fi

# R3 / A2-b / A4: CSV nhiều dòng số — trần quét nới rộng để thấy chi phí thật
d=$(subset csv "${csv[@]}")
trace regex-csv dotnet-sampled-thread-time "$regex_dll" rules "$d" --runs 1 --warmup 1 --max-scan-seconds 900 --out "$out/regex-csv"
# R2 / R5: dày từ khoá + nhiều chỗ khớp
d=$(subset kw "${kw[@]}")
trace regex-kw dotnet-sampled-thread-time "$regex_dll" rules "$d" --runs 1 --warmup 1 --out "$out/regex-kw"
# F*: extractor — thời gian theo hàm + cấp phát theo kiểu object
d=$(subset big "${big[@]}")
trace extractor-time dotnet-sampled-thread-time "$extr_dll" stages "$d" --runs 1 --out "$out/extractor-time"
trace extractor-alloc gc-verbose "$extr_dll" stages "$d" --runs 1 --out "$out/extractor-alloc"
echo "Xong: $out"
