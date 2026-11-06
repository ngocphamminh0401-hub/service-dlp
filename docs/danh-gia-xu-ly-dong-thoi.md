# Đánh giá hiệu năng khi xử lý đồng thời

- **Ngày đo:** 2026-10-22. Hai phần:
  - **A. Trong tiến trình** (`tools/perf/DlpConcurrencyBench`) — đo khả năng mở rộng theo số luồng, kiểm tra an toàn đa luồng. ✅ Đã đo.
  - **B. Trên service thật** (`tools/perf/Measure-DlpServiceE2E.ps1 -BatchSizes ... -EditStorm -LockedFile -PostIdleProbe`) — lô file đồng
    thời, ghi đè dồn dập, file bị khoá. ✅ Đã đo (lượt `20261022-143106`) — phát hiện lỗi: file bị khoá không được thử lại.
- Service hiện **không giới hạn số file xử lý cùng lúc**: mỗi sự kiện file → 1 task trên thread pool. Các lượt burst trước đo được ~19–33
  file xử lý song song.

## A. Khả năng mở rộng trong tiến trình

**Cách đo:** 4.000 file rawdata (16 lớp × docx/pdf), mỗi file qua đúng các bước của `FileWatcherService.ProcessFile` (tra cache DB → trích
text → băm → quét → phân loại + S1 + Router + FileConfidence → ghi DB SQLite). Mỗi mức song song: **N luồng riêng** cùng lấy file từ 1 hàng
đợi, **dùng chung 1 bộ đối tượng pipeline** (như service) và 1 DB tạm mới. Trước khi đo: đọc trước mọi file (bộ đệm file Windows) + chạy trọn
1 lượt 1 luồng không tính (JIT tối ưu dần — thử thứ tự 1→2→1→1 cho thấy lượt đầu chậm hơn ~40 % chỉ vì chạy trước). Máy: i7-13700H
(20 luồng logic). Kết quả thô: `tools/perf/results/concurrency-20261022-105231.md` + `.csv`.

| Song song | Thời gian (giây) | File/giây | Tăng tốc | Độ trễ/file p50 / p95 / p99 / max (ms) | Trích TB | Quét TB | Ghi DB TB | Lỗi | Lệch kết quả | Bản ghi DB | RAM đỉnh | Thread đỉnh |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 16,0 | 251 | ×1,00 | 3,5 / 6,4 / 14,1 / 57 | 2,4 | 1,1 | 0,4 | 0 | 0 | 4000/4000 | 116 MB | 15 |
| 2 | 8,1 | 493 | ×1,97 | 3,6 / 6,1 / 17,8 / 48 | 2,5 | 1,1 | 0,4 | 0 | 0 | 4000/4000 | 120 MB | 16 |
| **4** | 5,7 | **707** | ×2,82 | 4,9 / 9,8 / 22,4 / 41 | 3,5 | 1,4 | 0,6 | 0 | 0 | 4000/4000 | 128 MB | 18 |
| **8** | 5,4 | **737** | **×2,94** | 9,1 / 25,5 / 33,9 / 55 | 6,9 | 2,3 | 1,4 | 0 | 0 | 4000/4000 | 134 MB | 22 |
| 16 | 6,1 | 651 | ×2,60 | 22 / 48 / 61 / 91 | 12,6 | 4,0 | 7,4 | 0 | 0 | 4000/4000 | 146 MB | 30 |
| 32 | 6,9 | 577 | ×2,30 | 53 / 91 / 122 / 167 | 16,0 | 4,9 | **33,5** | 0 | 0 | 4000/4000 | 160 MB | 45 |
| 64 | 8,6 | 464 | ×1,85 | 140 / 256 / 335 / 486 | 19,5 | 5,1 | **111,2** | 0 | 0 | 4000/4000 | 167 MB | 77 |

**PDF lớn** (16 file × 1,5 triệu ký tự, ~500 trang) chạy song song:

| Song song | Thời gian cả 16 file | Độ trễ/file TB / max | RAM đỉnh | Lỗi |
|---|---|---|---|---|
| 1 | 14,8 giây | 0,9 / 1,2 giây | 147 MB | 0 |
| **4** | **7,8 giây** | 1,9 / 2,0 giây | 165 MB | 0 |
| 16 | 9,5 giây (chậm hơn 4) | **9,3 / 9,5 giây** | **452 MB** | 0 |

### Nhận xét

1. **An toàn đa luồng: đạt.** Ở mọi mức 1–64 luồng: 0 lỗi, 0 file đổi mức so với chạy 1 luồng, DB đủ 4.000/4.000 bản ghi — reader,
   scanner, engine, FileConfidence và ResultStore dùng chung giữa các luồng không gây sai kết quả hay mất dữ liệu.
