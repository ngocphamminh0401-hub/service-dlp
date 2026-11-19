#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# [Thay đổi] File mới (Benchmark T03 — guard phân loại): so 2 file CSV kết quả phân loại (Before / After) theo từng file.
# Nhận cả CSV của DlpBaseline (khoá id + format) lẫn CSV "fingerprint" của DlpRegexBench (khoá file).
# Quy tắc (kế hoạch §9):
#   - cột thời gian (*_ms) và cột trong --ignore: bỏ qua;
#   - cột số thực (giá trị có dấu '.'): sai lệch tuyệt đối ≤ --tol (mặc định 1e-4) — VD s1_*, route_confidence;
#   - mọi cột còn lại (predicted, match_count, rules_fired, tel_* đếm...): phải BẰNG TUYỆT ĐỐI.
# Thoát mã 0 nếu không có khác biệt, 1 nếu có (dùng được trong script tự động). Chỉ in id/tên cột/giá trị đếm, không in nội dung file.
#
# Cách dùng: python3 tools/perf/diff-classification.py before.csv after.csv [--tol 1e-4] [--ignore col1,col2] [--max-show 20]
import argparse
import csv
import sys


def load(path):
    # utf-8-sig: CSV của DlpBaseline có BOM
    with open(path, encoding="utf-8-sig", newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return [], {}
    cols = list(rows[0].keys())
    key_cols = ["id", "format"] if "id" in cols and "format" in cols else ["file"]
    data = {}
    for r in rows:
        k = tuple(r[c] for c in key_cols)
        if k in data:
            sys.exit(f"Khoá trùng {k} trong {path}")
        data[k] = r
    return cols, data


def is_float(s):
    try:
        float(s)
        return "." in s or "e" in s.lower()
    except ValueError:
        return False


def main():
    ap = argparse.ArgumentParser(description="Guard phân loại: so kết quả Before/After theo từng file")
    ap.add_argument("before")
    ap.add_argument("after")
    ap.add_argument("--tol", type=float, default=1e-4)
    ap.add_argument("--ignore", default="", help="các cột bỏ qua, ngăn bởi dấu phẩy (ngoài *_ms)")
    ap.add_argument("--max-show", type=int, default=20)
    a = ap.parse_args()

    cols_b, before = load(a.before)
    cols_a, after = load(a.after)
    ignore = {c for c in a.ignore.split(",") if c}
    compare = [c for c in cols_b if c in cols_a and not c.endswith("_ms") and c not in ignore]

    missing = sorted(set(before) - set(after))
    extra = sorted(set(after) - set(before))
    diffs = []  # (khoá, cột, before, after)
    for k in sorted(set(before) & set(after)):
        b, r = before[k], after[k]
        for c in compare:
            vb, va = b[c], r[c]
            if vb == va:
                continue
            if is_float(vb) and is_float(va) and abs(float(vb) - float(va)) <= a.tol:
                continue
            diffs.append((k, c, vb, va))

    print(f"Before: {len(before)} file · After: {len(after)} file · so {len(compare)} cột (bỏ {len(cols_b) - len(compare)} cột thời gian/ignore)")
    print(f"Thiếu ở After: {len(missing)} · thừa ở After: {len(extra)} · khác biệt: {len(diffs)} ô trên {len({d[0] for d in diffs})} file")
    by_col = {}
    for d in diffs:
        by_col[d[1]] = by_col.get(d[1], 0) + 1
    for c, n in sorted(by_col.items(), key=lambda x: -x[1]):
        print(f"  cột {c}: {n}")
    for k, c, vb, va in diffs[: a.max_show]:
        print(f"  {'/'.join(k)} · {c}: {vb!r} → {va!r}")
    for k in missing[: a.max_show]:
        print(f"  thiếu: {'/'.join(k)}")
    ok = not diffs and not missing and not extra
    print("KẾT QUẢ: GIỐNG NHAU" if ok else "KẾT QUẢ: CÓ KHÁC BIỆT — phải giải thích trước khi chấp nhận thay đổi")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
