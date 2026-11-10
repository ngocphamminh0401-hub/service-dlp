#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# [Thay đổi] File mới (đối chiếu Linux ↔ Windows, 10/11/2026): tính độ chính xác từ file .tsv của snapshot-ket-qua.py,
# cùng định nghĩa chỉ số với docs/danh-gia-lop-regex.md (Accuracy, Macro/Weighted P-R-F1, nhị phân "nhạy cảm",
# ma trận nhầm lẫn, 16 lớp, domain / định dạng / ca khó). Nhãn thật lấy từ <rawdata>/<thư mục>/_metadata.jsonl.
# Chỉ đọc mức + đường dẫn, KHÔNG in giá trị nhạy cảm.
#
# Cách dùng:
#   python3 scripts/snapshot-ket-qua.py --per-dir 100000 --timeout 5400 --set Llm__Enabled=false --out full.tsv
#   python3 scripts/doi-chieu-do-chinh-xac.py full.tsv ~/dlp-rawdata
import json, os, sys
from collections import Counter, defaultdict

tsv, rawdata = sys.argv[1], sys.argv[2]
LV = ["Public", "Internal", "Confidential", "Restricted"]
MAP = {"public": "Public", "internal": "Internal", "confidential": "Confidential", "top_secret": "Restricted"}

meta = {}  # "thu_muc/ten_file" -> (truth, domain, borderline)
for d in sorted(os.listdir(rawdata)):
    p = os.path.join(rawdata, d, "_metadata.jsonl")
    if not os.path.isfile(p):
        continue
    for line in open(p, encoding="utf-8"):
        if not line.strip():
            continue
        r = json.loads(line)
        for k in ("docx_path", "pdf_path"):
            name = r[k].replace("\\", "/").split("/")[-1]
            meta[f"{d}/{name}"] = (MAP[r["label"]], r["domain"], bool(r.get("is_borderline")))

pred = {}
for i, line in enumerate(open(tsv, encoding="utf-8")):
    if i == 0:
        continue
    c = line.rstrip("\n").split("\t")
    pred[c[0]] = json.loads(c[7])["level"] if c[7] else "Public"

rows = [(meta[k], pred[k], k) for k in meta if k in pred]
missing = [k for k in meta if k not in pred and os.path.exists(os.path.join(rawdata, k))]
print(f"có nhãn: {len(meta)} · có trên đĩa: {sum(os.path.exists(os.path.join(rawdata,k)) for k in meta)} "
      f"· có kết quả: {len(rows)} · thiếu kết quả: {len(missing)} · ngoài nhãn: {len(pred) - len(rows)}")

def pct(x): return "—" if x is None else f"{x*100:.1f}".replace(".", ",") + " %"
def prf(rs, lv):
    tp = sum(t == lv and p == lv for t, p in rs); fp = sum(t != lv and p == lv for t, p in rs)
    fn = sum(t == lv and p != lv for t, p in rs)
    P = tp / (tp + fp) if tp + fp else None; R = tp / (tp + fn) if tp + fn else None
    F = 2 * P * R / (P + R) if P and R else None
    return P, R, F, tp + fn
def summary(rs):
    acc = sum(t == p for t, p in rs) / len(rs)
    per = [prf(rs, l) for l in LV]
    mf = sum(x[2] or 0 for x in per) / 4
    sens = lambda l: l in ("Confidential", "Restricted")
    tp = sum(sens(t) and sens(p) for t, p in rs); fp = sum(not sens(t) and sens(p) for t, p in rs)
    fn = sum(sens(t) and not sens(p) for t, p in rs); tn = len(rs) - tp - fp - fn
    P = tp / (tp + fp); R = tp / (tp + fn)
    return acc, per, mf, (P, R, 2 * P * R / (P + R), tp, fp, fn, tn, (tp + tn) / len(rs))

tp_ = [(m[0], p) for m, p, _ in rows]
acc, per, mf, b = summary(tp_)
n = len(tp_)
wP = sum((x[0] or 0) * x[3] for x in per) / n; wR = sum((x[1] or 0) * x[3] for x in per) / n; wF = sum((x[2] or 0) * x[3] for x in per) / n
print(f"\nAccuracy {pct(acc)} | Macro P/R/F1 {pct(sum(x[0] for x in per)/4)} / {pct(sum(x[1] for x in per)/4)} / {pct(mf)}"
      f" | Weighted {pct(wP)} / {pct(wR)} / {pct(wF)}")
print(f"Nhạy cảm: Acc {pct(b[7])} P/R/F1 {pct(b[0])} / {pct(b[1])} / {pct(b[2])} (TP {b[3]} FP {b[4]} FN {b[5]} TN {b[6]})")
print("\n| Mức | Số file | P | R | F1 |")
for l, x in zip(LV, per):
    print(f"| {l} | {x[3]} | {pct(x[0])} | {pct(x[1])} | {pct(x[2])} |")
cm = Counter(tp_)
print("\n| Thật \\ Đoán | " + " | ".join(LV) + " |")
for t in LV:
    print(f"| {t} | " + " | ".join(str(cm[(t, p)]) for p in LV) + " |")

print("\n| Lớp | Số file | P | R | F1 | thấp hơn | cao hơn |")
groups = defaultdict(list)
for m, p, k in rows:
    groups[k.split("/")[0]].append((m, p))
for g, items in sorted(groups.items()):
    dom = items[0][0][1]
    lv = items[0][0][0]
    rs = [(m[0], p) for m, p, k in rows if m[1] == dom]
    P, R, F, cnt = prf(rs, lv)
    lo = sum(LV.index(p) < LV.index(lv) for m, p in items) / len(items)
    hi = sum(LV.index(p) > LV.index(lv) for m, p in items) / len(items)
    print(f"| `{g}` | {len(items)} | {pct(P)} | {pct(R)} | {pct(F)} | {pct(lo)} | {pct(hi)} |")

print("\n| Nhóm | Số file | Acc | Macro-F1 | F1 nhạy cảm |")
def grp(name, f):
    rs = [(m[0], p) for m, p, k in rows if f(m, k)]
    a, _, mf_, bb = summary(rs)
    print(f"| {name} | {len(rs)} | {pct(a)} | {pct(mf_)} | {pct(bb[2])} |")
for dom in sorted({m[1] for m, _, _ in rows}):
    grp(f"Domain {dom}", lambda m, k, dom=dom: m[1] == dom)
grp("docx", lambda m, k: k.endswith(".docx")); grp("pdf", lambda m, k: k.endswith(".pdf"))
grp("Ca khó", lambda m, k: m[2]); grp("Ca thường", lambda m, k: not m[2])
