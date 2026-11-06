# Đánh giá DlpService chạy thật (bản publish dưới SCM) — lớp Regex, không gọi LLM

- **Ngày đo:** 2026-10-21, lượt `20261021-150346` (sau khi sửa FileLogger). Lượt `20261021-144046` (trước khi sửa) dùng để so sánh.
- **Cách đo:** `tools/perf/Measure-DlpServiceE2E.ps1` đóng vai người dùng — copy file vào `watch-folder` của service đang chạy dưới
  Service Control Manager (tài khoản LocalSystem), đọc kết quả từ log (`[DLP]` mức, `[Perf]` thời gian từng bước, `[Conf]`), lấy mẫu
  CPU/RAM/handle/thread của tiến trình mỗi ~1 giây. `LlmSvc` **đã dừng** trong lúc đo (chỉ đo lớp regex). Phân tích:
  `tools/perf/Analyze-DlpServiceE2E.py`. Kết quả thô: `tools/perf/results/e2e-20261021-150346-*`.
- **Dữ liệu:** 25.486 file có nhãn của rawdata (đúng danh sách của `docs/danh-gia-lop-regex.md`) + 64 file đo tuần tự (2 file × 16 lớp ×
  2 định dạng) + 10 file làm nóng (không tính).
- **Kịch bản:** làm nóng → mức nền 15 giây → tuần tự 64 file (thả 1 file, chờ xong mới thả file tiếp) → burst 25.486 file (thả liên
  tục) → 2 phút sau tải → khởi động lại service.
- **Máy:** Intel i7-13700H (20 luồng), RAM 13,7 GB, Windows 11, .NET 10; chế độ nguồn Balanced, cắm sạc.

## 1. Độ chính xác phân loại

Service thật cho **cùng mức với DlpBaseline trên 100 % file** (25.486/25.486) → mọi chỉ số độ chính xác trùng khớp với
`docs/danh-gia-lop-regex.md` (bảng theo 16 lớp xem ở đó).

| Chỉ số | Giá trị |
|---|---|
| File có kết luận | 25.486 / 25.486 |
| Accuracy (4 mức) | **81,6 %** |
| Macro Precision / Recall / F1 | 81,9 % / 81,6 % / **81,7 %** |
| Weighted Precision / Recall / F1 | 82,0 % / 81,6 % / 81,7 % |
| "Nhạy cảm" (Confidential + Restricted) P / R / F1 | 91,7 % / 95,8 % / **93,7 %** |

| Mức | Precision | Recall | F1 |
|---|---|---|---|
| Public | 87,0 % | 78,8 % | 82,7 % |
| Internal | 78,2 % | 78,5 % | 78,4 % |
| Confidential | 75,6 % | 84,3 % | 79,7 % |
| Restricted | 87,0 % | 84,8 % | 85,9 % |

## 2. Thời gian xử lý

### 2.1. Từng file — bên trong service (dòng `[Perf]`, không gồm debounce/copy)

| Bước | Tuần tự: TB / p50 / p95 (ms) | Burst (tải cao): TB / p50 / p95 (ms) |
|---|---|---|
| Tra cache (DB) | 0,3 / 0,3 / 0,5 | 14,2 / 0,4 / 80,6 |
| Trích text | 67,0 / 63,6 / 94,5 | 215,4 / 189,6 / 423,4 |
| Băm SHA-256 | 0,1 / 0,1 / 0,1 | 0,2 / 0,0 / 0,1 |
| Quét regex | 7,9 / 5,2 / 16,5 | 19,9 / 11,3 / 61,3 |
| Phân loại + S1 + FileConfidence | 1,8 / 0,8 / 3,2 | 4,1 / 0,5 / 27,9 |
| Ghi DB (SQLite) | 2,4 / 2,3 / 3,4 | 53,9 / 22,4 / 204,2 |
| **Tổng** | **79,6 / 75,8 / 122,1** (max 193) | **307,7 / 259,1 / 627,9** (max 2.429) |

### 2.2. Từng file — đầu cuối (copy vào watch-folder → service ghi xong kết quả), tuần tự

| | TB | p50 | p95 | Max |
|---|---|---|---|---|
| Tất cả (64 file) | **425 ms** | 422 ms | 470 ms | 526 ms |
| docx / pdf | 415 / 435 ms | | | |

Gồm **300 ms debounce** cố ý của watcher (chờ file ghi xong) + ~80 ms xử lý + copy.

### 2.3. Toàn bộ pipeline (burst 25.486 file)

