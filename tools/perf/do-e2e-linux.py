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
#
# [Thay đổi] Benchmark T08–T11 (18/11/2026) — thêm, KHÔNG đổi cách chạy/kết quả của các pha cũ:
#   Pha mới (cần --dataset = thư mục bộ full/smoke của tools/perf/DlpBenchData cho large/mixed/sustained):
#     idle-long     rảnh --idle-long-seconds (S0, mặc định 1800 s)
#     large         S3: mỗi file lớn thả 2 bản CÙNG LÚC (= MaxConcurrency), rồi "large-all" thả mọi file lớn cùng lúc (đỉnh RSS)
#     sustained     S4: tải đều --sustained-rate file/s trong --sustained-seconds, cứ --sustained-large-every s chen 1 file lớn
#     cache         T09: C1 file mới · C4 chỉ đổi mtime (touch) · C3 đổi nội dung · C5 cùng nội dung khác đường dẫn (cần --debug-log)
#     overflow      Q2/Q3: thả --overflow-count file cùng lúc (vượt Watch:MaxPendingFiles, đặt bằng --max-pending) → không được mất file
#     mixed         Q4: 2 file nặng + 1.000 file nhỏ cùng lúc → độ trễ file nhỏ khi 2 chỗ ở trạm gác bận file nặng
#     restartburst  Q5: khởi động lại rồi thả ngay 1.000 file trong lúc quét khởi động
#   Tham số mới: --work-dir (thư mục tạm, VD /data) · --metrics-seconds · --max-pending · --debug-log · --prod-profile (systemd-run, cần
#   sudo đã xác thực) · --counters (dotnet-counters CSV) · --no-build · --label · --gcdump-delay (T11: chụp heap N giây sau khi thả large-all)
#   File ra thêm: -queue.csv (tách từ dòng [Metric]), -counters.csv, -cache.csv
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
# [Thay đổi] T10: dòng [Metric] của FileWatcherService.LogMetrics → số liệu hàng chờ theo thời gian
METRIC_RE = re.compile(r"\[Metric\] (\d+)s: .*?→ xử lý (\d+) \| trễ p50 (\d+) ms, p95 (\d+) ms, max (\d+) ms \| đang chờ (\d+)/(\d+) "
                       r"\(lâu nhất (\d+) ms\), tràn (\d+), thư mục chờ quét bù (\d+)")
