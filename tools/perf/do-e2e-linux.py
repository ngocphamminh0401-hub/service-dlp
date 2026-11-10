#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# [Thay đổi] File mới (đo hiệu năng Linux, 10/11/2026): bản Linux của tools/perf/Measure-DlpServiceE2E.ps1 — đo DlpService Linux
# THẬT (bản build Release, chạy như tiến trình riêng) từ đầu đến cuối trên rawdata có nhãn, để so với số đo Windows trong
# docs/danh-gia-service-e2e.md, docs/danh-gia-tai-nguyen.md, docs/danh-gia-xu-ly-dong-thoi.md.
#
# Kịch bản (giống bản Windows, bật/tắt bằng --phases):
#   warmup (10 file, không tính) → idle (nền 15 s) → seq (2 file × 16 lớp × 2 định dạng, thả từng file, chờ xong)
#   → rates (tải đều 5/20/50 file/giây × 120 s) → batches (thả cùng lúc lô 1/10/100/1000/5000 file bằng rename)
#   → burst (FastDrop: chép trước ra thư mục tạm cùng ổ, rename toàn bộ ~25.485 file vào thư mục theo dõi) → after (rảnh 120 s)
#   → restart (dừng bằng SIGTERM, khởi động lại, đo thời gian tới khi theo dõi + đếm file bị quét lại)
#
# Cách đo:
#   - Thời điểm "xong" của 1 file = lúc dòng [Perf] của file đó xuất hiện trong log (script tự đọc log mỗi 20 ms, vì log chỉ ghi
#     thời gian tới giây). Độ trễ đầu cuối = lúc xong − lúc file xuất hiện trong thư mục theo dõi (copy xong / rename xong).
#   - Thời gian trong service lấy từ chính dòng [Perf] (cache, đọc, băm, quét, phân loại, ghi DB, tổng).
#   - Tài nguyên: đọc /proc/<pid> mỗi 1 giây (CPU, RSS, số fd, số thread, byte đọc/ghi đĩa) + /proc/stat (CPU cả máy).
#   - Độ chính xác: đọc DB của service sau khi xong, so mức với nhãn trong _metadata.jsonl.
# KHÔNG in giá trị nhạy cảm: chỉ đọc đường dẫn + số đo trong log và mức trong DB.
#
# Cách dùng (Python 3.6+, cần .NET SDK):
#   python3 tools/perf/do-e2e-linux.py                         # đủ kịch bản, MaxConcurrency mặc định của bản Linux (2)
#   python3 tools/perf/do-e2e-linux.py --max-concurrency 6 --phases warmup,batches,burst
import argparse
import json
import os
import re
import shutil
import signal
import sqlite3
import statistics
import subprocess
import sys
import tempfile
import threading
import time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
HOST_PROJECT = os.path.join(REPO, "DlpService.Host.Linux")
LEVELS = ["Public", "Internal", "Confidential", "Restricted"]
LABEL_MAP = {"public": "Public", "internal": "Internal", "confidential": "Confidential", "top_secret": "Restricted"}
PERF_RE = re.compile(r"\[Perf\] (.+?): cache ([\d.,]+) ms, đọc ([\d.,]+) ms, băm ([\d.,]+) ms, quét ([\d.,]+) ms, "
                     r"phân loại ([\d.,]+) ms, ghi DB ([\d.,]+) ms, tổng ([\d.,]+) ms")
PERF_KEYS = ["cache", "read", "hash", "scan", "classify", "save", "total"]
CLK_TCK = os.sysconf("SC_CLK_TCK")
NCPU = os.cpu_count() or 1


# ==================================================================================================================
# HÀM PHỤ TRỢ (AI sinh): đọc nhãn, thống kê, đọc log, lấy mẫu tài nguyên
# ==================================================================================================================

