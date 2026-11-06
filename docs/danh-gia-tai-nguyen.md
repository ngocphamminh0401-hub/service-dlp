# Đánh giá ảnh hưởng của tài nguyên hệ thống (CPU, RAM, I/O) khi DlpService hoạt động

- **Ngày đo:** 2026-10-22, lượt `20261022-101205` — service bản publish 10:12 (đã gồm sửa lỗi watcher bị GC thu hồi, ghi log qua hàng
  đợi, sửa ghi DB), chạy dưới SCM, `LlmSvc` **dừng** trong lúc đo. Kết quả thô: `tools/perf/results/e2e-20261022-101205-*` (báo cáo
  `-report.md`).
- **Cách đo:** `tools/perf/Measure-DlpServiceE2E.ps1` lấy mẫu mỗi ~1 giây: CPU / RAM (private working set) / handle / thread / I/O đọc-ghi
  của tiến trình DlpService; CPU của Windows Defender (`MsMpEng`) và của PowerShell (chính công cụ đo); CPU cả máy; % đĩa bận; RAM trống
  của máy. CPU tính theo **% của cả máy** (100 % = 20 luồng logic; 5 % ≈ 1 nhân).
- **Kịch bản:** nền (rảnh 15 giây) → 64 file tuần tự → tải đều **5 / 20 / 50 file/giây** (mỗi mức 120 giây, file khác nhau) → burst 25.486
  file → **rảnh 30 phút** → khởi động lại.
- **Máy:** i7-13700H (20 luồng), RAM 13,7 GB, SSD; chế độ nguồn Balanced.

## 1. Tài nguyên theo mức tải

| Giai đoạn | CPU service TB / max | CPU Defender TB / max | CPU script đo TB | CPU cả máy TB | RAM service (MB) đầu → max → cuối | I/O đọc / ghi (MB/s) | Đĩa bận | Thread max |
|---|---|---|---|---|---|---|---|---|
| Nền (rảnh) | 0,4 % / 5,0 % | 0,1 % / 0,3 % | 0,0 % | 20,6 % | 65 → 65 → 52 | 0 / 0 | 1 % | 35 |
| Tuần tự (1 file/lần) | 2,2 % / 5,3 % | 0,6 % | 0,5 % | 23,6 % | 55 → 64 → 64 | 0,35 / 0,15 | 2 % | 34 |
| **5 file/giây** | 0,6 % / 4,7 % | 1,1 % / 3,1 % | 0,4 % | 23,3 % | 65 → 66 → 60 | 0,81 / 0,32 | 3 % | 25 |
| **20 file/giây** | 2,0 % / 2,5 % | 3,8 % / 5,4 % | 0,6 % | 30,7 % | 60 → 66 → 63 | 3,21 / 1,15 | 4 % | 27 |
| **50 file/giây** | 4,7 % / 6,2 % | 8,2 % / 12,7 % | 2,1 % | 40,2 % | 63 → 85 → 76 | 7,65 / 2,78 | 8 % | 43 |
| Burst (~71 file/giây) | 6,7 % / 13,6 % | 8,6 % / 17,3 % | 2,9 % | 41,8 % | 76 → **191** → 148 | 10,4 / 3,8 | 8 % | 72 |
| Rảnh 30 phút sau tải | 0,0 % | 0,9 % | 0,1 % | 20,2 % | 148 → 148 → **93** | 0 / 0 | 4 % | 63 |

**Độ trễ đầu cuối** (thả file → service ghi xong kết quả, gồm 300 ms debounce cố ý):

| Mức tải | Xong | p50 | p95 | Max | Xử lý trong service TB |
|---|---|---|---|---|---|
| 5 file/giây | 600/600 | 469 ms | 536 ms | 602 ms | 77 ms |
| 20 file/giây | 2.400/2.400 | 444 ms | 518 ms | 569 ms | 70 ms |
| 50 file/giây | 6.000/6.000 | 460 ms | **986 ms** | 1.778 ms | 89 ms |

