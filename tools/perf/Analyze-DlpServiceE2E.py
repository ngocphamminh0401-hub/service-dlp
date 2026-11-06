# [Thay đổi] File mới: phân tích kết quả Measure-DlpServiceE2E.ps1 → báo cáo Markdown (stdout). Chỉ số độ chính xác dùng
# CÙNG định nghĩa với docs/danh-gia-lop-regex.md (DlpBaseline) để so sánh trực tiếp service thật với pipeline đo riêng.
# Cách chạy: python -I tools\perf\Analyze-DlpServiceE2E.py tools\perf\results\e2e-<run_id>  > báo-cáo.md
import csv, json, sys
from collections import Counter

sys.stdout.reconfigure(encoding="utf-8")
prefix = sys.argv[1]
LV = ["Public", "Internal", "Confidential", "Restricted"]
LABELS = ["public", "internal", "confidential", "top_secret"]

files = list(csv.DictReader(open(prefix + "-files.csv", encoding="utf-8-sig")))
res = list(csv.DictReader(open(prefix + "-resources.csv", encoding="utf-8-sig")))
summ = json.load(open(prefix + "-summary.json", encoding="utf-8-sig"))
warns = list(csv.DictReader(open(prefix + "-warnings.csv", encoding="utf-8-sig"))) if __import__("os").path.exists(prefix + "-warnings.csv") else []

num = lambda v: float(v) if v not in ("", None) else None
pct = lambda x: "—" if x is None else f"{x*100:.1f} %"
f1 = lambda x: "—" if x is None else f"{x:.1f}"

