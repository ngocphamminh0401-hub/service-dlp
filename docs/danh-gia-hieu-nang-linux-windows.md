# Đánh giá hiệu năng DlpService Linux và so sánh với Windows (lớp Regex, không gọi LLM)

- **Ngày đo (Linux):** 2026-11-10 · nhánh `feature/linux` @ `1d22bd0` (+ 2 sửa nhỏ trong công cụ đo, mục 7).
- **Dữ liệu:** `~/dlp-rawdata`, 25.485 file có nhãn (thiếu 1 file so với 25.486 của Windows, xem `docs/danh-gia-doi-chieu-linux-windows.md`).
- **Độ chính xác trong mọi lượt đo:** vẫn 81,6 % (20.800/25.485), trùng Windows → các số dưới đây so tốc độ của **cùng một kết quả**.
- **Máy:**

| | Windows (mốc so) | Linux (lượt này) |
|---|---|---|
| Phần cứng | Laptop i7-13700H, **20 luồng logic**, RAM 13,7 GB, SSD | Máy ảo VMware, **2 vCPU** (i7-13700H), RAM **3,8 GB** |
| Hệ điều hành / .NET | Windows 11, .NET 10 | Ubuntu 18.04 (kernel 4.15), .NET 10.0.14 |
| Diệt virus thời gian thực | Windows Defender **bật** | Không có |
| Ngày đo | 21/10 – 29/10/2026 (từng mục ghi rõ) | 10/11/2026 |

> **Đọc đúng bảng so sánh:** hai máy khác nhau rất xa (2 vCPU so với 20 luồng; có/không có Defender), và số Windows được đo ở
> nhiều lượt, lúc code còn khác (chưa giới hạn số file xử lý cùng lúc ở các lượt E2E). Vì vậy, so **từng file / mỗi nhân CPU** có ý
> nghĩa; so **thông lượng tối đa** thì chỉ tham khảo. Chỗ nào không so được, báo cáo ghi rõ.

## Kết luận nhanh

| Câu hỏi | Kết quả |
|---|---|
| Xử lý 1 file nhanh hay chậm hơn Windows? | **Nhanh hơn.** 1 luồng: 6,3 ms/file so với 21,9 ms (Windows lúc máy tải) / 8,6 ms (Windows lúc máy rảnh). Trong service thật, tuần tự: **10,8 ms/file so với 79,6 ms** |
| Phần nào nhanh hơn? | Hầu hết ở **trích text** (đọc file + giải nén docx/pdf): 5,0 ms so với 20,4 ms. **Quét regex gần như bằng nhau** (1,3 so với 1,5 ms) — phần thuần CPU chạy cùng tốc độ trên cùng dòng CPU |
| Tốn CPU cho mỗi file? | **Ít hơn**: 3,1–3,2 ms CPU/file trong tiến trình (Windows 4,3 ms ở 6 luồng); trong service thật ở 50 file/giây khoảng 6 ms CPU/file (Windows khoảng 19 ms, chưa kể Defender) |
| Độ trễ đầu cuối lúc bình thường (5–50 file/giây)? | **p95 ≈ 330 ms** ở mọi mức tải (Windows 518–986 ms). Phần lớn là 300 ms debounce cố ý |
| Thông lượng tối đa? | Trong tiến trình: **~555 file/giây** (bão hoà ở 2 luồng = 2 vCPU), Windows 737–835 file/giây (20 luồng). Trên service thật: **142 file/giây** (`MaxConcurrency=2`) / **246 file/giây** (`=6`), Windows 75–104 file/giây |
| Ổn định? | 0 mất, 0 trùng, 0 tràn sự kiện, 0 lỗi trên 40.670 + 31.670 file; khởi động lại 3,6 giây với 40.670 file, **0 file bị quét lại** |
| RAM? | RSS 131 MB rảnh, tối đa 206–214 MB trong burst. Không so trực tiếp được với "private working set" của Windows (mục 5) |

**Đánh giá chung:** bản Linux **đạt và tốt hơn** bản Windows về tốc độ mỗi file, CPU mỗi file và độ trễ, dù chạy trên máy ảo yếu hơn
nhiều. Điểm yếu duy nhất là **thông lượng tối đa bị chặn bởi số nhân (2 vCPU)** và, trong burst lớn, bởi **đọc đĩa khi RAM không đủ giữ
file trong bộ đệm**. Đây là giới hạn của máy đo, không phải của code.