def load_labelled(rawdata):
    """Danh sách (thư mục lớp, tên file, mức thật) của mọi file có nhãn VÀ có trên đĩa, sắp theo tên (ổn định giữa các lần chạy)."""
    items = []
    for d in sorted(os.listdir(rawdata)):
        meta = os.path.join(rawdata, d, "_metadata.jsonl")
        if not os.path.isfile(meta):
            continue
        for line in open(meta, encoding="utf-8"):
            if not line.strip():
                continue
            r = json.loads(line)
            for k in ("docx_path", "pdf_path"):
                name = (r.get(k) or "").replace("\\", "/").split("/")[-1]
                if name and os.path.isfile(os.path.join(rawdata, d, name)):
                    items.append((d, name, LABEL_MAP[r["label"]]))
    return sorted(items)


def pct(values, p):
    if not values:
        return float("nan")
    s = sorted(values)
    k = min(len(s) - 1, max(0, int(round(p / 100.0 * (len(s) - 1)))))
    return s[k]


def fmt(x, nd=1):
    return "—" if x is None or x != x else f"{x:,.{nd}f}".replace(",", " ").replace(".", ",").replace(" ", ".")


class LogTail(threading.Thread):
    """Đọc tăng dần mọi file dlpservice-*.log trong thư mục log (kể cả file xoay vòng .1.log, .2.log...)."""

    def __init__(self, folder):
        super().__init__(daemon=True)
        self.folder = folder
        self.offsets = {}
        self.partial = {}
        self.lock = threading.Lock()
        self.perf = []            # (thời điểm thấy, đường dẫn, dict số đo)
        self.lines = []           # (thời điểm thấy, dòng) — mọi dòng không phải [Perf]/[DLP]/[Conf]
        self.levels = {}
        self.stop_flag = False

    def run(self):
        while not self.stop_flag:
            self.poll()
            time.sleep(0.02)

    def poll(self):
        try:
            names = [n for n in os.listdir(self.folder) if n.startswith("dlpservice-") and n.endswith(".log")]
        except OSError:
            return
        now = time.time()
        for n in names:
            path = os.path.join(self.folder, n)
            off = self.offsets.get(path, 0)
            try:
                size = os.path.getsize(path)
                if size <= off:
                    continue
                with open(path, "rb") as f:
                    f.seek(off)
                    data = f.read(size - off)
            except OSError:
                continue
            self.offsets[path] = off + len(data)
            data = self.partial.pop(path, b"") + data
            parts = data.split(b"\n")
            if parts[-1]:
                self.partial[path] = parts[-1]
            with self.lock:
                for raw in parts[:-1]:
                    line = raw.decode("utf-8", errors="replace")
                    m = re.match(r"\S+ \S+ \[(\w+)\]", line)
                    if m:
                        self.levels[m.group(1)] = self.levels.get(m.group(1), 0) + 1
                    pm = PERF_RE.search(line)
                    if pm:
                        vals = [float(v.replace(",", ".")) for v in pm.groups()[1:]]
                        self.perf.append((now, pm.group(1), dict(zip(PERF_KEYS, vals))))
                    elif "[DLP]" not in line and "[Conf]" not in line:
                        self.lines.append((now, line))

    def perf_count(self):
        with self.lock:
            return len(self.perf)

    def find_line(self, needle, since):
        with self.lock:
            for t, line in self.lines:
                if t >= since and needle in line:
                    return t, line
        return None


