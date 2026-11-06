#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Chụp "ảnh" kết quả quét của DlpService để so sánh trước/sau khi tách lõi (kế hoạch tách lõi, bước 0).

Cách hoạt động (đi qua ĐÚNG đường chạy thật của service, gồm FileWatcherService.ProcessFile):
  1. Chọn một tập con CỐ ĐỊNH của thư mục dữ liệu mẫu (cùng tên file -> cùng đầu vào mỗi lần).
  2. Chép sang thư mục tạm (không đụng dữ liệu gốc).
  3. Chạy DlpService với DB MỚI, trỏ Watch:Folders vào thư mục tạm (qua biến môi trường).
  4. Chờ quét xong lượt đầu (số dòng scan_result không đổi trong vài giây).
  5. Đọc DB, xuất mỗi file 1 dòng, sắp xếp theo đường dẫn, BỎ các cột đổi theo mỗi lần chạy.
  6. Dừng service.

So sánh:   py -3 scripts/snapshot-ket-qua.py --out a.tsv   (trước)
           py -3 scripts/snapshot-ket-qua.py --out b.tsv   (sau)
           fc a.tsv b.tsv   (hoặc: git diff --no-index a.tsv b.tsv)

KHÔNG xuất giá trị nhạy cảm: DB vốn chỉ lưu mã rule + vị trí + hash (xem ResultStore).
"""
import argparse
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time

# Console Windows mặc định cp1252 → không in được tiếng Việt
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_RAWDATA = r"D:\dlp-classifier\rawdata"
DEFAULT_DLL = os.path.join(REPO, "DlpService", "bin", "Debug", "net10.0", "DlpService.dll")
EXTENSIONS = (".docx", ".pdf")


def pick_sample(rawdata, per_dir_per_ext):
    """Mỗi thư mục con, mỗi đuôi: lấy N file đầu theo tên. Trả danh sách (đường dẫn gốc, đường dẫn tương đối)."""
    picked = []
    for d in sorted(os.listdir(rawdata)):
        full = os.path.join(rawdata, d)
        if not os.path.isdir(full):
            continue
        names = sorted(os.listdir(full))
        for ext in EXTENSIONS:
            for n in [x for x in names if x.lower().endswith(ext)][:per_dir_per_ext]:
                picked.append((os.path.join(full, n), os.path.join(d, n)))
    return picked


def export(db_path, out_path):
    """Xuất mỗi file 1 dòng. Cột đổi theo mỗi lần chạy (id, thời gian) bị loại để diff có ý nghĩa."""
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    findings = {}
    for fid, rule, count, score, vp, hard, loc in con.execute(
            "SELECT file_id, rule_id, count, score, validator_passed, hard_rule, location FROM finding"):
        findings.setdefault(fid, []).append(f"{rule}:{count}:{score:.6f}:{vp}:{hard}:{loc}")
    rows = []
    for (fid, path, size, h, s1, sfinal, status, source, labels, review, ocr, rv, mv) in con.execute(
            """SELECT f.file_id, f.path, f.size, r.file_hash, r.s1, r.s_final, r.status, r.source,
                      r.labels_json, r.needs_review, r.needs_ocr, r.rules_ver, r.merge_policy_ver
               FROM scan_result r JOIN file_state f ON f.file_id = r.file_id"""):
        rows.append((path, size, h, f"{s1:.6f}", f"{sfinal:.6f}", status, source, labels or "",
                     review, ocr, rv, mv, "|".join(sorted(findings.get(fid, [])))))
    con.close()
    return rows


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rawdata", default=DEFAULT_RAWDATA)
    ap.add_argument("--dll", default=DEFAULT_DLL, help="DlpService.dll đã build")
    ap.add_argument("--per-dir", type=int, default=10, help="số file mỗi đuôi mỗi thư mục con (mặc định 10)")
    ap.add_argument("--out", required=True, help="file .tsv kết quả")
    ap.add_argument("--timeout", type=int, default=900)
    ap.add_argument("--set", action="append", default=[], metavar="KEY=VALUE",
                    help="ghi đè cấu hình, dạng .NET (VD --set Llm__Enabled=false); dùng được nhiều lần")
    args = ap.parse_args()

    if not os.path.isfile(args.dll):
        sys.exit(f"Chưa build: không thấy {args.dll} (chạy: dotnet build DlpService)")
    sample = pick_sample(args.rawdata, args.per_dir)
    if not sample:
        sys.exit("Không chọn được file mẫu nào")
    print(f"Mẫu: {len(sample)} file từ {args.rawdata}")

    work = tempfile.mkdtemp(prefix="dlp-snap-")
    watch = os.path.join(work, "watch")
    db_path = os.path.join(work, "data", "dlp.db")
    try:
        for src, rel in sample:
            dst = os.path.join(watch, rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copyfile(src, dst)

        env = dict(os.environ)
        env["Watch__Folders__0"] = watch          # ghi đè danh sách thư mục trong appsettings.json
        env["Storage__DbPath"] = db_path          # DB mới, sạch
        env["DOTNET_ENVIRONMENT"] = "Production"
        for item in args.set:
            key, _, value = item.partition("=")
            env[key] = value
        proc = subprocess.Popen(["dotnet", args.dll], cwd=os.path.dirname(args.dll), env=env,
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.time() + args.timeout
            last, stable_since = -1, time.time()
            while time.time() < deadline:
                if proc.poll() is not None:
                    sys.exit(f"Service thoát sớm (mã {proc.returncode}) — xem log trong {os.path.dirname(args.dll)}\\logs")
                n = 0
                if os.path.exists(db_path):
                    try:
                        con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True, timeout=5)
                        n = con.execute("SELECT count(*) FROM scan_result").fetchone()[0]
                        con.close()
                    except sqlite3.Error:
                        n = last  # DB đang được tạo / khoá tạm — coi như chưa đổi
                if n != last:
                    last, stable_since = n, time.time()
                    print(f"  đã quét {n}/{len(sample)}", end="\r")
                elif n > 0 and time.time() - stable_since >= 8:
                    break
                time.sleep(1)
            else:
                sys.exit("Hết thời gian chờ")
            print()
            rows = export(db_path, args.out)
        finally:
            proc.kill()
            proc.wait()
        # Đường dẫn trong DB là đường dẫn thư mục tạm → đổi thành tương đối để 2 lần chạy so được với nhau
        prefix = os.path.normpath(watch) + os.sep
        rows = sorted((os.path.normpath(r[0]).replace(prefix, "").replace("\\", "/"),) + r[1:] for r in rows)
        with open(args.out, "w", encoding="utf-8", newline="\n") as f:
            f.write("path\tsize\tfile_hash\ts1\ts_final\tstatus\tsource\tlabels\tneeds_review\tneeds_ocr\trules_ver\tmerge_ver\tfindings\n")
            for r in rows:
                f.write("\t".join(str(x) for x in r) + "\n")
        print(f"Đã ghi {len(rows)} dòng -> {args.out}")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