## 1. Pipeline 1 luồng (`DlpBaseline`, toàn bộ dữ liệu)

Cùng công cụ, cùng lệnh: `dotnet run -c Release --project tools/eval/DlpBaseline -- <rawdata>`. Đo trích text → regex → phân loại, không
có watcher / DB / log. Linux: file đã nằm sẵn trong bộ đệm (đọc trước 1 lượt).

| Mục | Windows 21/10 (`danh-gia-lop-regex.md`) | Linux 10/11 | Ghi chú |
|---|---|---|---|
| Thời gian cả vòng đo | 564,1 s | **162,9 s** | |
| Thông lượng | 45,6 file/s | **157,6 file/s** | Windows 28/09 (máy rảnh): 116 file/s |
| Tổng TB / p50 / p95 / max (ms) | 21,9 / 14,0 / 45,3 / 124,8 | **6,3 / 5,0 / 12,2 / 67,6** | Windows 28/09: 8,6 ms TB |
| Trích text TB — docx / pdf | 21,2 / 19,7 ms | **6,2 / 3,8 ms** | phần chênh chính |
| Quét regex TB — docx / pdf | 1,5 / 1,4 ms | 1,4 / 1,3 ms | **gần như bằng nhau** |
| Phân loại TB | 0,0 ms | 0,0 ms | |
| Tỷ trọng trích / quét | 93 % / 7 % | 79 % / 21 % | |
| Nạp 222 pattern / khởi tạo | 934 / 902 ms | **125 / 11 ms** | |
| RAM cao nhất | 166 MB (working set) | 182 MB (working set) | xấp xỉ |

**Nhận xét:** regex (thuần CPU) chạy cùng tốc độ trên hai hệ điều hành → code giống nhau, CPU giống nhau. Chênh lệch nằm ở **trích text**,
nơi có đọc đĩa, Defender quét file khi mở (Windows) và tải máy lúc đo. Chính `danh-gia-lop-regex.md` mục 7 đã ghi số Windows dao động
8,6 ↔ 21,9 ms tuỳ tải máy, nên so với mốc rảnh 8,6 ms thì Linux nhanh hơn khoảng **1,4 lần**, so với mốc 21,9 ms thì khoảng **3,5 lần**.

## 2. Song song trong tiến trình (`DlpConcurrencyBench`, 4.000 file)

Cùng công cụ, cùng 4.000 file (16 lớp × docx/pdf), mỗi file đi đủ tra cache → trích → băm → quét → phân loại + S1 + Router +
FileConfidence → ghi SQLite, đọc trước mọi file vào bộ đệm. Windows: `danh-gia-xu-ly-dong-thoi.md` phần A (22/10) và
`so-sanh-bo-doc-6-luong.md` (29/10, đã tắt hạ xung EcoQoS).

| Song song | Windows file/s (tăng tốc) | Linux file/s (tăng tốc) | Windows p95 (ms) | Linux p95 (ms) | Linux ghi DB TB (ms) | Linux CPU/file (ms) |
|---|---|---|---|---|---|---|
| 1 | 251 (×1,00) | **318** (×1,00) | 6,4 | **4,9** | 0,2 | 3,1 |
| 2 | 493 (×1,97) | **553** (×1,74) | 6,1 | 6,5 | 0,3 | 3,1 |
| 4 | **707** (×2,82) | 554 (×1,74) | 9,8 | 17,0 | 0,8 | 3,2 |
| 6 | **835** (29/10) | 556 (×1,75) | 19,2 (29/10) | 25,9 | 1,7 | 3,2 (Win 4,3) |
| 8 | **737** (×2,94) | 532 (×1,67) | 25,5 | 37,6 | 3,0 | 3,3 |
| 16 | 651 | 499 | 48 | 82,5 | 12,8 | 3,5 |
| 32 | 577 | 429 | 91 | 182,4 | 40,0 | 4,0 |
| 64 | 464 | 412 | 256 | 345,8 | 93,0 | 4,1 |

- Lỗi 0, lệch kết quả 0, DB đủ 4.000/4.000 bản ghi ở mọi mức trên cả hai → **an toàn đa luồng đạt trên Linux**.
- **1 nhân CPU Linux xử lý nhiều file hơn 1 nhân Windows** (318 so với 251 file/s; CPU/file 3,1 so với 4,3 ms).
- Linux **bão hoà ngay ở 2 luồng** (= 2 vCPU, CPU 87–89 % cả máy). Windows bão hoà ở 4–8 luồng vì có 20 luồng logic. Từ 4 luồng trở lên
  Windows nhanh hơn chỉ vì **có nhiều nhân hơn**. Cả hai cùng giảm khi quá nhiều luồng (tranh khoá ghi SQLite, GC).