class Sampler(threading.Thread):
    """Mỗi 1 giây: CPU / RSS / fd / thread / I/O của tiến trình service + CPU cả máy, gắn nhãn giai đoạn hiện tại."""

    def __init__(self):
        super().__init__(daemon=True)
        self.pid = None
        self.phase = "start"
        self.samples = []
        self.stop_flag = False

    @staticmethod
    def machine_cpu():
        with open("/proc/stat") as f:
            v = [int(x) for x in f.readline().split()[1:]]
        idle = v[3] + v[4]
        return sum(v), idle

    def proc(self):
        pid = self.pid
        with open(f"/proc/{pid}/stat") as f:
            st = f.read().rsplit(")", 1)[1].split()
        cpu = int(st[11]) + int(st[12])
        threads = int(st[17])
        rss = 0
        with open(f"/proc/{pid}/status") as f:
            for line in f:
                if line.startswith("VmRSS:"):
                    rss = int(line.split()[1]) / 1024.0
        fds = len(os.listdir(f"/proc/{pid}/fd"))
        rd = wr = 0
        try:
            with open(f"/proc/{pid}/io") as f:
                for line in f:
                    if line.startswith("read_bytes:"):
                        rd = int(line.split()[1])
                    elif line.startswith("write_bytes:"):
                        wr = int(line.split()[1])
        except OSError:
            pass
        return cpu, threads, rss, fds, rd, wr

    def run(self):
        prev = None
        while not self.stop_flag:
            t = time.time()
            try:
                if self.pid:
                    cpu, th, rss, fds, rd, wr = self.proc()
                    tot, idle = self.machine_cpu()
                    if prev and prev[0] == self.pid:
                        dt = t - prev[1]
                        svc_cores = (cpu - prev[2]) / CLK_TCK / dt
                        mach = 1 - (idle - prev[4]) / max(1, tot - prev[3])
                        self.samples.append(dict(t=t, phase=self.phase, svc_cores=svc_cores, svc_pct=svc_cores / NCPU * 100,
                                                 mach_pct=mach * 100, rss=rss, fds=fds, threads=th,
                                                 rd_mbs=(rd - prev[5]) / dt / 1e6, wr_mbs=(wr - prev[6]) / dt / 1e6))
                    prev = (self.pid, t, cpu, tot, idle, rd, wr)
            except (OSError, ValueError, IndexError):
                prev = None  # tiến trình vừa thoát / đang khởi động lại
            time.sleep(max(0.0, 1.0 - (time.time() - t)))


# ==================================================================================================================
# CHẠY SERVICE + CÁC KỊCH BẢN
# ==================================================================================================================