def pctl(vals, p):
    s = sorted(vals)
    if not s: return None
    k = max(0, min(len(s) - 1, -(-int(p * len(s)) // 100) - 1 if p < 100 else len(s) - 1))
    return s[k]

def prf(tp, fp, fn):
    p = tp / (tp + fp) if tp + fp else None
    r = tp / (tp + fn) if tp + fn else None
    return p, r, (2 * p * r / (p + r) if p and r else 0.0)

def level_stats(rs):
    out = {}
    for l in LV:
        tp = sum(r["truth"] == l and r["predicted"] == l for r in rs)
        fp = sum(r["truth"] != l and r["predicted"] == l for r in rs)
        fn = sum(r["truth"] == l and r["predicted"] != l for r in rs)
        out[l] = (*prf(tp, fp, fn), tp + fn)
    n = len(rs)
    acc = sum(r["truth"] == r["predicted"] for r in rs) / n
    pres = [l for l in LV if out[l][3]]
    macro = [sum((out[l][i] or 0) for l in pres) / len(pres) for i in range(3)]
    weighted = [sum((out[l][i] or 0) * out[l][3] for l in pres) / n for i in range(3)]
    return out, acc, macro, weighted

def binary(rs):
    s = lambda l: l in ("Confidential", "Restricted")
    tp = sum(s(r["truth"]) and s(r["predicted"]) for r in rs)
    fp = sum(not s(r["truth"]) and s(r["predicted"]) for r in rs)
    fn = sum(s(r["truth"]) and not s(r["predicted"]) for r in rs)
    return (tp, fp, fn, len(rs) - tp - fp - fn, *prf(tp, fp, fn))

burst = [r for r in files if r["phase"] == "burst"]
seq = [r for r in files if r["phase"] == "seq"]
# [Thay đổi] hạng mục 4: file của các mức tải đều; chạy -NoBurst thì tính độ chính xác/ổn định trên các file này
rate = [r for r in files if r["phase"].startswith("rate")]
pool = burst if burst else rate
concluded = [r for r in pool if r["predicted"] in LV]
missing = [r for r in pool if not r["done_ms"]]
dup = [r for r in pool if int(r["perf_count"] or 0) > 1]
unconcluded = [r for r in pool if r["predicted"] == "Unconcluded"]
readerr = [r for r in pool if r["predicted"] == "ReadError"]
burst_label = "burst" if burst else "tải đều"

print(f"# Đánh giá DlpService (bản publish, chạy dưới SCM) — lượt `{summ['run_id']}`\n")
print(f"- Bắt đầu: {summ['run_start']} · LlmSvc đang chạy: **{summ['llm_running']}** · chạy quyền admin: {summ['is_admin']}"
      f" · thả file: **{'move nhanh (-FastDrop)' if summ.get('fast_drop') else 'copy'}**")  # [Thay đổi] chế độ thả file
print(f"- Tập đo: burst **{summ['burst_files']:,} file** có nhãn (cùng danh sách với `{summ['manifest'].split(chr(92))[-1]}`) + tuần tự {summ['seq_files']} file\n")

# ===================== 1. ĐỘ CHÍNH XÁC =====================
out, acc, macro, weighted = level_stats(concluded)
tp, fp, fn, tn, bp, br, bf = binary(concluded)
agree = sum(r["predicted"] == r["baseline_predicted"] for r in concluded) / len(concluded)
print("## 1. Độ chính xác (lớp regex, mức đọc từ log service)\n")
print("| Chỉ số | Giá trị |\n|---|---|")
print(f"| File có kết luận / tổng ({burst_label}) | {len(concluded):,} / {len(pool):,} |")
print(f"| Accuracy (4 mức) | **{pct(acc)}** |")
print(f"| Macro P / R / F1 | {pct(macro[0])} / {pct(macro[1])} / **{pct(macro[2])}** |")
print(f"| Weighted P / R / F1 | {pct(weighted[0])} / {pct(weighted[1])} / {pct(weighted[2])} |")
print(f"| \"Nhạy cảm\" P / R / F1 | {pct(bp)} / {pct(br)} / **{pct(bf)}** (TP {tp:,} · FP {fp:,} · FN {fn:,} · TN {tn:,}) |")
print(f"| Trùng mức với DlpBaseline (cùng file) | **{pct(agree)}** |\n")
print("| Mức | Số file thật | Precision | Recall | F1 |\n|---|---|---|---|---|")
for l in LV:
    p, r, f, s = out[l]
    print(f"| {l} | {s:,} | {pct(p)} | {pct(r)} | {pct(f)} |")
print("\n| Thật \\ Dự đoán | " + " | ".join(LV) + " |\n|---|---|---|---|---|")
for t in LV:
    print(f"| {t} | " + " | ".join(f"{sum(r['truth']==t and r['predicted']==p for r in concluded):,}" for p in LV) + " |")
print("\n### Theo 16 lớp\n\n| Lớp | Số file | Precision | Recall | F1 |\n|---|---|---|---|---|")
for d in sorted({r["domain"] for r in concluded}):
    rd = [r for r in concluded if r["domain"] == d]
    o, *_ = level_stats(rd)
    for lab, l in zip(LABELS, LV):
        p, r, f, s = o[l]
        print(f"| `{d}__{lab}` | {s:,} | {pct(p)} | {pct(r)} | {pct(f)} |")
print("\n| Nhóm | Số file | Accuracy | Macro-F1 |\n|---|---|---|---|")
for name, sel in [(f"Domain {d}", (lambda d: lambda r: r["domain"] == d)(d)) for d in sorted({r["domain"] for r in concluded})] + \
        [("docx", lambda r: r["format"] == "docx"), ("pdf", lambda r: r["format"] == "pdf")]:
    rs = [r for r in concluded if sel(r)]
    if not rs:  # [Sửa lỗi] nhóm rỗng → bỏ qua (tránh chia 0)
        continue
    _, a, m, _ = level_stats(rs)
    print(f"| {name} | {len(rs):,} | {pct(a)} | {pct(m[2])} |")
print()

# ===================== 2. THỜI GIAN =====================
print("## 2. Thời gian\n")
print("### 2.1. Thời gian xử lý bên trong service (dòng [Perf], đã loại debounce/copy)\n")
print("| Tập | Bước | TB (ms) | p50 | p95 | p99 | Max |\n|---|---|---|---|---|---|---|")
for name, rs in [("Tuần tự (1 file/lần)", seq), ("Burst (tải cao, song song)", burst)]:
    for step, k in [("Tra cache (DB)", "perf_cache"), ("Trích text", "perf_read"), ("Băm SHA-256", "perf_hash"), ("Quét regex", "perf_scan"),
                    ("Phân loại + S1 + conf", "perf_classify"), ("Ghi DB", "perf_save"), ("**Tổng**", "perf_total")]:
        v = [num(r[k]) for r in rs if num(r[k]) is not None]
        if v:
            print(f"| {name} | {step} | {f1(sum(v)/len(v))} | {f1(pctl(v,50))} | {f1(pctl(v,95))} | {f1(pctl(v,99))} | {f1(max(v))} |")
print("\n### 2.2. Độ trễ đầu-cuối tuần tự (từ lúc copy file vào watch-folder tới khi service ghi xong kết quả)\n")
lat = [num(r["done_ms"]) for r in seq if num(r["done_ms"]) is not None]
print("| Nhóm | Số file | TB (ms) | p50 | p95 | Max |\n|---|---|---|---|---|---|")
for name, sel in [("Tất cả", lambda r: True), ("docx", lambda r: r["format"] == "docx"), ("pdf", lambda r: r["format"] == "pdf")]:
    v = [num(r["done_ms"]) for r in seq if sel(r) and num(r["done_ms"]) is not None]
    if v:
        print(f"| {name} | {len(v)} | {f1(sum(v)/len(v))} | {f1(pctl(v,50))} | {f1(pctl(v,95))} | {f1(max(v))} |")
print("\n(Gồm 300 ms debounce của watcher + copy file; độ phân giải đọc log ~20 ms.)\n")
el = (summ["burst_elapsed_ms"] or 0) / 1000
mb = sum(int(r["size_bytes"]) for r in burst) / 1024 / 1024
print("### 2.3. Toàn bộ pipeline (burst)\n\n| Mục | Giá trị |\n|---|---|")
print(f"| Thời gian thả {len(burst):,} file (copy) | {(summ['burst_copy_ms'] or 0)/1000:,.1f} giây |")
print(f"| **Thời gian tới khi file cuối có kết quả** | **{el:,.1f} giây** ({el/60:.1f} phút) |")
if el:
    print(f"| **Thông lượng** | **{len(burst)-len(missing):,}/{el:,.0f} = {(len(burst)-len(missing))/el:.1f} file/giây**, {mb/el:.2f} MB/giây |")
tot = [num(r["perf_total"]) for r in burst if num(r["perf_total"])]
if tot:
    print(f"| Tổng thời gian xử lý cộng dồn ([Perf]) | {sum(tot)/1000:,.1f} giây → mức song song TB ≈ {sum(tot)/1000/el:.1f} file cùng lúc |")
print()

# ===================== 3. TÀI NGUYÊN =====================
print("## 3. Tài nguyên theo giai đoạn (lấy mẫu ~1 giây)\n")
# [Thay đổi] hạng mục 4: thứ tự giai đoạn động (gồm các mức tải đều) + cột CPU Defender / script đo / cả máy, I/O, đĩa
phases = ["nen", "tuan-tu"] + sorted({x["Phase"] for x in res if x["Phase"].startswith("rate")}, key=lambda p: int(p[4:])) + ["burst", "sau-tai", "dang-do-khi-loi"]
fv = lambda x, k: float(x.get(k) or 0)
avg = lambda v: sum(v) / len(v) if v else 0
print("| Giai đoạn | Mẫu | CPU service TB / max (% máy) | CPU Defender TB | CPU script đo TB | CPU cả máy TB | RAM đầu → max → cuối (MB) | I/O đọc / ghi TB (MB/s) | Đĩa bận TB | Handle đầu → max → cuối | Thread max |")
print("|---|---|---|---|---|---|---|---|---|---|---|")
for ph in phases:
    s = [x for x in res if x["Phase"] == ph]
    if not s: continue
    cpu = [fv(x, "CpuPct") for x in s]; ram = [fv(x, "RamMB") for x in s]; h = [int(fv(x, "Handles")) for x in s]; t = [int(fv(x, "Threads")) for x in s]
    print(f"| {ph} | {len(s)} | {avg(cpu):.1f} / {max(cpu):.1f} | {avg([fv(x,'DefenderCpu') for x in s]):.1f} | {avg([fv(x,'ScriptCpu') for x in s]):.1f} | "
          f"{avg([fv(x,'MachineCpu') for x in s]):.1f} | {ram[0]:.0f} → {max(ram):.0f} → {ram[-1]:.0f} | "
          f"{avg([fv(x,'IoReadMBs') for x in s]):.2f} / {avg([fv(x,'IoWriteMBs') for x in s]):.2f} | {avg([fv(x,'DiskPct') for x in s]):.0f} % | "
          f"{h[0]} → {max(h)} → {h[-1]} | {max(t)} |")
print("\n(CPU tính theo % của CẢ máy, 100 % = mọi nhân. \"Script đo\" = PowerShell của chính công cụ đo — không thuộc service.)\n")

# [Thay đổi] hạng mục 4: tài nguyên + độ trễ theo mức tải đều
rate_phases = [p for p in phases if p.startswith("rate")]
if rate_phases:
    print("### Theo mức tải đều\n")
    print("| Mức (file/giây) | Số file | Tốc độ thả thực | Xong | Độ trễ đầu-cuối p50 / p95 / max (ms) | Xử lý trong service TB (ms) | CPU service TB | CPU Defender TB | RAM TB / max (MB) | I/O đọc TB (MB/s) |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for ph in rate_phases:
        rows = [r for r in files if r["phase"] == ph]
        lat = [num(r["done_ms"]) - num(r["copy_ms"]) for r in rows if num(r["done_ms"]) is not None and num(r["copy_ms"]) is not None]
        last_copy = max((num(r["copy_ms"]) or 0) for r in rows) / 1000
        s = [x for x in res if x["Phase"] == ph]
        ram = [fv(x, "RamMB") for x in s] or [0]
        tot = [num(r["perf_total"]) for r in rows if num(r["perf_total"]) is not None]
        print(f"| {ph[4:]} | {len(rows):,} | {len(rows)/max(1e-9,last_copy):.1f} | {len(lat):,} | {f1(pctl(lat,50))} / {f1(pctl(lat,95))} / {f1(max(lat) if lat else None)} | "
              f"{f1(avg(tot))} | {avg([fv(x,'CpuPct') for x in s]):.1f} % | {avg([fv(x,'DefenderCpu') for x in s]):.1f} % | {avg(ram):.0f} / {max(ram):.0f} | {avg([fv(x,'IoReadMBs') for x in s]):.2f} |")
    print("\n(Độ trễ đầu-cuối gồm 300 ms debounce + copy; độ phân giải đọc log ~100 ms.)\n")

# [Thay đổi] hạng mục 4: độ dốc tài nguyên khi rảnh dài (rò rỉ thì tăng đều theo thời gian)
def slope_per_hour(vals):
    n = len(vals)
    if n < 30: return None
    xs = range(n); mx = (n - 1) / 2; my = sum(vals) / n
    return sum((x - mx) * (y - my) for x, y in zip(xs, vals)) / sum((x - mx) ** 2 for x in xs) * 3600  # mẫu ~1 giây
idle = [x for x in res if x["Phase"] == "sau-tai"]
if len(idle) >= 30:
    sr = slope_per_hour([fv(x, "RamMB") for x in idle]); sh = slope_per_hour([fv(x, "Handles") for x in idle]); st = slope_per_hour([fv(x, "Threads") for x in idle])
    print(f"- Rảnh sau tải ({len(idle)} mẫu ≈ {len(idle)/60:.0f} phút): độ dốc RAM **{sr:+.1f} MB/giờ**, handle {sh:+.0f}/giờ, thread {st:+.0f}/giờ "
          f"(≈ 0 = ổn định; dương đều = nghi rò rỉ). CPU service TB {avg([fv(x,'CpuPct') for x in idle]):.2f} %.")
b = [x for x in res if x["Phase"] == "burst"]
if len(b) > 20:
    # Độ dốc RAM ở nửa sau burst (MB/phút): ổn định ≈ 0, rò rỉ thì dương đều
    half = b[len(b)//2:]
    ram = [float(x["RamMB"]) for x in half]; n = len(ram); xs = list(range(n)); mx = sum(xs)/n; my = sum(ram)/n
    slope = sum((x-mx)*(y-my) for x, y in zip(xs, ram)) / max(1e-9, sum((x-mx)**2 for x in xs))
    print(f"\n- Độ dốc RAM nửa sau burst: **{slope*60:+.1f} MB/phút** (≈ 0 = ổn định; dương đều = nghi rò bộ nhớ).")
nen = [float(x["RamMB"]) for x in res if x["Phase"] == "nen"]; sau = [float(x["RamMB"]) for x in res if x["Phase"] == "sau-tai"]
if nen and sau:
    print(f"- RAM nền {sum(nen)/len(nen):.0f} MB → sau tải {sau[-1]:.0f} MB (chênh {sau[-1]-sum(nen)/len(nen):+.0f} MB).")
print()

# ===================== [Thay đổi] XỬ LÝ ĐỒNG THỜI =====================
batch_phases = sorted({r["phase"] for r in files if r["phase"].startswith("batch")}, key=lambda p: int(p[5:]))
if batch_phases or summ.get("storm") or summ.get("locked") or summ.get("post_idle"):
    print("## 3b. Xử lý đồng thời\n")
if batch_phases:
    binfo = {b["n"]: b for b in (summ.get("batches") or [])}
    print("Thả CÙNG LÚC N file (move nhanh từ thư mục tạm), chờ tới khi file cuối có kết quả:\n")
    print("| Lô (file) | Thả trong (ms) | Xong | Thời gian cả lô (giây) | Thông lượng (file/giây) | Độ trễ p50 / p95 / max (ms) | Xử lý trong service TB / p95 (ms) | Mất | Trùng | Đọc lỗi | Tràn buffer |")
    print("|---|---|---|---|---|---|---|---|---|---|---|")
    for ph in batch_phases:
        rows = [r for r in files if r["phase"] == ph]
        n = int(ph[5:]); b = binfo.get(n, {})
        lat = [num(r["done_ms"]) - num(r["copy_ms"]) for r in rows if num(r["done_ms"]) is not None and num(r["copy_ms"]) is not None]
        tot = [num(r["perf_total"]) for r in rows if num(r["perf_total"]) is not None]
        last = (b.get("last_done_ms") or 0) / 1000
        print(f"| {n:,} | {b.get('drop_ms','—')} | {len(lat):,}/{len(rows):,} | {f1(last)} | {f1(len(lat)/last if last else None)} | "
              f"{f1(pctl(lat,50))} / {f1(pctl(lat,95))} / {f1(max(lat) if lat else None)} | {f1(avg(tot))} / {f1(pctl(tot,95))} | "
              f"{sum(1 for r in rows if not r['done_ms'])} | {sum(1 for r in rows if int(r['perf_count'] or 0) > 1)} | "
              f"{sum(1 for r in rows if r['predicted'] == 'ReadError')} | {b.get('overflow','—')} |")
    print()
st = summ.get("storm")
if st:
    ok = st.get("final_level") == st.get("expected")
    print(f"- **Ghi đè dồn dập** (1 file ghi đè {st['writes']} lần, cách nhau 100 ms, lần cuối là nội dung {st['expected']}): service xử lý **{st['processings']} lần**, "
          f"mức lần lượt `{st['levels_seen'] or '—'}` → mức cuối **{st['final_level'] or '—'}** ({'ĐÚNG' if ok else 'SAI'} theo nội dung cuối); ghi lỗi do file đang bị đọc: {st['write_errors']}.")
lk = summ.get("locked")
if lk:
    print(f"- **File bị khoá** ({lk['files']} file khoá độc quyền 5 giây ngay sau khi thả): đọc lỗi {lk['read_failed']} lần, **xử lý được sau khi mở khoá {lk['processed_after_unlock']}/{lk['files']}** "
          f"(chờ 30 giây). Thông điệp: `{lk.get('sample_error') or '—'}`.")
pi = summ.get("post_idle")
if pi:
    print(f"- **Sau {pi['idle_sec']} giây rảnh** (kiểm chứng sửa lỗi watcher bị GC thu hồi): xử lý **{pi['processed']}/{pi['files']}** file.")
if batch_phases or st or lk or pi:
    print()

# ===================== 4. ỔN ĐỊNH =====================
print("## 4. Độ ổn định\n\n| Kiểm tra | Kết quả |\n|---|---|")
print(f"| File KHÔNG có kết quả (mất) | **{len(missing):,}** / {len(pool):,} |")
print(f"| File xử lý trùng (>1 dòng [Perf]) | {len(dup):,} |")
print(f"| Quét chưa trọn (rule timeout) | {len(unconcluded):,} |")
print(f"| Đọc file thất bại | {len(readerr):,} |")
print(f"| Tràn buffer FileSystemWatcher | {summ['buffer_overflow']} lần |")
pids = [p for p in str(summ['pids_seen']).split(';') if p]
print(f"| Tiến trình bị khởi động lại ngoài ý muốn (PID đổi trong lúc đo) | {'KHÔNG' if len(pids) <= 1 else 'CÓ — PID ' + ', '.join(pids)} |")
print(f"| Sự kiện SCM (7031/7034/7023/7024 — crash/dừng bất thường) | {summ['scm_events'] or 'không có'} |")
print(f"| Lỗi Application Event Log liên quan | {summ['app_errors'] or 'không có'} |")
print(f"| Số dòng log trong lượt đo | {summ['log_lines']:,} ({summ['log_levels']}) |")
if summ.get("restart_running_ms") is not None:
    print(f"| Khởi động lại: tới Running / tới \"Đang theo dõi\" | {summ['restart_running_ms']:,} ms / {summ['restart_watching_ms'] or '—'} ms |")
    print(f"| Sau khởi động lại: file đã xử lý bị quét lại | **{summ['restart_rescanned_files']}** (mong đợi 0 nhờ cache) |")
if warns:
    print("\nCảnh báo/lỗi trong log (gom theo mẫu thông điệp, đã bỏ đường dẫn/số):\n\n| Số lần | Thông điệp |\n|---|---|")
    for w in warns[:12]:
        print(f"| {int(w['count']):,} | `{w['message'].replace('|', '/')}` |")