2. **Thông lượng bão hoà ở 4–8 luồng (~710–740 file/giây, ×2,8–2,9)** rồi **giảm** khi tăng tiếp (32 luồng 577, 64 luồng 464 file/giây). Tăng tốc
   tối đa chỉ ~×3 dù máy có 20 luồng logic: các phần dùng chung tuần tự hoá công việc — **khoá ghi SQLite** (1 kết nối ghi) và bộ thu gom rác
   (pipeline cấp phát ~1,6 MB/file).
3. **Độ trễ tăng mạnh khi quá nhiều luồng:** p95 từ 6–10 ms (≤ 4 luồng) lên 91 ms (32) và 256 ms (64). Thời gian ghi DB tăng từ 0,4 ms lên
   111 ms — gần như toàn bộ là **chờ khoá ghi** (DB đo riêng ở `DlpDbBench` chỉ ~0,3 ms/lần ghi).
4. **Service hiện chạy ở vùng xấu:** dưới tải burst, service tự xử lý ~19–33 file song song (không giới hạn) — đúng vùng thông lượng đã giảm
   và độ trễ cao (khớp với đo service thật: ghi DB TB 54–60 ms, p95 204–239 ms).
5. **PDF lớn song song là rủi ro bộ nhớ:** 16 PDF lớn cùng lúc → RAM đỉnh 452 MB (gấp 3 lần), mỗi file chờ ~9 giây, và **tổng thời gian còn chậm
   hơn** chạy 4 luồng. Trên máy đang ít RAM trống (LlmSvc giữ 10–12 GB), nhiều file lớn đến cùng lúc có thể gây thiếu bộ nhớ.

### Khuyến nghị (cần quyết định — chưa sửa code)

**Giới hạn số file xử lý cùng lúc trong service** (VD `SemaphoreSlim` trong `FileWatcherService.ProcessAfterDebounceAsync`, cấu hình
`Watch:MaxConcurrency`, mặc định 4–8): theo số đo, cùng thông lượng tối đa (~700 file/giây trong tiến trình) nhưng độ trễ p95 giảm ~10 lần so
với 32 luồng, ghi DB hết chờ khoá, RAM đỉnh khi gặp nhiều file lớn được chặn. Có thể giới hạn chặt hơn cho file lớn (VD > 5 MB chỉ 1–2 file cùng lúc).

## B. Trên service thật — lượt `20261022-143106`

Service bản publish (đã sửa watcher), chạy dưới SCM, `LlmSvc` dừng. Kết quả thô: `tools/perf/results/e2e-20261022-143106-*`.

### B1. Thả cùng lúc N file (`-BatchSizes`)

File được chuẩn bị sẵn trong thư mục tạm rồi `Move` vào thư mục theo dõi (không phải copy), đo tới khi file cuối có kết quả.

| Lô (file) | Thả trong | Xong | Cả lô | Thông lượng | Độ trễ đầu-cuối p50 / p95 / max | Xử lý trong service TB / p95 | Mất | Trùng | Đọc lỗi | Tràn buffer |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 4 ms | 1/1 | 0,4 s | — | 413 / 413 / 413 ms | 56 / 56 ms | 0 | 0 | 0 | 0 |
| 10 | 16 ms | 10/10 | 0,5 s | 19 file/s | 508 / 529 / 529 ms | 99 / 104 ms | 0 | 0 | 0 | 0 |
| 100 | 0,5 s | 100/100 | 1,7 s | 59 file/s | 1,1 / 1,3 / 1,3 s | 144 / 200 ms | 0 | 0 | 0 | 0 |
| 1.000 | 2,9 s | 1.000/1.000 | 14,8 s | **68 file/s** | 10,6 / 11,8 / 11,9 s | 240 / 372 ms | 0 | 0 | 0 | 0 |
| 5.000 | 30,4 s | 5.000/5.000 | 69,9 s | **72 file/s** | 40,5 / 42,7 / 43,7 s | 217 / 374 ms | 0 | 0 | 0 | 0 |

- **Không mất, không trùng, không tràn buffer ở mọi cỡ lô** (kể cả 1.000 file xuất hiện trong 2,9 giây).
- Lô 10 file vẫn xong trong ~0,5 giây. Từ 100 file trở lên, thông lượng chững ở **~70 file/giây** → file đến nhanh hơn thế sẽ **xếp hàng**:
  lô 1.000 file thì file cuối chờ ~12 giây, lô 5.000 thì ~43 giây. Không mất file, chỉ chậm.