class Run:
    def __init__(self, args):
        self.args = args
        self.work = tempfile.mkdtemp(prefix="dlp-e2e-")
        self.svc = os.path.join(self.work, "svc")
        self.watch = os.path.join(self.work, "watch")
        self.staging = os.path.join(self.work, "staging")   # cùng ổ với watch, NẰM NGOÀI thư mục theo dõi
        os.makedirs(self.watch)
        os.makedirs(self.staging)
        self.proc = None
        self.tail = None
        self.sampler = Sampler()
        self.drops = {}          # đường dẫn tuyệt đối → (giai đoạn, thời điểm xuất hiện, mức thật)
        self.results = {}        # tên giai đoạn → dict kết quả
        self.phase_windows = []  # (giai đoạn, bắt đầu, kết thúc)

    # ---------- service ----------
    def publish(self):
        # Build Release rồi chép output ra thư mục tạm → thư mục logs/ và data/ sạch cho mỗi lượt đo, không đụng bản build
        # của người dùng (FileLogger ghi vào <thư mục chạy>/logs ở chế độ Portable).
        print("Build bản Release ...", flush=True)
        subprocess.run(["dotnet", "build", HOST_PROJECT, "-c", "Release", "--nologo", "-v", "q"],
                       check=True, stdout=subprocess.DEVNULL)
        out = os.path.join(HOST_PROJECT, "bin", "Release", "net10.0")
        shutil.copytree(out, self.svc, ignore=shutil.ignore_patterns("logs", "data"))
        self.tail = LogTail(os.path.join(self.svc, "logs"))
        self.tail.start()
        self.sampler.start()

    def start(self):
        env = dict(os.environ)
        env["Watch__Folders__0"] = self.watch
        env["Watch__MaxConcurrency"] = str(self.args.max_concurrency)
        env["Llm__Enabled"] = "false"
        env["DOTNET_ENVIRONMENT"] = "Production"
        t0 = time.time()
        self.proc = subprocess.Popen(["dotnet", os.path.join(self.svc, "dlp-service.dll")], cwd=self.svc, env=env,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.sampler.pid = self.proc.pid
        watching = self.wait_line("Đang theo dõi thư mục", t0, 60)
        scanned = self.wait_line("[FileWatcher] Quét (", t0, 600)
        return t0, watching, scanned

    def stop(self):
        t0 = time.time()
        self.proc.send_signal(signal.SIGTERM)
        try:
            self.proc.wait(60)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            self.proc.wait()
        return time.time() - t0

    def wait_line(self, needle, since, timeout):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.proc.poll() is not None:
                sys.exit(f"Service thoát sớm (mã {self.proc.returncode}) — xem log trong {self.svc}/logs")
            hit = self.tail.find_line(needle, since)
            if hit:
                return hit[0]
            time.sleep(0.05)
        sys.exit(f"Hết thời gian chờ dòng log: {needle}")

    # ---------- thả file ----------
    def drop_copy(self, phase, src, rel, truth):
        dst = os.path.join(self.watch, phase, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        shutil.copyfile(src, dst)
        self.drops[dst] = (phase, time.time(), truth)

    def stage(self, phase, items):
        """Chép trước ra thư mục tạm (không bị theo dõi) — để thả bằng rename (vài ms cho hàng nghìn file)."""
        out = []
        for d, n, truth in items:
            src = os.path.join(self.args.rawdata, d, n)
            st = os.path.join(self.staging, phase, d, n)
            os.makedirs(os.path.dirname(st), exist_ok=True)
            shutil.copyfile(src, st)
            out.append((st, os.path.join(self.watch, phase, d, n), truth))
        return out

    def drop_rename(self, phase, staged):
        for st, dst, truth in staged:
            os.makedirs(os.path.dirname(dst), exist_ok=True)
        t_first = time.time()
        for st, dst, truth in staged:
            os.rename(st, dst)
            self.drops[dst] = (phase, time.time(), truth)
        return t_first, time.time()

    def wait_done(self, paths, timeout, quiet=3.0):
        """Chờ tới khi mọi đường dẫn có dòng [Perf]; trả thời điểm xong của từng file."""
        paths = set(paths)
        deadline = time.time() + timeout
        done = {}
        idx = 0
        while time.time() < deadline:
            with self.tail.lock:
                new = self.tail.perf[idx:]
                idx = len(self.tail.perf)
            for t, p, _ in new:
                if p in paths and p not in done:
                    done[p] = t
            if len(done) == len(paths):
                return done
            if self.proc.poll() is not None:
                sys.exit("Service thoát giữa chừng")
            time.sleep(0.05)
        print(f"  ! hết thời gian: {len(done)}/{len(paths)} file xong", flush=True)
        return done

    def set_phase(self, name):
        self.sampler.phase = name
        self.phase_windows.append([name, time.time(), None])
        print(f"== {name}", flush=True)

    def end_phase(self):
        self.phase_windows[-1][2] = time.time()

    def perf_of(self, paths):
        paths = set(paths)
        with self.tail.lock:
            return [m for _, p, m in self.tail.perf if p in paths]

    def latency_summary(self, paths, done):
        lat = [(done[p] - self.drops[p][1]) * 1000 for p in paths if p in done]
        perf = self.perf_of(paths)
        r = dict(n=len(paths), done=len(done), lat=lat,
                 lat_p50=pct(lat, 50), lat_p95=pct(lat, 95), lat_max=max(lat) if lat else float("nan"),
                 lat_mean=statistics.mean(lat) if lat else float("nan"))
        for k in PERF_KEYS:
            vals = [m[k] for m in perf]
            r[f"{k}_mean"] = statistics.mean(vals) if vals else float("nan")
            r[f"{k}_p50"] = pct(vals, 50)
            r[f"{k}_p95"] = pct(vals, 95)
            r[f"{k}_max"] = max(vals) if vals else float("nan")
        return r

    # ---------- kịch bản ----------
    def run(self):
        a = self.args
        labelled = load_labelled(a.rawdata)
        by_class = {}
        for it in labelled:
            by_class.setdefault((it[0], it[1].rsplit(".", 1)[1]), []).append(it)
        phases = a.phases.split(",")
        print(f"{len(labelled)} file có nhãn · {NCPU} CPU · MaxConcurrency {a.max_concurrency} · kịch bản {phases}", flush=True)

        self.publish()
        t0, t_watch, t_scan = self.start()
        self.results["startup"] = dict(watching=t_watch - t0, scanned=t_scan - t0)
        cursor = {k: 0 for k in by_class}

        def take(per_class):
            """Lấy file kế tiếp (chưa dùng ở giai đoạn trước) của mỗi lớp × định dạng."""
            out = []
            for k in sorted(by_class):
                lst = by_class[k]
                for _ in range(per_class):
                    out.append(lst[cursor[k] % len(lst)])
                    cursor[k] += 1
            return out

        if "warmup" in phases:
            self.set_phase("warmup")
            items = labelled[:: max(1, len(labelled) // 10)][:10]
            for d, n, truth in items:
                self.drop_copy("warmup", os.path.join(a.rawdata, d, n), os.path.join(d, n), truth)
            self.wait_done([p for p, v in self.drops.items() if v[0] == "warmup"], 120)
            self.end_phase()

        if "idle" in phases:
            self.set_phase("idle")
            time.sleep(15)
            self.end_phase()

        if "seq" in phases:
            self.set_phase("seq")
            paths = []
            for d, n, truth in take(2):
                self.drop_copy("seq", os.path.join(a.rawdata, d, n), os.path.join(d, n), truth)
                p = os.path.join(self.watch, "seq", d, n)
                paths.append(p)
                self.wait_done([p], 60)
            done = self.wait_done(paths, 5)
            self.results["seq"] = self.latency_summary(paths, done)
            self.end_phase()

        if "rates" in phases:
            for rate in [int(x) for x in a.rates.split(",")]:
                name = f"rate{rate}"
                self.set_phase(name)
                n = rate * a.step_seconds
                items = take(-(-n // len(by_class)))[:n]
                paths = []
                start = time.time()
                for i, (d, nm, truth) in enumerate(items):
                    target = start + i / float(rate)
                    delay = target - time.time()
                    if delay > 0:
                        time.sleep(delay)
                    self.drop_copy(name, os.path.join(a.rawdata, d, nm), os.path.join(d, nm), truth)
                    paths.append(os.path.join(self.watch, name, d, nm))
                actual = len(items) / max(1e-9, time.time() - start)
                done = self.wait_done(paths, 600)
                r = self.latency_summary(paths, done)
                r["actual_rate"] = actual
                self.results[name] = r
                self.end_phase()

        if "batches" in phases:
            for size in [int(x) for x in a.batches.split(",")]:
                name = f"batch{size}"
                items = take(-(-size // len(by_class)))[:size]
                staged = self.stage(name, items)
                time.sleep(3)  # để service rảnh trước khi thả
                self.set_phase(name)
                t1, t2 = self.drop_rename(name, staged)
                paths = [s[1] for s in staged]
                done = self.wait_done(paths, 3600)
                r = self.latency_summary(paths, done)
                last = max(done.values()) if done else float("nan")
                r.update(drop_s=t2 - t1, all_s=last - t1, tput=len(done) / max(1e-9, last - t1))
                self.results[name] = r
                self.end_phase()

        if "burst" in phases:
            items = labelled if not a.burst_limit else labelled[: a.burst_limit]
            print(f"  chép trước {len(items)} file ra thư mục tạm ...", flush=True)
            staged = self.stage("burst", items)
            time.sleep(5)
            self.set_phase("burst")
            t1, t2 = self.drop_rename("burst", staged)
            paths = [s[1] for s in staged]
            done = self.wait_done(paths, a.burst_timeout)
            r = self.latency_summary(paths, done)
            last = max(done.values()) if done else float("nan")
            r.update(drop_s=t2 - t1, all_s=last - t1, after_drop_s=last - t2, tput=len(done) / max(1e-9, last - t1))
            self.results["burst"] = r
            self.end_phase()

        if "after" in phases:
            self.set_phase("after")
            time.sleep(a.idle_after)
            self.end_phase()

        if "restart" in phases:
            self.set_phase("restart")
            before = self.tail.perf_count()
            stop_s = self.stop()
            t0, t_watch, t_scan = self.start()
            time.sleep(20)
            scan_line = self.tail.find_line("[FileWatcher] Quét (", t0)
            self.results["restart"] = dict(stop_s=stop_s, watching=t_watch - t0, scanned=t_scan - t0,
                                           rescanned=self.tail.perf_count() - before,
                                           scan_line=re.sub(r"/\S+", "<thư mục>", scan_line[1]) if scan_line else "")
            self.end_phase()

        self.stop()
        time.sleep(0.5)
        self.tail.poll()
        self.check_integrity()

    # ---------- kiểm tra sau cùng ----------
    def check_integrity(self):
        with self.tail.lock:
            perf = list(self.tail.perf)
        seen = {}
        for _, p, _ in perf:
            seen[p] = seen.get(p, 0) + 1
        dropped = set(self.drops)
        lost = [p for p in dropped if p not in seen]
        dup = [p for p in dropped if seen.get(p, 0) > 1]
        # độ chính xác: mức trong DB của service
        db = os.path.join(self.svc, "data", "dlp.db")
        pred = {}
        con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
        for path, labels in con.execute("SELECT f.path, r.labels_json FROM scan_result r JOIN file_state f ON f.file_id = r.file_id"):
            pred[path] = json.loads(labels)["level"] if labels else "Public"
        con.close()
        acc = {}
        for p, (phase, _, truth) in self.drops.items():
            if p in pred:
                a = acc.setdefault(phase, [0, 0])
                a[0] += pred[p] == truth
                a[1] += 1
        with self.tail.lock:
            lines = list(self.tail.lines)
        self.results["integrity"] = dict(
            dropped=len(dropped), lost=len(lost), dup=len(dup), levels=dict(self.tail.levels),
            db_rows=len(pred), accuracy=acc,
            overflow=sum("tràn" in l.lower() and "[Metric]" not in l and "Warning" in l for _, l in lines),
            errors=[re.sub(r"/\S+", "<đường dẫn>", l)[:200] for _, l in lines if "[Error]" in l or "[Critical]" in l][:10],
            metric=[re.sub(r"^\S+ \S+ \[\w+\] [\w.]+: ", "", l) for _, l in lines if "[Metric]" in l])

    # ---------- báo cáo ----------
    def phase_resources(self, name):
        s = [x for x in self.sampler.samples if x["phase"] == name]
        if not s:
            return None
        return dict(n=len(s), svc_pct=statistics.mean(x["svc_pct"] for x in s), svc_pct_max=max(x["svc_pct"] for x in s),
                    svc_cores=statistics.mean(x["svc_cores"] for x in s), mach=statistics.mean(x["mach_pct"] for x in s),
                    rss0=s[0]["rss"], rss_max=max(x["rss"] for x in s), rss1=s[-1]["rss"],
                    fds0=s[0]["fds"], fds_max=max(x["fds"] for x in s), fds1=s[-1]["fds"],
                    th_max=max(x["threads"] for x in s),
                    rd=statistics.mean(x["rd_mbs"] for x in s), wr=statistics.mean(x["wr_mbs"] for x in s))

    def report(self):
        a = self.args
        stamp = time.strftime("%Y%m%d-%H%M%S", time.localtime(self.phase_windows[0][1] if self.phase_windows else time.time()))
        os.makedirs(a.out, exist_ok=True)
        base = os.path.join(a.out, f"e2e-linux-{stamp}-mc{a.max_concurrency}")
        R = self.results
        md = [f"# Đo E2E DlpService Linux — lượt {stamp}", "",
              f"- Máy: {NCPU} CPU, RAM {os.sysconf('SC_PAGE_SIZE') * os.sysconf('SC_PHYS_PAGES') / 2**30:.1f} GB · "
              f"`Watch:MaxConcurrency = {a.max_concurrency}` · LLM tắt · bản build Release", ""]
        st = R.get("startup", {})
        md += [f"Khởi động: tới khi theo dõi {fmt(st.get('watching'), 2)} s, xong quét lượt đầu {fmt(st.get('scanned'), 2)} s", ""]

        md += ["## Độ trễ và thời gian trong service", "",
               "| Giai đoạn | Xong | Đầu cuối p50 / p95 / max (ms) | Trong service TB / p50 / p95 / max (ms) | Đọc TB | Quét TB | "
               "Phân loại TB | Ghi DB TB | Cache TB | Tải thực / thông lượng |", "|---|---|---|---|---|---|---|---|---|---|"]
        for name, r in R.items():
            if "lat" not in r:
                continue
            extra = ""
            if "actual_rate" in r:
                extra = f"{fmt(r['actual_rate'])} file/s"
            if "tput" in r:
                extra = f"thả {fmt(r['drop_s'], 2)} s · cả lô {fmt(r['all_s'], 1)} s · **{fmt(r['tput'])} file/s**"
            md.append(f"| {name} | {r['done']}/{r['n']} | {fmt(r['lat_p50'], 0)} / {fmt(r['lat_p95'], 0)} / {fmt(r['lat_max'], 0)} | "
                      f"{fmt(r['total_mean'])} / {fmt(r['total_p50'])} / {fmt(r['total_p95'])} / {fmt(r['total_max'])} | "
                      f"{fmt(r['read_mean'])} | {fmt(r['scan_mean'])} | {fmt(r['classify_mean'])} | {fmt(r['save_mean'])} | "
                      f"{fmt(r['cache_mean'])} | {extra} |")
        if "seq" in R:
            r = R["seq"]
            md += ["", "Tuần tự — từng bước (TB / p50 / p95 ms): " + " · ".join(
                f"{k} {fmt(r[k + '_mean'])} / {fmt(r[k + '_p50'])} / {fmt(r[k + '_p95'])}" for k in PERF_KEYS)]
        if "burst" in R:
            r = R["burst"]
            md += ["", f"Burst: thả {r['n']} file trong {fmt(r['drop_s'], 1)} s; file cuối xong sau {fmt(r['all_s'], 1)} s "
                       f"({fmt(r['after_drop_s'], 1)} s sau khi thả xong) → **{fmt(r['tput'])} file/giây**. Từng bước (TB / p95 ms): " +
                   " · ".join(f"{k} {fmt(r[k + '_mean'])} / {fmt(r[k + '_p95'])}" for k in PERF_KEYS)]

        md += ["", "## Tài nguyên theo giai đoạn", "", f"CPU tính theo % cả máy (100 % = {NCPU} CPU) và theo số nhân.", "",
               "| Giai đoạn | Mẫu | CPU service TB / max | Nhân TB | CPU máy TB | RSS (MB) đầu → max → cuối | fd đầu → max → cuối | "
               "Thread max | Đọc / ghi đĩa (MB/s) |", "|---|---|---|---|---|---|---|---|---|"]
        for name, _, _ in self.phase_windows:
            x = self.phase_resources(name)
            if x:
                md.append(f"| {name} | {x['n']} | {fmt(x['svc_pct'])} % / {fmt(x['svc_pct_max'])} % | {fmt(x['svc_cores'], 2)} | "
                          f"{fmt(x['mach'])} % | {fmt(x['rss0'], 0)} → {fmt(x['rss_max'], 0)} → {fmt(x['rss1'], 0)} | "
                          f"{x['fds0']} → {x['fds_max']} → {x['fds1']} | {x['th_max']} | {fmt(x['rd'], 2)} / {fmt(x['wr'], 2)} |")
        if "after" in R or any(p[0] == "after" for p in self.phase_windows):
            s = [x for x in self.sampler.samples if x["phase"] == "burst"]
            if len(s) > 20:
                half = s[len(s) // 2:]
                slope = (half[-1]["rss"] - half[0]["rss"]) / max(1e-9, (half[-1]["t"] - half[0]["t"]) / 60)
                md.append(f"\nĐộ dốc RSS nửa sau burst: {fmt(slope, 1)} MB/phút")

        it = R.get("integrity", {})
        md += ["", "## Độ ổn định", "", "| Kiểm tra | Kết quả |", "|---|---|",
               f"| File đã thả / mất / xử lý trùng | {it.get('dropped')} / **{it.get('lost')}** / **{it.get('dup')}** |",
               f"| Dòng log theo mức | {it.get('levels')} |",
               f"| Cảnh báo tràn sự kiện | {it.get('overflow')} |",
               f"| Lỗi (Error/Critical) | {len(it.get('errors', []))} |"]
        if "restart" in R:
            r = R["restart"]
            md.append(f"| Khởi động lại: dừng / tới khi theo dõi / xong quét lượt đầu | {fmt(r['stop_s'], 2)} s / "
                      f"{fmt(r['watching'], 2)} s / {fmt(r['scanned'], 2)} s |")
            md.append(f"| Sau khởi động lại: file bị quét lại | **{r['rescanned']}** |")
            md.append(f"| Dòng quét lượt đầu sau khởi động lại | {r['scan_line'][-220:]} |")
        acc = it.get("accuracy", {})
        if acc:
            md += ["", "Độ chính xác (mức trong DB so với nhãn): " + " · ".join(
                f"{k} {v[0]}/{v[1]} = {fmt(100.0 * v[0] / v[1])} %" for k, v in acc.items())]
        for e in it.get("errors", []):
            md.append(f"- {e}")
        if it.get("metric"):
            md += ["", "Dòng [Metric] (watcher):", ""] + [f"- {m}" for m in it["metric"]]

        with open(base + "-report.md", "w", encoding="utf-8") as f:
            f.write("\n".join(md) + "\n")
        with open(base + "-samples.csv", "w", encoding="utf-8") as f:
            f.write("t,phase,svc_pct,svc_cores,mach_pct,rss_mb,fds,threads,rd_mbs,wr_mbs\n")
            for x in self.sampler.samples:
                f.write(f"{x['t']:.2f},{x['phase']},{x['svc_pct']:.2f},{x['svc_cores']:.3f},{x['mach_pct']:.2f},"
                        f"{x['rss']:.1f},{x['fds']},{x['threads']},{x['rd_mbs']:.3f},{x['wr_mbs']:.3f}\n")
        with open(base + "-files.csv", "w", encoding="utf-8") as f:
            f.write("phase,class,file,latency_ms\n")
            done = {}
            for t, p, _ in self.tail.perf:
                done.setdefault(p, t)
            for p, (phase, t, _) in self.drops.items():
                rel = os.path.relpath(p, os.path.join(self.watch, phase))
                lat = f"{(done[p] - t) * 1000:.1f}" if p in done else ""
                f.write(f"{phase},{os.path.dirname(rel)},{os.path.basename(rel)},{lat}\n")
        print("\n".join(md))
        print(f"\nĐã ghi {base}-report.md / -samples.csv / -files.csv")

    def cleanup(self):
        if self.proc and self.proc.poll() is None:
            self.proc.kill()
        if self.tail:
            self.tail.stop_flag = True
        self.sampler.stop_flag = True
        if not self.args.keep:
            shutil.rmtree(self.work, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser(description="Đo E2E DlpService Linux (bản Linux của Measure-DlpServiceE2E.ps1)")
    ap.add_argument("--rawdata", default=os.environ.get("DLP_RAWDATA", os.path.expanduser("~/dlp-rawdata")))
    ap.add_argument("--max-concurrency", type=int, default=2, help="Watch:MaxConcurrency (mặc định bản Linux = 2, Windows = 6)")
    ap.add_argument("--phases", default="warmup,idle,seq,rates,batches,burst,after,restart")
    ap.add_argument("--rates", default="5,20,50")
    ap.add_argument("--step-seconds", type=int, default=120)
    ap.add_argument("--batches", default="1,10,100,1000,5000")
    ap.add_argument("--burst-limit", type=int, default=0, help="0 = toàn bộ file có nhãn")
    ap.add_argument("--burst-timeout", type=int, default=7200)
    ap.add_argument("--idle-after", type=int, default=120)
    ap.add_argument("--out", default=os.path.join(REPO, "tools", "perf", "results"))
    ap.add_argument("--keep", action="store_true", help="giữ thư mục tạm (service, DB, log) sau khi đo")
    args = ap.parse_args()
    run = Run(args)
    try:
        run.run()
        run.report()
    finally:
        run.cleanup()


if __name__ == "__main__":
    main()