| Mục | Giá trị |
|---|---|
| Thời gian copy 25.486 file (1,7 GB) vào watch-folder | 415,7 giây |
| **Thời gian tới khi file cuối có kết quả** | **419,5 giây (7,0 phút)** |
| Thông lượng quan sát | 60,8 file/giây · 4,0 MB/giây |
| Mức song song trung bình trong service | ≈ 18,7 file cùng lúc |

**Đọc đúng con số thông lượng:** service xử lý xong file cuối chỉ **4 giây sau khi copy xong** → trong lượt này service luôn theo kịp,
thông lượng bị giới hạn bởi **tốc độ thả file của script đo**, không phải năng lực tối đa của service. Năng lực tối đa cần đo bằng cách
thả file nhanh hơn (xem mục 6).

### 2.4. So với trước khi sửa FileLogger

| | Trước (`144046`) | Sau (`150346`) |
|---|---|---|
| Bước phân loại (burst, TB) | **240,5 ms** | **4,1 ms** |
| Tổng/file (burst, TB) | 309,8 ms | 307,7 ms |
| Thời gian xử lý trong bước "phân loại" chủ yếu là | chờ khoá ghi log | tính toán thật |
| Ai giới hạn thông lượng | **service (FileLogger ~1.070 dòng/giây)** | **script copy file** |

Sau khi sửa, thời gian chờ không còn dồn ở bước ghi log mà chuyển sang **trích text** (58 → 215 ms) và **ghi DB** (2 → 54 ms): nhiều file
được xử lý song song thật sự hơn nên tranh nhau đĩa (đồng thời với việc copy 1,7 GB vào cùng ổ) và khoá ghi SQLite (`ResultStore`
ghi tuần tự). Tổng/file không đổi vì file đến với tốc độ copy.

## 3. Tài nguyên

| Giai đoạn | CPU TB (% cả máy) | CPU max | RAM (MB) đầu → max → cuối | Handle đầu → max → cuối | Thread max |
|---|---|---|---|---|---|
| Nền (rảnh) | 0,4 % | 4,5 % | 59 → 59 → 47 | 685 → 685 → 681 | 33 |
| Tuần tự | 2,2 % | 5,2 % | 49 → 62 → 62 | 681 → 681 → 569 | 28 |
| Burst | 6,1 % | 12,6 % | 62 → **138** → 116 | 566 → 810 → 765 | 61 |
| Sau tải (2 phút) | 0,0 % | 0,1 % | 116 → 116 → **76** | 765 → 765 → **563** | 56 |

- RAM (private working set) cao nhất **138 MB**; sau tải trả về 76 MB (cao hơn nền 24 MB — cache JIT, regex đã biên dịch, pool kết nối).
- Độ dốc RAM nửa sau burst **+1,7 MB/phút** — rất nhỏ, không có dấu hiệu rò bộ nhớ; handle trở về thấp hơn mức nền.
- CPU trung bình chỉ ~1,2 nhân (6,1 % của 20 luồng) khi tải cao → service chủ yếu **chờ I/O** (đọc file, ghi DB), không bị giới hạn CPU.

## 4. Độ ổn định

| Kiểm tra | Kết quả |
|---|---|
| File không có kết quả (mất) | **0** / 25.486 |
| File bị xử lý trùng | **0** |
| Quét chưa trọn (rule timeout) | 0 |
| Đọc file thất bại | 0 |
| Tràn buffer FileSystemWatcher | 0 lần |
| Crash / khởi động lại ngoài ý muốn (PID, Event Log SCM 7031/7034/7023/7024) | Không |
| Lỗi Application Event Log liên quan | Không |
| Dòng log | 366.145 (Information 362.225 · Warning 2.129 · Error 0) |
| Khởi động lại: tới trạng thái Running / tới khi theo dõi thư mục | **0,8 giây / 8,1 giây** |
| Sau khởi động lại: file đã xử lý bị quét lại | **0** (cache size/mtime hoạt động đúng) |

Toàn bộ 2.129 cảnh báo là **cảnh báo nghiệp vụ dự kiến**, không phải lỗi:
- 1.971 × file vào vùng xám của Router (cần LLM thẩm định, `needs_review`);
- 158 × job LLM bị bỏ vì hàng đợi đầy (giới hạn `MaxPendingJobs = 1000`, cố ý chặn đầy hàng đợi khi tải lớn).

8,1 giây "tới khi theo dõi" gồm bước quét lại các file đang có trong watch-folder lúc khởi động (25.000+ file, đều bỏ qua nhờ cache).

## 5. Kết luận

1. **Đúng:** service thật phân loại giống hệt pipeline đánh giá (100 %), không mất, không trùng, không timeout file nào trên 25.486 file.
2. **Nhanh:** ~80 ms xử lý / 425 ms đầu cuối mỗi file khi thả từng file (phần lớn là 300 ms debounce cố ý); dưới tải 25.486 file liên tục
   service theo kịp tốc độ thả ~61 file/giây.
