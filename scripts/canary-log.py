#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""[Thay đổi] File mới (GĐ2, R7): kiểm tra "canary" ở TẦNG LOG + DB của service chạy thật (khác tools/canary — công cụ đó kiểm gói gửi LLM).

Cách hoạt động:
  1. Tạo thư mục gốc giả (chế độ System với DLP_SYSTEM_ROOT) → log, DB, cấu hình đều nằm trong thư mục tạm, không đụng máy.
  2. Tạo file mẫu chứa các giá trị đánh dấu (CCCD, SĐT, email, secret, số thẻ, số tài khoản) — giống tools/canary.
  3. Chạy dlp-service ở mức log Information rồi Debug, chờ quét xong.
  4. Tìm trong file log + output console + file DB:
       - giá trị nguyên văn  → FAIL
       - bất kỳ đoạn liên tiếp >= 6 ký tự của giá trị → FAIL
       - dạng "lộ quanh dấu che": >= 2 ký tự đầu + "*", hoặc "*" + >= 3 ký tự cuối → FAIL
         (bắt đúng kiểu che cũ "001******001"; cách che mới chỉ còn "*" + 2 ký tự cuối)
     Tên file/thư mục tạm cố ý KHÔNG chứa chữ nào của giá trị đánh dấu (đường dẫn có trong log → báo nhầm).
Chạy:  python3 scripts/canary-log.py            (cần build trước: dotnet build DlpService.Host.Linux)
Mã thoát 0 = không lộ.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DLL = os.path.join(REPO, "DlpService.Host.Linux", "bin", "Debug", "net10.0", "dlp-service.dll")

# Giá trị đánh dấu — cùng bộ với tools/canary/DlpCanaryTest (hợp lệ theo validator thật) + số thẻ/số tài khoản
CANARIES = {
    "cccd": "001199000001",
    "phone": "0324681357",
    "email": "canary.leak.marker@vidu-canary.test",
    "secret": "CanaryKey9xQ7zR2vLmP4wJ8",
    "card": "4111111111111111",
    "bank": "0071000777123",
}

BODY = """Họ tên: Nguyễn Văn Canary
Số CCCD: {cccd}
Số điện thoại: {phone}
Email: {email}
api_key={secret}
Số thẻ: {card}
Số tài khoản: {bank} tại Vietcombank
""".format(**CANARIES)


WINDOW = 6


def leak_forms(value):
    """Các chuỗi mà nếu xuất hiện trong log/DB là đã lộ (một phần) giá trị."""
    out = set()
    for i in range(len(value) - WINDOW + 1):
        w = value[i:i + WINDOW]
        if len(set(w)) > 1:  # bỏ đoạn toàn ký tự lặp ("111111" của số thẻ test) — quá phổ biến, sẽ báo nhầm
            out.add(w)
    out.add(value[:2] + "*")   # cách che giữ ký tự đầu
    out.add("*" + value[-3:])  # cách che giữ >= 3 ký tự cuối
    return out


def run_once(dll, level, seconds):
    root = tempfile.mkdtemp(prefix="dlp-cl-")
    try:
        etc = os.path.join(root, "etc", "dlp-service")
        os.makedirs(etc)
        shutil.copytree(os.path.join(os.path.dirname(dll), "rules"), os.path.join(etc, "rules"))
        watch = os.path.join(root, "watch")
        os.makedirs(watch)
        for name in ("mau.txt", "mau.csv", "mau.eml"):
            with open(os.path.join(watch, name), "w", encoding="utf-8") as f:
                f.write(BODY)
        with open(os.path.join(etc, "appsettings.json"), "w", encoding="utf-8") as f:
            json.dump({"Watch": {"Folders": [watch]}, "Logging": {"LogLevel": {"Default": level}}}, f)

        env = dict(os.environ, DLP_LAYOUT="system", DLP_SYSTEM_ROOT=root)
        proc = subprocess.Popen(["dotnet", dll], env=env, cwd="/", stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        time.sleep(seconds)
        proc.send_signal(2)  # SIGINT → dừng gọn
        console, _ = proc.communicate(timeout=30)

        texts = {"console": console.decode("utf-8", "replace")}
        logdir = os.path.join(root, "var", "log", "dlp-service")
        texts["log"] = "".join(open(os.path.join(logdir, n), encoding="utf-8", errors="replace").read() for n in os.listdir(logdir))
        datadir = os.path.join(root, "var", "lib", "dlp-service")
        texts["db"] = b"".join(open(os.path.join(datadir, n), "rb").read() for n in os.listdir(datadir)).decode("latin-1")

        scanned = texts["log"].count("phát hiện")
        failures = []
        for name, value in CANARIES.items():
            for where, text in texts.items():
                if value in text:
                    failures.append(f"{level}: {name} NGUYÊN VĂN trong {where}")
                    continue
                leaked = sorted(w for w in leak_forms(value) if w in text)
                if leaked:
                    failures.append(f"{level}: {name} lộ một phần trong {where}: {leaked[:3]}")
        return scanned, failures
    finally:
        shutil.rmtree(root, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dll", default=DEFAULT_DLL)
    ap.add_argument("--seconds", type=int, default=15)
    args = ap.parse_args()
    if not os.path.exists(args.dll):
        sys.exit(f"Chưa build: không thấy {args.dll}")

    all_failures = []
    for level in ("Information", "Debug"):
        scanned, failures = run_once(args.dll, level, args.seconds)
        print(f"[{level}] file có phát hiện: {scanned}, lộ: {len(failures)}")
        if scanned == 0:
            failures.append(f"{level}: service không quét được file nào — bài test vô nghĩa")
        all_failures += failures
    for f in all_failures:
        print("  FAIL", f)
    print("== PASS — không canary nào lộ ==" if not all_failures else f"== {len(all_failures)} FAIL ==")
    sys.exit(1 if all_failures else 0)


if __name__ == "__main__":
    main()