- RAM Linux: 149 MB (1 luồng) → 165 MB (8) → 287 MB đỉnh (64). Windows: 116 → 134 → 167 MB (đo khác loại bộ nhớ, xem mục 5).

**Cache trong luồng** (8 luồng, cùng 1 DB, 3 lượt; Windows: `buoi-15-tom-tat.md`):

| Lượt | Windows file/s · CPU/file | Linux file/s · CPU/file |
|---|---|---|
| Quét đủ (DB trống) | 838 · 5,33 ms | 540 · 3,26 ms |
| Tầng 1 (size/mtime không đổi, không mở file) | 32.435 · 0,04 ms | **63.688 · 0,02 ms** |
| Tầng 2 (mtime đổi, text không đổi → bỏ quét) | 1.032 · 3,66 ms | 789 · 2,08 ms |

Cùng kết luận trên cả hai: tầng 1 gần như miễn phí; tầng 2 chỉ tiết kiệm phần quét (khoảng 30–35 % CPU).

## 3. Service thật, đầu cuối (E2E)

**Cách đo Linux:** `tools/perf/do-e2e-linux.py` (mới — bản Linux của `Measure-DlpServiceE2E.ps1`). Service bản build Release chạy như
tiến trình riêng, LLM tắt, thư mục theo dõi + DB + log sạch. Thả file vào thư mục theo dõi; "xong" = lúc dòng `[Perf]` của file xuất
hiện trong log. Hai lượt: `20261110-163618` (`MaxConcurrency = 2`, mặc định Linux, đủ kịch bản, 40.670 file) và `20261110-165145`
(`MaxConcurrency = 6`, mức của Windows, 31.670 file). **Windows:** `danh-gia-service-e2e.md` (21/10), `danh-gia-tai-nguyen.md` (22/10),
`danh-gia-xu-ly-dong-thoi.md` phần B (22/10). Lúc đó service Windows **chưa giới hạn số file cùng lúc** (19–33 file song song).

### 3.1. Tuần tự (thả 1 file, chờ xong rồi thả file tiếp — 64 file)

| Bước (TB / p50 / p95 ms) | Windows 21/10 | Linux (mc2) |
|---|---|---|
| Tra cache (DB) | 0,3 / 0,3 / 0,5 | 0,2 / 0,2 / 0,4 |
| Trích text | 67,0 / 63,6 / 94,5 | **5,9 / 5,3 / 12,5** |
| Băm SHA-256 | 0,1 / 0,1 / 0,1 | 0,1 / 0,0 / 0,1 |
| Quét regex | 7,9 / 5,2 / 16,5 | **2,9 / 2,0 / 9,9** |
| Phân loại + S1 (+ FileConfidence trên Windows) | 1,8 / 0,8 / 3,2 | 0,9 / 0,8 / 1,8 |
| Ghi DB (SQLite) | 2,4 / 2,3 / 3,4 | 0,8 / 0,5 / 2,0 |
| **Tổng trong service** | **79,6 / 75,8 / 122,1** | **10,8 / 11,1 / 21,2** |
| **Đầu cuối** (gồm 300 ms debounce) p50 / p95 / max | 422 / 470 / 526 ms | **322 / 338 / 341 ms** |

Trong service thật, khoảng cách lớn hơn so với `DlpBaseline` (trích text 67 so với 6 ms). Trên Windows, service chạy dưới SCM và Defender
quét file mới được copy vào, cộng với tải máy lúc đo. Trên Linux, phần ngoài debounce chỉ còn khoảng 22 ms.

### 3.2. Tải đều 5 / 20 / 50 file/giây (mỗi mức 120 giây, file khác nhau)

| Mức tải | Windows 22/10: đầu cuối p50 / p95 / max · trong service TB | Linux (mc2): đầu cuối p50 / p95 / max · trong service TB | Xong (Linux) |
|---|---|---|---|
| 5 file/s | 469 / 536 / 602 ms · 77 ms | **318 / 330 / 344 ms · 9,0 ms** | 600/600 |
| 20 file/s | 444 / 518 / 569 ms · 70 ms | **320 / 331 / 351 ms · 8,9 ms** | 2.400/2.400 |
| 50 file/s | 460 / **986** / 1.778 ms · 89 ms | **319 / 331 / 380 ms · 7,6 ms** | 6.000/6.000 |