# [Thay đổi] T09: dòng Debug của FileProcessor khi bỏ qua file không đổi (chỉ có khi --debug-log)
UNCHANGED_RE = re.compile(r"\[FileWatcher\] (.+?): (?:nội dung không đổi \(file_hash\)|không đổi \(size/mtime/dấu thay đổi\)), bỏ qua")
DOTNET = os.environ.get("DOTNET", shutil.which("dotnet") or os.path.expanduser("~/.dotnet/dotnet"))
TOOLS_PERF = os.path.dirname(os.path.abspath(__file__))
# [Thay đổi] T08 S3: file lớn trong bộ DlpBenchData dùng cho large / mixed / sustained (tên tương đối; thiếu file nào thì bỏ file đó)
LARGE_FILES = ["D-pdf/pdf-text-500p.pdf", "D-pdf/pdf-text-1000p.pdf", "D-pdf/pdf-scan-200p.pdf", "D-office/docx-xml20mb.docx",
               "D-office/pptx-500slides.pptx", "D-txt/txt-9.5mb-utf8.txt", "D-csv/csv-30krows.csv", "D-kw/kw-2M.txt",
               "D-match/match-2M-50kv.txt",
               # bộ smoke (khi chạy thử --dry-run)
               "D-pdf/pdf-text-10p.pdf", "D-office/docx-xml2mb.docx", "D-csv/csv-5krows.csv", "D-match/match-200k-1kv.txt"]
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
        self.unchanged = []       # [Thay đổi] T09: (thời điểm thấy, đường dẫn) — file bỏ qua vì không đổi (cần --debug-log)
        self.metrics = []         # [Thay đổi] T10: (thời điểm thấy, dict số liệu hàng chờ) từ dòng [Metric]
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
                        um = UNCHANGED_RE.search(line)
                        if um:
                            self.unchanged.append((now, um.group(1)))
                            continue  # không giữ dòng Debug trong lines (rất nhiều, chỉ cần đếm)
                        mm = METRIC_RE.search(line)
                        if mm:
                            keys = ["span_s", "processed", "lat_p50", "lat_p95", "lat_max", "pending", "capacity", "oldest_ms", "overflow", "dirty"]
                            self.metrics.append((now, dict(zip(keys, [int(v) for v in mm.groups()]))))
                        if " [Debug] " in line:
                            continue  # dòng Debug khác (chi tiết chỗ khớp đã che) — không cần giữ
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
        # [Thay đổi] T08: --work-dir đặt thư mục tạm lên đĩa đo (VD /data) — burst chép ~2 GB, ổ hệ thống có thể không đủ
        if args.work_dir:
            os.makedirs(args.work_dir, exist_ok=True)
        self.work = tempfile.mkdtemp(prefix="dlp-e2e-", dir=args.work_dir or None)
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
        self.svc_pid = None      # [Thay đổi] PID thật của dotnet (khác self.proc.pid khi chạy qua sudo/systemd-run)
        self.counters = None     # [Thay đổi] tiến trình dotnet-counters đang gắn (nếu --counters)
        self.counter_files = []
        self.cache = {}          # [Thay đổi] T09: kết quả từng trường hợp cache

    # ---------- service ----------
    def publish(self):
        # Build Release rồi chép output ra thư mục tạm → thư mục logs/ và data/ sạch cho mỗi lượt đo, không đụng bản build
        # của người dùng (FileLogger ghi vào <thư mục chạy>/logs ở chế độ Portable).
        if not self.args.no_build:  # [Thay đổi] --no-build: session đã build 1 lần, các lượt sau dùng lại
            print("Build bản Release ...", flush=True)
            subprocess.run([DOTNET, "build", HOST_PROJECT, "-c", "Release", "--nologo", "-v", "q"],
                           check=True, stdout=subprocess.DEVNULL)
        out = self.args.svc_src or os.path.join(HOST_PROJECT, "bin", "Release", "net10.0")  # [Thay đổi] Vòng 5: --svc-src
        shutil.copytree(out, self.svc, ignore=shutil.ignore_patterns("logs", "data"))
        self.tail = LogTail(os.path.join(self.svc, "logs"))
        self.tail.start()
        self.sampler.start()

    def service_env(self):
        """[Thay đổi] Biến môi trường cấu hình service cho lượt đo (ghi đè appsettings.json, xem Worker.cs)."""
        a = self.args
        env = {"Watch__Folders__0": self.watch, "Watch__MaxConcurrency": str(a.max_concurrency), "Llm__Enabled": "false",
               "DOTNET_ENVIRONMENT": "Production"}
        if a.metrics_seconds:
            env["Watch__MetricsIntervalSeconds"] = str(a.metrics_seconds)
        if a.max_pending:
            env["Watch__MaxPendingFiles"] = str(a.max_pending)
        if a.debug_log:
            env["Logging__LogLevel__DlpService.Worker"] = "Debug"  # chỉ category của pipeline, không bật Debug của Microsoft.*
        return env

    def start(self):
        t0 = self.launch()
        watching = self.wait_line("Đang theo dõi thư mục", t0, 60)
        scanned = self.wait_line("[FileWatcher] Quét (", t0, 600)
        self.start_counters()
        return t0, watching, scanned

    def start_no_wait(self):
        """[Thay đổi] Q5: khởi chạy service, KHÔNG chờ quét khởi động xong."""
        t0 = self.launch()
        self.start_counters()
        return t0

    def launch(self):
        cfg = self.service_env()
        dll = os.path.join(self.svc, "dlp-service.dll")
        t0 = time.time()
        if self.args.prod_profile:
            # [Thay đổi] P-prod: cùng giới hạn như unit thật (packaging/linux/systemd/dlp-service.service). cgroup v1 (systemd 237) dùng
            # MemoryLimit (MemoryMax không có tác dụng). Nice/IOSchedulingClass không áp được cho scope → nice + ionice trong lệnh.
            # sudo xoá biến môi trường → truyền TƯỜNG MINH qua env; runuser hạ quyền về user hiện tại (Ubuntu 18.04 không có setpriv).
            keep = {k: os.environ[k] for k in ("HOME", "PATH", "DOTNET_ROOT", "LANG") if k in os.environ}
            keep.update(cfg)
            cmd = ["sudo", "-n", "systemd-run", "--scope", "--quiet", "-p", "CPUQuota=30%", "-p", "MemoryLimit=512M", "--",
                   "runuser", "-u", os.environ.get("USER") or os.getlogin(), "--",
                   "env"] + [f"{k}={v}" for k, v in keep.items()] + ["nice", "-n", "10", "ionice", "-c3", DOTNET, dll]
            self.proc = subprocess.Popen(cmd, cwd=self.svc, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            self.svc_pid = self.find_service_pid(dll, 30)
        else:
            env = dict(os.environ)
            env.update(cfg)
            self.proc = subprocess.Popen([DOTNET, dll], cwd=self.svc, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            self.svc_pid = self.proc.pid
        self.sampler.pid = self.svc_pid
        return t0

    def find_service_pid(self, dll, timeout):
        """[Thay đổi] P-prod: PID của tiến trình dotnet thật (con cháu của sudo → systemd-run → runuser)."""
        deadline = time.time() + timeout
        while time.time() < deadline:
            out = subprocess.run(["pgrep", "-u", str(os.getuid()), "-f", dll], stdout=subprocess.PIPE, universal_newlines=True).stdout.split()
            pids = [int(x) for x in out if int(x) != os.getpid()]
            for pid in pids:
                try:
                    with open(f"/proc/{pid}/cmdline", "rb") as f:
                        argv = f.read().split(b"\0")
                    if argv and os.path.basename(argv[0].decode()) == "dotnet":
                        return pid
                except OSError:
                    pass
            if self.proc.poll() is not None:
                sys.exit(f"systemd-run thoát sớm (mã {self.proc.returncode}) — sudo đã xác thực chưa (sudo -v)?")
            time.sleep(0.2)
        sys.exit("Không tìm thấy tiến trình dotnet của service (P-prod)")

    def start_counters(self):
        """[Thay đổi] T08: gắn dotnet-counters (CSV, 1 s) vào service — chỉ P-dev (bản thân công cụ tốn CPU, không dùng khi kiểm chỉ tiêu)."""
        if not self.args.counters:
            return
        path = os.path.join(self.work, f"counters-{len(self.counter_files)}.csv")
        self.counter_files.append(path)
        self.counters = subprocess.Popen(
            [DOTNET, "tool", "run", "dotnet-counters", "collect", "-p", str(self.svc_pid), "--refresh-interval", "1",
             "--format", "csv", "-o", path, "--counters", "System.Runtime"],
            cwd=TOOLS_PERF, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def stop_counters(self):
        if self.counters and self.counters.poll() is None:
            self.counters.send_signal(signal.SIGINT)  # dotnet-counters ghi nốt file khi nhận Ctrl+C
            try:
                self.counters.wait(15)
            except subprocess.TimeoutExpired:
                self.counters.kill()
        self.counters = None

    def stop(self):
        self.stop_counters()
        t0 = time.time()
        # [Thay đổi] gửi SIGTERM tới đúng tiến trình dotnet (P-prod: self.proc là sudo, cùng user nên được phép gửi tín hiệu cho dotnet)
        os.kill(self.svc_pid, signal.SIGTERM)
        try:
            self.proc.wait(60)
        except subprocess.TimeoutExpired:
            self.kill_service()
            self.proc.wait()
        return time.time() - t0

    def kill_service(self):
        # [Thay đổi] kill thẳng dotnet: ở P-prod self.proc là sudo (root) — user thường không kill được
        try:
            os.kill(self.svc_pid, signal.SIGKILL)
        except (OSError, TypeError):
            pass

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

    def wait_done(self, paths, timeout, quiet=3.0, since=0.0):
        """Chờ tới khi mọi đường dẫn có dòng [Perf]; trả thời điểm xong của từng file.
        [Thay đổi] since: chỉ tính dòng [Perf] thấy SAU thời điểm này (T09: cùng đường dẫn được quét lại nhiều lần)."""
        paths = set(paths)
        deadline = time.time() + timeout
        done = {}
        idx = 0
        while time.time() < deadline:
            with self.tail.lock:
                new = self.tail.perf[idx:]
                idx = len(self.tail.perf)
            for t, p, _ in new:
                if p in paths and p not in done and t >= since:
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


    # ---------- [Thay đổi] phụ trợ cho các pha mới ----------
    def stage_files(self, phase, files):
        """Chép trước file bất kỳ (src, tên đích) ra thư mục tạm — dùng cho file DlpBenchData (không có nhãn)."""
        out = []
        for src, name in files:
            st = os.path.join(self.staging, phase, name)
            os.makedirs(os.path.dirname(st), exist_ok=True)
            shutil.copyfile(src, st)
            out.append((st, os.path.join(self.watch, phase, name), None))
        return out

    def svc_cpu_s(self):
        """CPU (user + sys, giây) đã dùng của tiến trình service."""
        with open(f"/proc/{self.svc_pid}/stat") as f:
            st = f.read().rsplit(")", 1)[1].split()
        return (int(st[11]) + int(st[12])) / CLK_TCK

    def wait_unchanged(self, paths, since, timeout):
        """T09: chờ mỗi đường dẫn có dòng Debug "không đổi" thấy sau `since`; trả thời điểm từng file."""
        paths, done, deadline = set(paths), {}, time.time() + timeout
        while time.time() < deadline and len(done) < len(paths):
            with self.tail.lock:
                for t, p in self.tail.unchanged:
                    if t >= since and p in paths and p not in done:
                        done[p] = t
            time.sleep(0.05)
        return done

    def gcdump(self, delay):
        """T11: chụp managed heap N giây sau khi thả large-all (dotnet-gcdump ép 1 lần GC — chỉ dùng ở lượt trace, không ở lượt đo số)."""
        time.sleep(delay)
        path = os.path.join(self.work, "large-all.gcdump")
        subprocess.run([DOTNET, "tool", "run", "dotnet-gcdump", "collect", "-p", str(self.svc_pid), "-o", path],
                       cwd=TOOLS_PERF, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=600)
        if os.path.isfile(path):
            self.results["gcdump"] = dict(path=path, rss_at_dump=self.sampler.samples[-1]["rss"] if self.sampler.samples else float("nan"))

    def cache_case(self, name, n, cpu0, t1, done_times, perf_paths=None):
        last = max(done_times.values()) if done_times else float("nan")
        cpu = self.svc_cpu_s() - cpu0
        r = dict(files=n, finished=len(done_times), wall_s=last - t1, cpu_ms_per_file=cpu * 1000 / max(1, len(done_times)))
        perf = self.perf_of(perf_paths) if perf_paths else []
        for k in ("read", "hash", "scan", "total"):
            vals = [m[k] for m in perf]
            r[f"{k}_mean"] = statistics.mean(vals) if vals else float("nan")
        self.cache[name] = r
        print(f"  {name}: {len(done_times)}/{n} xong · {r['cpu_ms_per_file']:.1f} ms CPU/file", flush=True)

    def phase_cache(self, take_n):
        """T09 (C1–C5) — cần --debug-log để thấy dòng "không đổi". Chi phí mỗi file = CPU-giây của service trong lúc xử lý ÷ số file
        (file "không đổi" không có dòng [Perf]). Các ca nối tiếp nhau trên CÙNG N file."""
        a = self.args
        n = a.cache_count
        self.set_phase("cache")
        # C1 — file mới
        staged = self.stage("cache", take_n(n))
        paths = [x[1] for x in staged]
        time.sleep(3)
        cpu0 = self.svc_cpu_s()
        t1, _ = self.drop_rename("cache", staged)
        done = self.wait_done(paths, 1800, since=t1)
        self.cache_case("C1 file mới", n, cpu0, t1, done, paths)
        time.sleep(5)
        # C4a — lưu lại y nguyên (ghi đúng byte cũ → IN_CLOSE_WRITE): bỏ cache tầng 1, đọc + băm, tầng 2 trùng hash → không quét
        cpu0, t1 = self.svc_cpu_s(), time.time()
        for p in paths:
            with open(p, "rb") as f:
                data = f.read()
            with open(p, "wb") as f:
                f.write(data)
        done = self.wait_unchanged(paths, t1, 1800)
        self.cache_case("C4a lưu lại y nguyên (qua watcher)", n, cpu0, t1, done)
        time.sleep(5)
        # C3 — đổi nội dung: ghi đè 1/5 số file bằng nội dung của file khác cùng định dạng
        k = max(2, n // 5)
        sub = paths[:k]
        cpu0, t1 = self.svc_cpu_s(), time.time()
        for i, p in enumerate(sub):
            donor = next(q for q in paths[k:] if q.rsplit(".", 1)[1] == p.rsplit(".", 1)[1] and q != p)
            shutil.copyfile(donor, p)
        done = self.wait_done(sub, 1800, since=t1)
        self.cache_case("C3 đổi nội dung", k, cpu0, t1, done, sub)
        time.sleep(5)
        # C5 — cùng nội dung, khác đường dẫn: chép sang thư mục khác (cache theo đường dẫn → quét lại toàn bộ)
        dups = [os.path.join(self.watch, "cache-dup", os.path.relpath(p, os.path.join(self.watch, "cache"))) for p in paths]
        staged = []
        for p, d in zip(paths, dups):
            st = os.path.join(self.staging, "cache-dup", os.path.relpath(p, os.path.join(self.watch, "cache")))
            os.makedirs(os.path.dirname(st), exist_ok=True)
            shutil.copyfile(p, st)
            staged.append((st, d, None))
        time.sleep(3)
        cpu0 = self.svc_cpu_s()
        t1, _ = self.drop_rename("cache-dup", staged)
        done = self.wait_done(dups, 1800, since=t1)
        self.cache_case("C5 cùng nội dung khác đường dẫn", n, cpu0, t1, done, dups)
        self.end_phase()
        # C2 / C4b — quét khởi động: (a) không đổi gì → cache tầng 1; (b) touch (chỉ đổi mtime) 1/5 số file → tầng 1 trượt, đọc + băm
        for label, touch, ph in (("C2 không đổi (quét khởi động)", 0, "cache-restart-C2"), ("C4b touch rồi quét khởi động", k, "cache-restart-C4b")):
            self.stop()
            for p in paths[:touch]:
                os.utime(p, None)
            self.set_phase(ph)  # [Thay đổi] tên riêng từng lần để bảng tài nguyên không gộp 2 lần khởi động
            t0, _, t_scan = self.start()
            line = self.tail.find_line("[FileWatcher] Quét (", t0)
            ms = re.search(r"\((\d+) ms\)\s*$", line[1]) if line else None
            cnt = re.search(r"(\d+) file — quét (\d+), bỏ qua (\d+) không đổi", line[1]) if line else None
            self.cache[label] = dict(files=int(cnt.group(1)) if cnt else -1, scanned=int(cnt.group(2)) if cnt else -1,
                                     unchanged=int(cnt.group(3)) if cnt else -1, scan_ms=int(ms.group(1)) if ms else -1, touched=touch)
            print(f"  {label}: {self.cache[label]}", flush=True)
            self.end_phase()

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


        # ======================= [Thay đổi] Benchmark T08–T10: các pha mới =======================
        large = [os.path.join(a.dataset, f) for f in LARGE_FILES if a.dataset and os.path.isfile(os.path.join(a.dataset, f))]

        if "large" in phases:
            # S3: mỗi file lớn 2 bản cùng lúc (đúng MaxConcurrency = 2 file nặng song song), cách nhau 10 s cho RSS hạ xuống
            for src in large:
                name = "large-" + os.path.splitext(os.path.basename(src))[0]
                staged = self.stage_files(name, [(src, f"{i}-{os.path.basename(src)}") for i in range(2)])
                time.sleep(10)
                self.set_phase(name)
                t1, _ = self.drop_rename(name, staged)
                paths = [s_[1] for s_ in staged]
                done = self.wait_done(paths, 900)
                r = self.latency_summary(paths, done)
                r.update(all_s=(max(done.values()) if done else float("nan")) - t1, tput=len(done) / max(1e-9, (max(done.values()) if done else t1 + 1) - t1),
                         drop_s=0.0)
                self.results[name] = r
                self.end_phase()
            if large:
                # large-all: MỌI file lớn cùng lúc → hàng chờ đầy file nặng, đo đỉnh RSS tệ nhất; T11: chụp heap giữa chừng nếu --gcdump-delay
                staged = self.stage_files("large-all", [(src, os.path.basename(src)) for src in large])
                time.sleep(10)
                self.set_phase("large-all")
                t1, _ = self.drop_rename("large-all", staged)
                if a.gcdump_delay:
                    threading.Thread(target=self.gcdump, args=(a.gcdump_delay,), daemon=True).start()
                paths = [s_[1] for s_ in staged]
                done = self.wait_done(paths, 1800)
                r = self.latency_summary(paths, done)
                last = max(done.values()) if done else float("nan")
                r.update(drop_s=0.0, all_s=last - t1, tput=len(done) / max(1e-9, last - t1))
                self.results["large-all"] = r
                self.end_phase()

        if "mixed" in phases and large:
            # Q4: 2 file nặng thả TRƯỚC, ngay sau đó 1.000 file nhỏ — so độ trễ file nhỏ với batch1000 (không có file nặng)
            heavy = self.stage_files("mixed-heavy", [(large[0], "heavy-0-" + os.path.basename(large[0])),
                                                     (large[min(1, len(large) - 1)], "heavy-1-" + os.path.basename(large[min(1, len(large) - 1)]))])
            small = self.stage("mixed-small", take(-(-a.mixed_small // len(by_class)))[: a.mixed_small])
            time.sleep(5)
            self.set_phase("mixed")
            t1, _ = self.drop_rename("mixed-heavy", heavy)
            self.drop_rename("mixed-small", small)
            hp, sp = [x[1] for x in heavy], [x[1] for x in small]
            done = self.wait_done(hp + sp, 3600)
            self.results["mixed-small"] = self.latency_summary(sp, {p: done[p] for p in sp if p in done})
            self.results["mixed-heavy"] = self.latency_summary(hp, {p: done[p] for p in hp if p in done})
            self.end_phase()

        if "overflow" in phases:
            # Q2/Q3: thả cùng lúc nhiều file hơn trần hàng chờ (Watch:MaxPendingFiles, đặt bằng --max-pending) → tràn → đánh dấu thư mục
            # quét bù; KHÔNG được mất file nào (integrity.lost). Lấy file theo thứ tự xoay vòng, không trùng với pha trước trong cùng lượt.
            items = take(-(-a.overflow_count // len(by_class)))[: a.overflow_count]
            staged = self.stage("overflow", items)
            time.sleep(5)
            self.set_phase("overflow")
            t1, t2 = self.drop_rename("overflow", staged)
            paths = [x[1] for x in staged]
            done = self.wait_done(paths, a.burst_timeout)
            r = self.latency_summary(paths, done)
            last = max(done.values()) if done else float("nan")
            r.update(drop_s=t2 - t1, all_s=last - t1, tput=len(done) / max(1e-9, last - t1))
            self.results["overflow"] = r
            self.end_phase()

        if "restartburst" in phases:
            # Q5: thả 1.000 file NGAY khi service vừa theo dõi xong, lúc quét khởi động (mọi file cũ, dùng chung trạm gác) còn chạy
            items = take(-(-a.mixed_small // len(by_class)))[: a.mixed_small]
            staged = self.stage("restartburst", items)
            self.stop()
            self.set_phase("restartburst")
            env_t0 = time.time()
            self.start_no_wait()
            self.wait_line("Đang theo dõi thư mục", env_t0, 60)
            t1, _ = self.drop_rename("restartburst", staged)
            paths = [x[1] for x in staged]
            done = self.wait_done(paths, 3600)
            r = self.latency_summary(paths, done)
            last = max(done.values()) if done else float("nan")
            r.update(drop_s=0.0, all_s=last - t1, tput=len(done) / max(1e-9, last - t1))
            self.results["restartburst"] = r
            self.end_phase()

        if "cache" in phases:
            self.phase_cache(lambda n: take(-(-n // len(by_class)))[:n])  # [Thay đổi] lấy đúng n file xoay vòng mọi lớp × định dạng

        if "sustained" in phases:
            # S4: tải đều trong thời gian dài; tên file thêm số vòng để không trùng; cứ N giây chen 1 file lớn (xoay vòng danh sách large)
            self.set_phase("sustained")
            n = int(a.sustained_rate * a.sustained_seconds)
            items = take(-(-n // len(by_class)))[:n]
            paths, start, next_large, li = [], time.time(), time.time() + a.sustained_large_every, 0
            for i, (d, nm, truth) in enumerate(items):
                target = start + i / float(a.sustained_rate)
                delay = target - time.time()
                if delay > 0:
                    time.sleep(delay)
                rel = os.path.join(d, f"{i}-{nm}")
                self.drop_copy("sustained", os.path.join(a.rawdata, d, nm), rel, truth)
                paths.append(os.path.join(self.watch, "sustained", rel))
                if large and a.sustained_large_every and time.time() >= next_large:
                    src = large[li % len(large)]
                    li += 1
                    rel = os.path.join("large", f"{i}-{os.path.basename(src)}")
                    self.drop_copy("sustained", src, rel, None)
                    paths.append(os.path.join(self.watch, "sustained", rel))
                    next_large += a.sustained_large_every
            done = self.wait_done(paths, 1800)
            r = self.latency_summary(paths, done)
            r["actual_rate"] = len(paths) / max(1e-9, time.time() - start)
            self.results["sustained"] = r
            self.end_phase()

        if "idle-long" in phases:
            # S0: rảnh dài — CPU nền (chu kỳ metrics / làm mới mẫu thư mục / GC), RSS có giảm dần không
            time.sleep(10)
            self.set_phase("idle-long")
            time.sleep(a.idle_long_seconds)
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
        # [Thay đổi] T09: file của pha cache bị CỐ Ý sửa nội dung (C3) để quét lại → không tính là "xử lý trùng"
        dup = [p for p in dropped if seen.get(p, 0) > 1 and self.drops[p][0] != "cache"]
        # độ chính xác: mức trong DB của service
        db = os.path.join(self.svc, "data", "dlp.db")
        pred = {}
        con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
        for path, labels in con.execute("SELECT f.path, r.labels_json FROM scan_result r JOIN file_state f ON f.file_id = r.file_id"):
            pred[path] = json.loads(labels)["level"] if labels else "Public"
        con.close()
        acc = {}
        for p, (phase, _, truth) in self.drops.items():
            if p in pred and truth is not None:  # [Thay đổi] file tổng hợp (DlpBenchData) không có nhãn → không tính độ chính xác
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
        base = os.path.join(a.out, f"e2e-linux-{stamp}-mc{a.max_concurrency}" + (f"-{a.label}" if a.label else ""))
        R = self.results
        md = [f"# Đo E2E DlpService Linux — lượt {stamp}", "",
              f"- Máy: {NCPU} CPU, RAM {os.sysconf('SC_PAGE_SIZE') * os.sysconf('SC_PHYS_PAGES') / 2**30:.1f} GB · "
              f"`Watch:MaxConcurrency = {a.max_concurrency}` · LLM tắt · bản build Release", "",
              # [Thay đổi] ghi rõ cấu hình của lượt đo (profile, các biến ghi đè appsettings)
              f"- Profile: **{'P-prod (systemd-run: CPUQuota=30 %, MemoryLimit=512M, nice 10, ionice idle)' if a.prod_profile else 'P-dev (tiến trình thường)'}**"
              f" · dotnet-counters: {'có' if a.counters else 'không'} · cấu hình ghi đè: `{json.dumps({k: v for k, v in self.service_env().items() if k != 'Watch__Folders__0'}, ensure_ascii=False)}`", ""]
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

        # [Thay đổi] T09: bảng cache
        if self.cache:
            md += ["", "## Cache (T09)", "", "Chi phí / file = CPU của service trong lúc xử lý ÷ số file xong (file \"không đổi\" không có dòng [Perf]).", "",
                   "| Trường hợp | File | Xong | Thời gian (s) | CPU ms / file | Đọc TB | Băm TB | Quét TB | Tổng TB | Quét khởi động |",
                   "|---|---|---|---|---|---|---|---|---|---|"]
            for name, r in self.cache.items():
                if "scan_ms" in r:
                    md.append(f"| {name} | {r['files']} | — | — | — | — | — | — | — | {r['scan_ms']} ms; quét {r['scanned']}, bỏ qua {r['unchanged']} (touch {r['touched']}) |")
                else:
                    md.append(f"| {name} | {r['files']} | {r['finished']} | {fmt(r['wall_s'], 1)} | {fmt(r['cpu_ms_per_file'])} | {fmt(r['read_mean'])} | "
                              f"{fmt(r['hash_mean'])} | {fmt(r['scan_mean'])} | {fmt(r['total_mean'])} | |")
            with open(base + "-cache.csv", "w", encoding="utf-8") as f:
                keys = sorted({k for r in self.cache.values() for k in r})
                f.write("case," + ",".join(keys) + "\n")
                for name, r in self.cache.items():
                    f.write(name + "," + ",".join(str(r.get(k, "")) for k in keys) + "\n")
        if "gcdump" in R:
            dst = base + "-large-all.gcdump"
            shutil.copyfile(R["gcdump"]["path"], dst)
            md += ["", f"Heap dump (T11): `{os.path.basename(dst)}` — RSS lúc chụp {fmt(R['gcdump']['rss_at_dump'], 0)} MB"]

        # [Thay đổi] T10: số liệu hàng chờ theo thời gian (dòng [Metric]) + T08: dotnet-counters
        with self.tail.lock:
            metrics = list(self.tail.metrics)
        if metrics:
            with open(base + "-queue.csv", "w", encoding="utf-8") as f:
                keys = ["span_s", "processed", "lat_p50", "lat_p95", "lat_max", "pending", "capacity", "oldest_ms", "overflow", "dirty"]
                f.write("t,phase," + ",".join(keys) + "\n")
                for t, m in metrics:
                    ph = next((w[0] for w in self.phase_windows if w[1] <= t and (w[2] is None or t <= w[2])), "")
                    f.write(f"{t:.2f},{ph}," + ",".join(str(m[k]) for k in keys) + "\n")
            peak = max(metrics, key=lambda x: x[1]["pending"])[1]
            md += ["", f"Hàng chờ (T10): {len(metrics)} dòng [Metric] → `{os.path.basename(base)}-queue.csv`; đỉnh đang chờ "
                       f"{peak['pending']}/{peak['capacity']}, tuổi lâu nhất {peak['oldest_ms']} ms, tổng tràn {sum(m['overflow'] for _, m in metrics)}"]
        for i, cf in enumerate(self.counter_files):
            if os.path.isfile(cf):
                shutil.copyfile(cf, f"{base}-counters-{i}.csv")
        if self.counter_files:
            md += ["", f"dotnet-counters (System.Runtime, 1 s): {len(self.counter_files)} file `-counters-N.csv` (mỗi lần khởi động service 1 file)"]

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
        self.stop_counters()
        if self.proc and self.proc.poll() is None:
            self.kill_service()
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
    # [Thay đổi] Benchmark T08–T11
    ap.add_argument("--work-dir", default="", help="thư mục chứa thư mục tạm của lượt đo (mặc định /tmp) — nên đặt trên đĩa đo, VD /data")
    ap.add_argument("--dataset", default="", help="bộ dữ liệu DlpBenchData (cho large / mixed / sustained)")
    ap.add_argument("--label", default="", help="hậu tố tên file kết quả")
    ap.add_argument("--no-build", action="store_true", help="không build lại (session đã build)")
    # [Thay đổi] Vòng 5: so Before/After — chạy service từ thư mục build KHÁC (Vvd bản commit cũ build sẵn), thay vì DlpService.Host.Linux/bin/Release/net10.0
    ap.add_argument("--svc-src", default="", help="thư mục output build của DlpService.Host.Linux để chạy (mặc định: bản build hiện tại của repo)")
    ap.add_argument("--metrics-seconds", type=int, default=0, help="Watch:MetricsIntervalSeconds (0 = giữ appsettings, 60 s)")
    ap.add_argument("--max-pending", type=int, default=0, help="Watch:MaxPendingFiles (0 = giữ appsettings, 10.000)")
    ap.add_argument("--debug-log", action="store_true", help="bật Debug cho DlpService.Worker (bắt buộc cho pha cache)")
    ap.add_argument("--prod-profile", action="store_true", help="chạy service trong systemd-run --scope (CPUQuota 30 %%, MemoryLimit 512M) — cần sudo đã xác thực")
    ap.add_argument("--counters", action="store_true", help="gắn dotnet-counters (CSV 1 s)")
    ap.add_argument("--idle-long-seconds", type=int, default=1800)
    ap.add_argument("--sustained-seconds", type=int, default=3600)
    ap.add_argument("--sustained-rate", type=float, default=20.0)
    ap.add_argument("--sustained-large-every", type=int, default=30, help="cứ N giây chen 1 file lớn (0 = không)")
    ap.add_argument("--overflow-count", type=int, default=15000)
    ap.add_argument("--mixed-small", type=int, default=1000, help="số file nhỏ của mixed / restartburst")
    ap.add_argument("--cache-count", type=int, default=500)
    ap.add_argument("--gcdump-delay", type=float, default=0, help="T11: chụp heap N giây sau khi thả large-all (0 = không)")
    args = ap.parse_args()
    run = Run(args)
    try:
        run.run()
        run.report()
    finally:
        run.cleanup()


if __name__ == "__main__":
    main()