## 2. Nhận xét

1. **Service rất nhẹ về CPU:** ≈ 0,1 nhân ở 5 file/giây, 0,4 nhân ở 20, ~1 nhân ở 50, tối đa ~2,7 nhân (13,6 %) khi burst. Ở mọi mức tải
   CPU service tăng gần tuyến tính theo số file/giây (~0,1 % máy cho mỗi file/giây).
2. **Windows Defender tốn CPU NHIỀU HƠN chính service ở mọi mức tải:** 20 file/giây — Defender 3,8 % so với service 2,0 %; 50 file/giây —
   8,2 % so với 4,7 %; burst — 8,6 % so với 6,7 % (đỉnh 17,3 %). Defender quét mỗi file docx/pdf mới xuất hiện / được service mở. Đây là chi
   phí của **môi trường**, không của service, nhưng là phần lớn nhất trong CPU tăng thêm khi service làm việc. (Xác nhận nghi vấn ở
   `docs/danh-gia-service-e2e.md` mục 5b.) Công cụ đo (PowerShell đọc log) tốn thêm 2–3 %.
3. **RAM nhỏ và trả lại sau tải:** 52–66 MB khi rảnh/tải nhẹ, 85 MB ở 50 file/giây, đỉnh **191 MB** khi burst; sau 30 phút rảnh về **93 MB**
   (cao hơn nền ~36 MB — cache JIT, regex đã biên dịch, kết nối DB). Rảnh 25 phút: RAM **giảm** 47 MB/giờ, handle +32/giờ, thread không tăng →
   **không có dấu hiệu rò rỉ**. Handle/thread trở về gần mức nền.
4. **Đĩa không phải nút thắt:** I/O đọc tối đa ~10 MB/s, đĩa bận ≤ 8 %.
5. **Độ trễ ổn định tới 20 file/giây** (p95 ~520 ms ≈ 300 ms debounce + ~80 ms xử lý + copy); ở **50 file/giây p95 tăng gấp đôi** (986 ms) dù
   service vẫn theo kịp (6.000/6.000) — bắt đầu có hàng chờ khi file đến dồn. Thông lượng tối đa đo ở lượt trước (`161412`): ~104 file/giây.
6. **Ổn định:** 0 file mất / trùng / timeout / đọc lỗi / tràn buffer, không crash; khởi động lại 0,8 giây tới Running, 2,9 giây tới khi theo dõi
   thư mục, 0 file bị quét lại. Lỗi watcher của lượt `085800` **không tái hiện** sau khi publish bản sửa (lượt trước dừng nhận file ngay sau
   10 file làm nóng; lượt này xử lý đủ mọi giai đoạn). Chưa kiểm thả file NGAY SAU 30 phút rảnh (`-PostIdleProbe` chưa bật ở lượt này).
7. **Bối cảnh RAM của máy:** RAM trống của máy 2,0–2,7 GB trong lúc đo (LlmSvc đã dừng); khi `LlmSvc` chạy có lúc chỉ còn ~0,5 GB — mô hình LLM
   (phi-4, giới hạn 10–12 GB) mới là thành phần chiếm tài nguyên lớn nhất của hệ thống, không phải lớp regex.

## 3. Giới hạn của phép đo

- 1 lượt, 1 máy; CPU nền của máy ~20 % (trình duyệt, VS Code, Zalo...) dao động theo các ứng dụng khác.
- Ở bước burst, script thả file chậm (354 giây cho 25.486 file ≈ 72 file/giây — vòng lặp PowerShell vừa chuyển file vừa đọc log), nên
  burst ở đây gần với "tải đều ~71 file/giây" hơn là quá tải; số quá tải xem lượt `161412` (`docs/danh-gia-service-e2e.md` mục 5b).
- Chưa tách ảnh hưởng của Defender bằng lượt có loại trừ thư mục (theo quyết định: giữ nguyên bảo vệ).
- Không gồm tài nguyên của `LlmSvc` (lớp LLM).