Ở 50 file/giây, Windows bắt đầu xếp hàng (p95 gần 1 giây), còn Linux vẫn phẳng (p95 = debounce + khoảng 30 ms). Watcher Linux tự báo
(`[Metric]`) độ trễ sự kiện → bắt đầu xử lý p95 chỉ 2–6 ms ở các mức tải này.

**CPU theo mức tải** (Windows: % của 20 luồng; Linux: % của 2 vCPU; quy về số nhân để so):

| Mức tải | Windows service: % máy → nhân · CPU/file | Windows Defender | Linux service: % máy → nhân · CPU/file |
|---|---|---|---|
| 5 file/s | 0,6 % → 0,12 nhân · ~24 ms | 1,1 % (0,22 nhân) | 2,8 % → **0,06 nhân · ~12 ms** |
| 20 file/s | 2,0 % → 0,40 nhân · ~20 ms | 3,8 % (0,76 nhân) | 8,4 % → **0,17 nhân · ~8,5 ms** |
| 50 file/s | 4,7 % → 0,94 nhân · ~19 ms | 8,2 % (1,64 nhân) | 15,1 % → **0,30 nhân · ~6 ms** |

Linux tốn khoảng **1/3 CPU mỗi file** so với Windows, và **không có thêm chi phí diệt virus** (trên Windows, Defender tốn CPU nhiều hơn chính
service). Lưu ý: trên máy 2 vCPU, 0,30 nhân là 15 % cả máy. Trên máy người dùng nhiều nhân, tỷ lệ % sẽ nhỏ hơn tương ứng.

### 3.3. Thả cùng lúc N file (rename từ thư mục tạm cùng ổ)

| Lô | Windows 22/10 (không giới hạn): cả lô · file/s · p95 đầu cuối | Linux mc2: cả lô · file/s · p95 | Linux mc6: cả lô · file/s · p95 |
|---|---|---|---|
| 1 | 0,4 s · — · 413 ms | 0,3 s · — · 310 ms | 0,3 s · — · 327 ms |
| 10 | 0,5 s · 19 · 529 ms | 0,3 s · 29 · 346 ms | 0,3 s · 29 · 344 ms |
| 100 | 1,7 s · 59 · 1,3 s | 0,6 s · 174 · 575 ms | 0,5 s · 192 · 521 ms |
| 1.000 | 14,8 s · 68 · 11,8 s | **2,3 s · 437** · 2,2 s | 2,5 s · 404 · 2,3 s |
| 5.000 | 69,9 s · 72 · 42,7 s | 16,1 s · 311 · 15,2 s | **10,7 s · 465** · 10,1 s |

Không mất, không trùng, không tràn sự kiện ở mọi cỡ lô, trên cả hai. Thời gian xử lý trong service khi có lô lớn: Windows tăng từ 56 lên
khoảng 240 ms/file. Linux giữ ở **4–6 ms/file**, vì giới hạn đồng thời (2 hoặc 6) giữ cho không có hàng chục file tranh khoá cùng lúc.

### 3.4. Burst toàn bộ dữ liệu (FastDrop: rename 25.485 file trong 0,3 giây)

| Mục | Windows 21/10 FastDrop (không giới hạn) | Linux mc2 | Linux mc6 |
|---|---|---|---|
| Tới khi file cuối có kết quả | 244 s | 179,0 s | **103,7 s** |
| **Thông lượng** | 104 file/s (lượt 22/10: ~71–75 file/s) | 142 file/s | **246 file/s** |
| Trong service TB / p95 | 319 / 603 ms | **13,9 / 30,0 ms** | 19,3 / 43,1 ms |
| Trích text TB | 223 ms | 11,2 ms | 16,0 ms |
| Ghi DB TB / p95 | 59,8 / 239 ms | **0,7 / 2,3 ms** | 0,9 / 3,1 ms |
| Song song thực tế | ~33 file | 2 | 6 |
| CPU service | 11,4 % của 20 luồng ≈ 2,3 nhân | 36 % ≈ **0,73 nhân** | 53 % ≈ 1,05 nhân |
| Đọc đĩa của service | — | 10,1 MB/s | 17,9 MB/s |
| RAM | 190 MB max (private) → 113 MB sau tải | RSS 166 → **206** → 170 MB | RSS 163 → 214 → 190 MB |
| Độ dốc RAM nửa sau burst | −7,6 MB/phút | −22,2 MB/phút | −26,8 MB/phút |
| Mất / trùng / tràn / lỗi | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 | 0 / 0 / 0 / 0 |
| Độ chính xác | 81,6 % | 81,6 % (20.800/25.485) | 81,6 % |

