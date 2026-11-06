#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""[Thay đổi] File mới (GĐ3 bước 3.6): chạy dlp-service THẬT qua các kịch bản tiêu chí GĐ3 (ke-hoach-trien-khai-linux.md mục 7 + 8.1) và đo.

Mọi thứ nằm trong 1 thư mục tạm (chế độ System với DLP_SYSTEM_ROOT): "home" giả /home/<user>/Documents, DB, log — không đụng máy.
Kiểm tra bằng DB (file_state = file nào service đang biết) + log ([Perf] = mỗi lần quét 1 file; [Metric] = số đo định kỳ).

Kịch bản:  tạo · sửa · đổi tên · xoá · chuyển thư mục ra ngoài · ghi dở (ghi 2 nửa cách nhau 1,5 s) · tải về (.part → đổi tên)
           · giải nén N file · xoá rồi tạo lại Documents · người dùng mới · cây nhiều thư mục (đếm watch) · CPU lúc rảnh.
Tiêu chí:  không sót file (DB khớp đĩa), không quét trùng (giải nén: mỗi file đúng 1 lần [Perf]), p95 trễ ≤ 2 s, CPU rảnh < 2 %.

Chạy:  python3 scripts/watcher-scenarios.py [--unzip 10000] [--dirs 5000] [--idle 60]
Python 3.6+ (Ubuntu 18.04). Cần build trước: dotnet build DlpService.slnx
"""
import argparse
import io
import json
import os
import re
import shutil
import signal
import sqlite3
import subprocess
import sys
import tempfile
import time
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DLL = os.path.join(REPO, "DlpService.Host.Linux", "bin", "Debug", "net10.0", "dlp-service.dll")
TICKS = os.sysconf(os.sysconf_names["SC_CLK_TCK"])


class Run:
    def __init__(self, args):
        self.args = args
        self.root = tempfile.mkdtemp(prefix="dlp-scn-")
        self.home = os.path.join(self.root, "home")
        self.etc = os.path.join(self.root, "etc", "dlp-service")
        self.db = os.path.join(self.root, "var", "lib", "dlp-service", "dlp.db")
        self.logdir = os.path.join(self.root, "var", "log", "dlp-service")
        self.results = []
        self.proc = None

    # ---- tiện ích ----
    def docs(self, user):
        return os.path.join(self.home, user, "Documents")

    def start(self):
        os.makedirs(self.etc)
        shutil.copytree(os.path.join(os.path.dirname(DLL), "rules"), os.path.join(self.etc, "rules"))
        for u in ("an", "binh"):
            os.makedirs(self.docs(u))
        cfg = {"Watch": {"Folders": [os.path.join(self.home, "*", "Documents")], "MetricsIntervalSeconds": 5,
                         "PatternRefreshSeconds": 2, "ReconcileIntervalMinutes": 0}}
        with open(os.path.join(self.etc, "appsettings.json"), "w") as f:
            json.dump(cfg, f)
        env = dict(os.environ, DLP_LAYOUT="system", DLP_SYSTEM_ROOT=self.root)
        self.proc = subprocess.Popen(["dotnet", DLL], env=env, cwd="/", stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.wait(lambda: "Worker running" in self.log(), 60, "service khởi động")

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.send_signal(signal.SIGINT)
            try:
                self.proc.wait(30)
            except subprocess.TimeoutExpired:
                self.proc.kill()

    def log(self):
        out = []
        if os.path.isdir(self.logdir):
            for n in sorted(os.listdir(self.logdir)):
                with open(os.path.join(self.logdir, n), encoding="utf-8", errors="replace") as f:
                    out.append(f.read())
        return "".join(out)

    def known(self):
        """Tập đường dẫn service đang có bản ghi."""
        if not os.path.exists(self.db):
            return set()
        try:
            con = sqlite3.connect("file:%s?mode=ro" % self.db, uri=True, timeout=5)
            rows = {r[0] for r in con.execute("SELECT path FROM file_state")}
            con.close()
            return rows
        except sqlite3.Error:
            return set()

    def scans(self, path):
        return self.log().count("[Perf] %s:" % path)

    @staticmethod
    def wait(cond, timeout, what):
        end = time.time() + timeout
        while time.time() < end:
            if cond():
                return True
            time.sleep(0.2)
        return cond()

    def check(self, name, ok, detail=""):
        self.results.append((name, ok, detail))
        print("  [%s] %s %s" % ("PASS" if ok else "FAIL", name, detail))

    @staticmethod
    def write(path, text):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)

    # ---- kịch bản ----
    def scenario_basic(self):
        d = self.docs("an")
        a = os.path.join(d, "a.txt")
        self.write(a, "Họ tên: Nguyễn Văn A\nSố CCCD: 001099012345\n")
        self.check("tạo file", self.wait(lambda: a in self.known(), 10, ""))
        n0 = self.scans(a)
        time.sleep(0.5)
        self.write(a, "Họ tên: Nguyễn Văn A\nSố CCCD: 001099012399\n")
        self.check("sửa file → quét lại", self.wait(lambda: self.scans(a) > n0, 10, ""))
        b = os.path.join(d, "b.txt")
        os.rename(a, b)
        self.check("đổi tên → bản ghi chuyển sang tên mới", self.wait(lambda: b in self.known() and a not in self.known(), 10, ""))
        os.remove(b)
        self.check("xoá file → xoá bản ghi", self.wait(lambda: b not in self.known(), 10, ""))
        sub = os.path.join(d, "du-an")
        f1, f2 = os.path.join(sub, "x.txt"), os.path.join(sub, "con", "y.txt")
        self.write(f1, "x")
        self.write(f2, "y")
        self.wait(lambda: f1 in self.known() and f2 in self.known(), 10, "")
        shutil.move(sub, os.path.join(self.root, "ngoai-vung"))
        self.check("chuyển thư mục ra ngoài → xoá bản ghi cả cây",
                   self.wait(lambda: f1 not in self.known() and f2 not in self.known(), 10, ""))

    def scenario_partial_write(self):
        p = os.path.join(self.docs("an"), "ghi-do.txt")
        full = "Dòng đầu.\n" * 2000 + "Số thẻ: 4111111111111111\n"
        with open(p, "w", encoding="utf-8") as f:
            f.write(full[: len(full) // 2])
            f.flush()
            time.sleep(1.5)  # đang ghi dở (file còn mở) — inotify chỉ báo khi ĐÓNG
            f.write(full[len(full) // 2:])
        ok = self.wait(lambda: p in self.known(), 10, "")
        time.sleep(1.5)
        n = self.scans(p)
        self.check("ghi dở (2 nửa cách 1,5 s) → quét đúng 1 lần, sau khi ghi xong", ok and n == 1, "(%d lần quét)" % n)

    def scenario_download(self):
        d = self.docs("binh")
        part = os.path.join(d, "hop-dong.txt.part")
        final = os.path.join(d, "hop-dong.txt")
        self.write(part, "Hợp đồng lao động\nSố CCCD: 079088001234\n")
        time.sleep(1)
        os.rename(part, final)
        ok = self.wait(lambda: final in self.known(), 10, "")
        self.check("tải về (.part → đổi tên): .part không quét, file thật được quét",
                   ok and part not in self.known() and self.scans(part) == 0)

    def scenario_unzip(self, n):
        target = os.path.join(self.docs("binh"), "giai-nen")
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as z:
            for i in range(n):
                z.writestr("thu-muc-%02d/file-%05d.txt" % (i % 50, i), "Tài liệu %d\n" % i)
        t0 = time.time()
        zipfile.ZipFile(io.BytesIO(buf.getvalue())).extractall(target)
        expected = {os.path.join(target, "thu-muc-%02d" % (i % 50), "file-%05d.txt" % i) for i in range(n)}
        ok = self.wait(lambda: expected <= self.known(), max(120, n // 10), "")
        elapsed = time.time() - t0
        missing = len(expected - self.known())
        log = self.log()
        counts = {}
        for m in re.finditer(r"\[Perf\] (\S+giai-nen\S+):", log):
            counts[m.group(1)] = counts.get(m.group(1), 0) + 1
        dup = sum(1 for c in counts.values() if c > 1)
        overflow = log.count("Buffer tràn")
        self.check("giải nén %d file → không sót" % n, ok and missing == 0, "(thiếu %d, %.0f s, %.0f file/s)" % (missing, elapsed, n / elapsed))
        self.check("giải nén %d file → không quét trùng" % n, dup == 0, "(%d file quét > 1 lần, %d lần tràn hàng đợi)" % (dup, overflow))
        shutil.rmtree(target)
        self.check("xoá cả thư mục giải nén → xoá hết bản ghi",
                   self.wait(lambda: not any("giai-nen" in p for p in self.known()), 60, ""))

    def scenario_recreate_documents(self):
        d = self.docs("an")
        shutil.rmtree(d)
        time.sleep(1)
        os.makedirs(d)
        time.sleep(8)  # vòng theo dõi lại (R6): 5 s rồi tăng dần
        p = os.path.join(d, "sau-khi-tao-lai.txt")
        self.write(p, "x")
        self.check("xoá rồi tạo lại Documents → tự theo dõi lại", self.wait(lambda: p in self.known(), 30, ""))

    def scenario_new_user(self):
        d = self.docs("chi-moi")
        p = os.path.join(d, "ho-so.txt")
        self.write(p, "Số CCCD: 001099012345")
        ok = self.wait(lambda: p in self.known(), 15, "")
        self.check("người dùng mới (khớp /home/*/Documents) → được theo dõi + quét", ok)
        p2 = os.path.join(d, "sau.txt")
        self.write(p2, "y")
        self.check("người dùng mới → file tạo sau cũng được bắt", self.wait(lambda: p2 in self.known(), 10, ""))

    def scenario_many_dirs(self, n):
        base = os.path.join(self.docs("an"), "nhieu-thu-muc")
        for i in range(n):
            os.makedirs(os.path.join(base, "c%03d" % (i // 100), "d%05d" % i))
        hidden = os.path.join(self.docs("an"), ".cache", "rac")
        for i in range(2000):
            os.makedirs(os.path.join(hidden, "r%05d" % i))  # không được tốn watch
        time.sleep(5)
        deep = os.path.join(base, "c%03d" % ((n - 1) // 100), "d%05d" % (n - 1), "sau-cung.txt")
        self.write(deep, "x")
        ok = self.wait(lambda: deep in self.known(), 15, "")
        time.sleep(6)
        m = re.findall(r"\[Metric\].*?watch (\d+)/(\d+) \(lỗi (\d+)\)", self.log())
        used, limit, failed = (int(x) for x in m[-1]) if m else (-1, -1, -1)
        self.check("%d thư mục mới + 2000 thư mục ẩn → file ở thư mục sâu nhất được bắt" % n, ok,
                   "(watch %d/%d, lỗi %d)" % (used, limit, failed))
        self.check("thư mục ẩn không tốn watch", used < n + 2000 and used >= n, "(watch %d)" % used)
        return used, limit, failed

    def measure_idle_cpu(self, seconds):
        def cpu():
            with open("/proc/%d/stat" % self.proc.pid) as f:
                parts = f.read().rsplit(")", 1)[1].split()
            return (int(parts[11]) + int(parts[12])) / TICKS  # utime + stime (giây)
        c0, t0 = cpu(), time.time()
        time.sleep(seconds)
        pct = (cpu() - c0) / (time.time() - t0) * 100
        self.check("CPU lúc rảnh %d s < 2 %%" % seconds, pct < 2, "(%.2f %%)" % pct)
        with open("/proc/%d/status" % self.proc.pid) as f:
            rss = next(int(l.split()[1]) for l in f if l.startswith("VmRSS"))
        print("  RAM (RSS): %.0f MB" % (rss / 1024))
        return pct, rss

    def latency(self):
        # Tách 2 loại chu kỳ metric: "đợt dồn" (> 1000 file xử lý trong 1 chu kỳ — giải nén / xoá hàng loạt) và bình thường.
        # Ngưỡng p95 ≤ 2 s của mục 8.1 áp cho lúc bình thường; trong đợt dồn, độ trễ = thời gian xếp hàng chờ trạm gác (Watch:MaxConcurrency,
        # D6 = 2) nên chỉ ghi nhận — tiêu chí của đợt dồn là "không sót, không trùng" (kiểm ở kịch bản giải nén).
        vals = [(int(n), int(b)) for n, b in re.findall(r"→ xử lý (\d+) \| trễ p50 \d+ ms, p95 (\d+) ms", self.log())]
        normal = max((p95 for n, p95 in vals if 0 < n <= 1000), default=0)
        burst = max((p95 for n, p95 in vals if n > 1000), default=0)
        self.check("độ trễ p95 lúc bình thường ≤ 2000 ms", normal <= 2000, "(p95 tệ nhất %d ms)" % normal)
        print("  [INFO] độ trễ p95 trong đợt dồn (> 1000 file / chu kỳ, MaxConcurrency=2): %d ms — chỉ ghi nhận" % burst)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--unzip", type=int, default=10000)
    ap.add_argument("--dirs", type=int, default=5000)
    ap.add_argument("--idle", type=int, default=60)
    ap.add_argument("--keep", action="store_true", help="giữ thư mục tạm để xem log")
    args = ap.parse_args()
    if not os.path.exists(DLL):
        sys.exit("Chưa build: " + DLL)
    r = Run(args)
    print("Thư mục tạm:", r.root)
    try:
        r.start()
        print("== Cơ bản"); r.scenario_basic()
        print("== Ghi dở"); r.scenario_partial_write()
        print("== Tải về"); r.scenario_download()
        print("== Giải nén"); r.scenario_unzip(args.unzip)
        print("== Xoá + tạo lại Documents"); r.scenario_recreate_documents()
        print("== Người dùng mới"); r.scenario_new_user()
        print("== Nhiều thư mục"); r.scenario_many_dirs(args.dirs)
        print("== CPU rảnh"); r.measure_idle_cpu(args.idle)
        print("== Độ trễ"); r.latency()
        print("== Metric (5 dòng cuối)")
        for line in [l for l in r.log().splitlines() if "[Metric]" in l][-5:]:
            print("  ", line[20:])
        errors = [l for l in r.log().splitlines() if "[Error]" in l]
        print("== Log [Error]: %d dòng" % len(errors))
        for l in errors[:5]:
            print("  ", l[20:240])
    finally:
        r.stop()
        if not args.keep:
            shutil.rmtree(r.root, ignore_errors=True)
    failed = [x for x in r.results if not x[1]]
    print("== %d/%d PASS" % (len(r.results) - len(failed), len(r.results)))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