3. **Ổn định:** không crash, không rò bộ nhớ (RAM tối đa 138 MB, trả về sau tải), handle/thread trở về bình thường, khởi động lại
   0,8 giây và không quét lại file cũ.
4. **Đã sửa 1 nút thắt thật:** ghi log đồng bộ có khoá giới hạn service ~75 file/giây; sau khi chuyển sang hàng đợi + luồng nền, bước
   phân loại giảm từ 240 ms xuống 4 ms.

## 5b. Lượt `20261021-161412` — thông lượng tối đa (`-FastDrop`) sau khi sửa ghi DB

Thả file bằng `File.Move` từ thư mục tạm (90 giây cho 25.486 file, thay cho 416 giây copy) → lần này **service là giới hạn**
(xong 154 giây SAU khi thả xong). Độ chính xác vẫn trùng 100 % DlpBaseline.

| | `150346` (copy, trước sửa DB) | `161412` (FastDrop, sau sửa DB) |
|---|---|---|
| Thời gian thả file | 416 giây | 90 giây |
| **Thời gian tới file cuối có kết quả** | 419 giây | **244 giây (4,1 phút)** |
| **Thông lượng** | 61 file/giây (giới hạn bởi copy) | **104 file/giây** (giới hạn bởi service) |
| Song song TB trong service | 18,7 file | 33,3 file |
| Tra cache dưới tải: TB / p95 / max | 14,2 / 80,6 / 666 ms | **3,1 / 13,3 / 207 ms** |
| Ghi DB dưới tải: TB / p95 | 53,9 / 204 ms | 59,8 / 239 ms |
| Trích text dưới tải: TB | 215 ms | 223 ms |
| Tổng/file dưới tải: TB / p95 | 308 / 628 ms | 319 / 603 ms |
| Khởi động lại: tới khi theo dõi thư mục | 8,1 giây | **2,3 giây** |
| RAM max / sau tải | 138 / 76 MB | 190 / 113 MB (độ dốc nửa sau burst −7,6 MB/phút → không rò) |
| Mất / trùng / timeout / tràn buffer / crash | 0 / 0 / 0 / 0 / không | 0 / 0 / 0 / 0 / không |

**Tác động của sửa DB** thấy rõ ở phần ĐỌC (tra cache nhanh ~4,5 lần, khởi động lại nhanh 3,5 lần nhờ kết nối đọc riêng + index) nhưng
**không** thấy ở bước "ghi DB" trong service (60 ms), dù đo riêng bằng `DlpDbBench` cùng code chỉ ~5 ms/lần ở 20 luồng.

**Phát hiện: tranh tài nguyên từ NGOÀI service.** Trong burst, CPU cả máy TB **50 %** nhưng DlpService chỉ **11,4 %** (nền khi rảnh: máy
15 %) → ~35 % CPU (~7 nhân) do tiến trình khác dùng đúng lúc service xử lý; mọi bước (trích text, quét, ghi DB) đều chậm ~3 lần so với
tuần tự trong khi service chỉ dùng ~2,3 nhân. Nghi phạm: **Windows Defender quét thời gian thực** (đang bật) mỗi file docx/pdf service mở
và file DB/WAL service ghi; một phần do chính script đo (đọc ~366.000 dòng log bằng PowerShell). **Chưa xác nhận** — lượt này chưa lấy
mẫu CPU theo từng tiến trình.

## 6. Giới hạn và việc nên làm tiếp

- **Chưa đo được thông lượng tối đa** của service vì script thả file chậm hơn service xử lý. Cách đo tiếp: copy trước vào thư mục
  tạm cùng ổ rồi `Move-Item` từng file vào watch-folder (đổi tên chỉ sửa metadata, thả 25.000 file trong vài giây).
- **Nút thắt kế tiếp dưới tải cao** là ghi DB (p95 204 ms — khoá ghi SQLite tuần tự) và trích text (tranh I/O). Có thể gộp nhiều
  file vào 1 transaction hoặc giới hạn số file xử lý song song.
- Khi copy, Windows Defender quét file mới và tranh I/O với service — chưa tách riêng ảnh hưởng này.
- Đo 1 lần, 1 máy; số thời gian dao động theo tải máy (xem `docs/danh-gia-lop-regex.md` mục 7).
- `LlmSvc` dừng trong lúc đo; khi bật, LLM chạy xen lúc máy rảnh (CPU ≤ 50 %) và tốn thêm RAM của tiến trình riêng.