**Vì sao burst Linux chậm hơn lô 1.000–5.000 file (142 so với 437 file/s)?** Trong burst, CPU service chỉ 0,73 nhân (chưa kín 2 vCPU),
bước đọc tăng từ 2 lên 11 ms và service đọc đĩa thật 10 MB/s. Nghĩa là **file không còn trong bộ đệm RAM**: máy ảo chỉ có 3,8 GB, vừa chép
1,7 GB ra thư mục tạm cộng 2,7 GB file của các giai đoạn trước, nên phải đọc lại từ đĩa ảo. Với 6 file cùng lúc, chờ đĩa được chồng lên
nhau, nên thông lượng tăng 1,7 lần (246 file/s) dù CPU vẫn chưa kín. Trên máy thật nhiều RAM / SSD, khoảng cách này sẽ nhỏ hơn. **Chưa
kiểm chứng.**

### 3.5. Khởi động, dừng, khởi động lại

| Mục | Windows | Linux (mc2) |
|---|---|---|
| Khởi động lần đầu → đang theo dõi (thư mục trống) | 0,8 s tới Running | 0,32 s |
| Dừng (Windows: SCM; Linux: SIGTERM) | — | **0,42 s** |
| Khởi động lại → đang theo dõi, khi thư mục có ~41.000 file | **19 s** (41.468 file, 22/10); 2,3 s (25.486 file, 21/10) | **3,6 s** (40.670 file; bước quét lượt đầu 2,0 s) |
| File bị quét lại sau khởi động lại | 0 | **0** (40.670 bỏ qua nhờ size/mtime) |

## 4. Độ ổn định trong các lượt đo Linux

| Kiểm tra | mc2 (40.670 file) | mc6 (31.670 file) |
|---|---|---|
| Mất / xử lý trùng | **0 / 0** | **0 / 0** |
| Tràn sự kiện inotify | 0 | 0 |
| Dòng Error / Critical | 0 | 0 |
| Dòng Warning | 6.298 | 4.840 = **2 dòng × 2.420 file vùng xám** ("cần LLM thẩm định" + "không gửi LLM vì LLM tắt") → cảnh báo nghiệp vụ dự kiến, không phải lỗi |
| inotify watch dùng | tối đa 153 / 8.192 | — |
| fd / thread | 198 → tối đa 201 → 198 · thread ≤ 21 | fd ≤ 205 · thread ≤ 24 |

## 5. RAM — vì sao không so trực tiếp

- Windows đo **private working set** (bộ nhớ riêng của tiến trình). Linux đo **RSS**, gồm cả phần thư viện dùng chung và file .dll đã map
  vào bộ nhớ, nên luôn cao hơn. Ví dụ lúc rảnh: Windows 52–65 MB, Linux RSS 131 MB.
- Trên cùng một loại bộ nhớ (working set trong `DlpBaseline`), hai bên xấp xỉ nhau: **166 MB so với 182 MB**.
- Sau burst, Linux RSS giảm trong burst (độ dốc âm) nhưng **2 phút sau tải vẫn giữ 170–190 MB**. Windows trả về sau 2 phút (116 → 76 MB) và
  sau 30 phút (148 → 93 MB). Có thể do GC .NET trên Linux ít trả bộ nhớ về hệ điều hành khi rảnh ngắn. **Không có dấu hiệu rò** (độ dốc
  âm, fd/thread về mức nền), nhưng **chưa đo rảnh 30 phút** trên Linux.

## 6. Giới hạn của phép đo

- **Máy khác nhau** (mục đầu). Linux là máy ảo 2 vCPU / 3,8 GB, nên thông lượng tối đa và burst bị giới hạn bởi máy. Chưa đo trên máy
  Linux thật nhiều nhân (GĐ6).
- **Số Windows không cùng lượt, không cùng code:** E2E Windows (21–22/10) đo lúc chưa có `Watch:MaxConcurrency`. Bench 6 luồng đo 29/10.
  Chưa có lượt E2E Windows nào với giới hạn 6, nên các bảng 3.3–3.4 so **service Windows không giới hạn** với **service Linux có giới hạn**.