- Thời gian xử lý trong service tăng từ 56 ms (1 file) lên ~240 ms/file khi có hàng trăm file chạy song song — đúng xu hướng của phần A
  (quá nhiều file cùng lúc → tranh khoá ghi DB, trích text chậm lại).

### B2. Ghi đè dồn dập (`-EditStorm`)

1 file `storm.docx` bị ghi đè 20 lần, mỗi lần cách 100 ms, xen kẽ nội dung Public / Restricted, lần cuối là Restricted.

- Service xử lý **đúng 1 lần** (14:41:00), cho kết quả **Restricted = đúng nội dung cuối**. Debounce 300 ms gộp 20 lần ghi thành 1 lần xử lý,
  không đọc phải file đang ghi dở.
- ⚠️ Script đo in ra "0 lần xử lý" là **lỗi của script** (xem B5). Số trên lấy từ log service.

### B3. File bị khoá (`-LockedFile`) — ❌ phát hiện lỗi thật của service

3 file được thả vào rồi bị chương trình khác mở khoá độc quyền trong 5 giây.

- 14:41:18 — service đọc thất bại cả 3 file: `Đọc thất bại ...: File đang bị khóa`.
- Sau khi mở khoá, service **không thử lại**: suốt **38 phút** tiếp theo 3 file này không có kết quả.
- 15:19:27 — 3 file chỉ được quét (đều ra Restricted) khi **service khởi động lại**, nhờ bước quét toàn thư mục lúc khởi động.
- **Hệ quả thực tế:** file đang được Word / Excel / phần mềm sao lưu giữ khoá đúng lúc service đọc sẽ **bị bỏ sót cho đến lần khởi động lại sau**.
  Với DLP, đây là lỗ hổng: file Restricted nằm đó mà không bị phát hiện.
- ⚠️ Script đo in "đọc lỗi 0, xử lý 0/3": số "0/3" đúng, nhưng "đọc lỗi 0" sai vì cùng lỗi script ở B5. Log có 3 lần đọc lỗi.

### B4. Sau 30 phút rảnh (`-PostIdleProbe`) — ✅ bản sửa watcher đã được kiểm chứng

20/20 file thả vào sau 1.800 giây rảnh đều được xử lý ngay (15:19:12). Lỗi watcher bị GC thu hồi của lượt `085800` **đã hết** trên service thật.

### B5. Lỗi của script đo và ghi chú khác

- **Lỗi script:** hàm `Poll-Into` trả về cả danh sách như một phần tử (`return , $lines`). Do đó `@(Poll-Into ...) | Where-Object { $_.Contains(...) }`
  gọi `List.Contains` trên cả danh sách (so khớp nguyên dòng) thay vì `String.Contains` trên từng dòng → không dòng nào khớp. Lô N file không bị
  ảnh hưởng vì kết quả lô được ghi bên trong `Poll-Into`. **Đã sửa:** thêm hàm `Poll-Matching` (lọc từng dòng); thử trên log lượt `143106`
  tìm đúng 1 lần xử lý storm và 3 lần đọc lỗi (cách cũ: 0).
- **Khởi động lại:** từ Running tới "Đang theo dõi" mất **19 giây** (lượt trước 2,9 giây). Lý do: lúc khởi động service quét toàn thư mục theo dõi
  (41.468 file, bỏ qua 41.465 file không đổi, quét 3 file bị khoá ở trên) **trước khi** bật watcher. Thời gian này tăng theo số file trong thư mục,
  và file mới xuất hiện trong khoảng đó có nguy cơ bị lỡ (cần kiểm thêm).
- Kết quả các giai đoạn khác lặp lại lượt `101205`: Accuracy 81,6 %, 0 mất / 0 trùng / 0 timeout trên 25.486 file; burst 75 file/giây; tải đều
  50 file/giây p95 641 ms; rảnh 23 phút RAM giảm, handle giảm → không rò rỉ.

### Kết luận phần B

| Câu hỏi | Kết quả |
|---|---|
| Nhiều file cùng lúc có mất / trùng / tràn buffer? | ✅ Không (tới 5.000 file/lô, 25.486 file burst) |
| Thông lượng tối đa trên service thật | ~70–75 file/giây; vượt mức này file xếp hàng, độ trễ tăng tuyến tính theo kích thước lô |
| Ghi đè dồn dập → kết quả cuối đúng? | ✅ Đúng, 20 lần ghi → 1 lần xử lý |
| File bị khoá có được xử lý lại? | ❌ **Không** — bỏ sót tới lần khởi động lại sau |
| Watcher sống sau rảnh dài? | ✅ 20/20 sau 30 phút |