- **Windows có Defender, Linux không.** Đây là phần lớn chênh lệch ở bước trích text. Không tách riêng được nếu không đo lại Windows với
  Defender loại trừ thư mục.
- Windows chạy dưới SCM (bản publish). Linux chạy tiến trình thường (bản build Release, cùng code, không qua systemd). Gói `.deb` dưới
  systemd có hardening chưa được đo hiệu năng.
- Công cụ đo khác nhau: PowerShell (Windows, tốn 2–3 % CPU khi đọc log) và Python (Linux). Lúc "xong" ở Linux = lúc script thấy dòng
  `[Perf]` (đọc log mỗi 20 ms), nên có thể muộn hơn thực tế tới khoảng 20 ms cộng độ trễ hàng đợi ghi log.
- Mỗi kịch bản đo 1 lần. Không đo LLM, Tika native (GĐ5), file lớn (PDF ~500 trang — trên Linux không có bộ file `--bigpdf`).

## 7. Thay đổi trong lượt đo này (chỉ công cụ đo, không đụng code service)

| File | Thay đổi | Lý do |
|---|---|---|
| `tools/shared/PowerThrottling.cs` | `Disable()` bỏ qua khi không phải Windows | Gọi `kernel32.dll` trên Linux → `DllNotFoundException`, `DlpBaseline` và `DlpConcurrencyBench` dừng ngay |
| `tools/eval/DlpBaseline/SampleLoader.cs` | Đổi `\` thành `/` trước `Path.GetFileName` | Đường dẫn trong `_metadata.jsonl` sinh trên Windows. Trên Linux không tìm thấy file nào ("Không có file nào để đo"). Trên Windows kết quả không đổi |
| `tools/perf/do-e2e-linux.py` | **Mới**: đo E2E service Linux | Bản Linux của `Measure-DlpServiceE2E.ps1` (không chạy được trên Linux) |

## 8. Đề xuất (cần quyết định)

| # | Việc | Đề xuất |
|---|---|---|
| 1 | `Watch:MaxConcurrency` mặc định trên Linux (D6 = 2) | Lúc bình thường (≤ 50 file/giây) 2 và 6 cho cùng độ trễ. Chỉ khác ở đợt dồn lớn, khi đọc đĩa chậm: 6 nhanh hơn 1,7 lần (246 so với 142 file/s) nhưng dùng khoảng 1 nhân thay vì 0,7. **Giữ 2** cho máy người dùng (ít giật máy), hoặc **4** nếu ưu tiên xử lý xong đợt dồn nhanh. Nên đo lại trên máy thật trước khi đổi |
| 2 | RAM sau tải chưa trả về trên Linux | Đo rảnh 30 phút; nếu vẫn giữ cao, cân nhắc `DOTNET_GCConserveMemory` hoặc `System.GC.RetainVM=false` trong unit systemd |
| 3 | Mốc so Windows còn cũ | Khi chạy test Windows trước khi gộp `main`: chạy lại `Measure-DlpServiceE2E.ps1 -FastDrop -BatchSizes 1,10,100,1000,5000` với `MaxConcurrency=6`, để có cặp so cùng code |

## 9. Cách chạy lại

```bash
export PATH=$HOME/.dotnet:$PATH
# 1 luồng, toàn bộ dữ liệu (kết quả: tools/eval/results/baseline-*.md / .csv)
dotnet run -c Release --project tools/eval/DlpBaseline -- ~/dlp-rawdata
# song song trong tiến trình (cần CSV của bước trên làm manifest)
dotnet run -c Release --project tools/perf/DlpConcurrencyBench -- --manifest tools/eval/results/baseline-<lượt>-legacy.csv \
    --rawdata ~/dlp-rawdata --levels 1,2,4,6,8,16,32,64
# service thật E2E (~15 phút, cần ~3 GB đĩa trống)
python3 tools/perf/do-e2e-linux.py                          # MaxConcurrency 2, đủ kịch bản
python3 tools/perf/do-e2e-linux.py --max-concurrency 6 --phases warmup,seq,batches,burst,after
```

**Kết quả thô** (máy Linux, thư mục bị `.gitignore`):
- `tools/eval/results/baseline-20261110-162920-legacy.*`
- `tools/perf/results/concurrency-20261110-163412-legacy.*`
- `tools/perf/results/e2e-linux-20261110-163618-mc2-*`, `e2e-linux-20261110-165145-mc6-*` (`-report.md`, `-samples.csv` mỗi giây, `-files.csv` độ trễ từng file)
