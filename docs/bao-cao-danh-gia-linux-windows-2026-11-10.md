# Báo cáo đánh giá DlpService bản Linux so với bản Windows (10/11/2026)

- **Phạm vi:** độ chính xác phân loại + hiệu năng (tốc độ, CPU, RAM, độ ổn định) của service Linux trên bộ dữ liệu `rawdata`, so với số
  đo đã có của service Windows. Lớp Regex + Router, **không gọi LLM**.
- **Nhánh / commit:** `feature/linux` @ `1d22bd0`. Code service không đổi trong lượt đánh giá; chỉ sửa 2 chỗ trong công cụ đo (mục 5).
- **Báo cáo chi tiết:**
  - `docs/danh-gia-doi-chieu-linux-windows.md`: độ chính xác, so từng file và so chỉ số tổng.
  - `docs/danh-gia-hieu-nang-linux-windows.md`: hiệu năng, gồm 1 luồng, song song, service thật E2E, tài nguyên, khởi động lại.

## 1. Kết luận

| Tiêu chí | Kết quả | Đánh giá |
|---|---|---|
| **Độ chính xác** | Giống Windows: 320/320 file trùng từng cột; 25.485 file trùng mọi chỉ số (Accuracy 81,6 %, F1 "nhạy cảm" 93,7 %) | ✅ Đạt (GĐ2 bước 2.4) |
| **Tốc độ mỗi file** | Nhanh hơn Windows: 1 luồng 6,3 ms (Windows 8,6–21,9 ms); trong service 10,8 ms (Windows 79,6 ms) | ✅ Tốt hơn |
| **CPU mỗi file** | Khoảng 1/3 Windows (~6 so với ~19 ms ở 50 file/giây), không có chi phí diệt virus | ✅ Tốt hơn |
| **Độ trễ lúc bình thường** | p95 ≈ 330 ms ở 5–50 file/giây (Windows 518–986 ms); phần lớn là 300 ms debounce cố ý | ✅ Tốt hơn |
| **Thông lượng tối đa** | Trong tiến trình 555 file/giây (Windows 737–835). Service thật 142–246 file/giây (Windows 75–104) | ⚠️ Bị giới hạn bởi máy đo 2 vCPU / 3,8 GB, không phải bởi code |
| **Ổn định** | 0 mất, 0 trùng, 0 tràn sự kiện, 0 lỗi trên hơn 72.000 lần xử lý; khởi động lại 3,6 giây, 0 file quét lại | ✅ Đạt |
| **RAM** | RSS 131 MB rảnh, tối đa ~210 MB khi dồn file; chưa thấy trả về sau 2 phút rảnh | ⚠️ Chưa kết luận, cần đo rảnh 30 phút |

**Tóm lại:** bản Linux cho **cùng kết quả phân loại** với bản Windows và **xử lý mỗi file nhanh hơn, nhẹ hơn**, dù chạy trên máy ảo yếu
hơn nhiều. Hai điểm còn mở là RAM sau tải và thông lượng trên máy thật nhiều nhân. Cả hai cần đo thêm, không chặn việc gộp nhánh.

## 2. Môi trường đo

| | Windows (mốc so) | Linux |
|---|---|---|
| Máy | Laptop i7-13700H, 20 luồng, RAM 13,7 GB, SSD | Máy ảo VMware, 2 vCPU (i7-13700H), RAM 3,8 GB |
| Hệ điều hành | Windows 11, .NET 10, Defender bật | Ubuntu 18.04, kernel 4.15, .NET 10.0.14 |
| Dữ liệu | 25.486 file có nhãn | 25.485 file (thiếu `fin_int_00484.pdf` khi chép; coi là tương đương) |
| Ngày đo | 21/10 – 29/10/2026 | 10/11/2026 |
| Service | Bản publish dưới SCM | Bản build Release, tiến trình thường (chưa qua systemd) |

## 3. Độ chính xác

| Phép so | Kết quả |
|---|---|
| 320 file mẫu (`docs/snapshot/baseline-2-llm-tat.tsv`): mức, S1, hash, findings, `needs_review` | **Trùng 100 %**, `git diff` rỗng |
| 25.485 file: Accuracy / Macro-F1 / F1 "nhạy cảm" | 81,6 % / 81,7 % / 93,7 %, **giống Windows** |
| Ma trận nhầm lẫn, 16 lớp, domain, định dạng, ca khó | Trùng. Chỉ 1 ô lệch 1 file, đúng file bị thiếu khi chép |
| Service thật (burst E2E) và `DlpBaseline` trên Linux | Cùng 20.800/25.485 file đúng |

Trích text docx/pdf, regex .NET và công thức điểm cho **cùng kết quả bit-by-bit** trên hai hệ điều hành.

## 4. Hiệu năng

| Chỉ số | Windows | Linux | Ghi chú |
|---|---|---|---|
| 1 luồng (`DlpBaseline`): ms/file · file/giây | 21,9 · 45,6 (máy rảnh: 8,6 · 116) | **6,3 · 157,6** | |
| Trong đó: trích text / quét regex | 20,4 / 1,5 ms | 5,0 / 1,3 ms | regex bằng nhau → chênh ở đọc file + Defender |
| Trong tiến trình, 1 luồng | 251 file/giây | **318** | 1 nhân Linux làm được nhiều hơn |
| Trong tiến trình, tối đa | **737–835** file/giây (4–8 luồng) | 555 (bão hoà ở 2 luồng) | Windows có 10 lần số luồng |
| Service thật, tuần tự: trong service / đầu cuối p95 | 79,6 ms / 470 ms | **10,8 ms / 338 ms** | |
| Tải đều 50 file/giây: đầu cuối p95 | 986 ms | **331 ms** | Windows bắt đầu xếp hàng |
| Lô 5.000 file: cả lô | 69,9 s (72 file/giây) | **10,7–16,1 s** (311–465 file/giây) | Windows lúc chưa giới hạn đồng thời |
| Burst 25.485 file | 244 s (104 file/giây) | 179 s (`MaxConcurrency=2`) / **104 s** (`=6`) | Linux bị chặn bởi đọc đĩa (RAM ít) |
| Ghi DB dưới tải: TB / p95 | 59,8 / 239 ms | **0,7 / 2,3 ms** | Giới hạn đồng thời tránh tranh khoá SQLite |
| Khởi động lại (~41.000 file trong thư mục) | 19 s | **3,6 s** | cả hai: 0 file quét lại |

## 5. Thay đổi trong lượt đánh giá

Chỉ thay đổi công cụ đo. Code service không đổi. Solution build: 0 cảnh báo, 0 lỗi.

| File | Thay đổi |
|---|---|
| `tools/shared/PowerThrottling.cs` | Bỏ qua bước tắt EcoQoS khi không phải Windows. Trước đó `DllNotFoundException` làm công cụ đo dừng trên Linux |
| `tools/eval/DlpBaseline/SampleLoader.cs` | Đổi `\` thành `/` trong đường dẫn của `_metadata.jsonl`. Trước đó Linux không tìm thấy file nào; trên Windows kết quả không đổi |
| `scripts/doi-chieu-do-chinh-xac.py` | **Mới**: tính chỉ số độ chính xác từ `.tsv` của `snapshot-ket-qua.py` |
| `tools/perf/do-e2e-linux.py` | **Mới**: đo E2E service Linux, bản Linux của `Measure-DlpServiceE2E.ps1` |

## 6. Giới hạn

- Hai máy khác xa nhau, nên so **mỗi file / mỗi nhân** có ý nghĩa; so **thông lượng tối đa** chỉ để tham khảo.
- Số Windows được đo ở nhiều lượt cũ. Các lượt E2E Windows đo lúc service chưa có `Watch:MaxConcurrency`.
- Windows có Defender, Linux không. Chưa tách riêng ảnh hưởng này.
- Chưa đo: gói `.deb` dưới systemd, LLM, Tika native, PDF rất lớn, máy Linux thật nhiều nhân, rảnh dài 30 phút.
- Mỗi kịch bản đo 1 lần.

## 7. Việc cần quyết định / làm tiếp

| # | Việc | Đề xuất |
|---|---|---|
| 1 | `Watch:MaxConcurrency` mặc định Linux (D6 = 2) | Giữ 2 cho máy người dùng: lúc bình thường độ trễ như 6. Chỉ đợt dồn lớn mới chậm hơn (142 so với 246 file/giây). Chọn 4 nếu ưu tiên xử lý đợt dồn nhanh |
| 2 | RAM sau tải chưa trả về | Đo rảnh 30 phút; nếu vẫn cao, thử `DOTNET_GCConserveMemory` trong unit systemd |
| 3 | Mốc Windows cùng code | Khi chạy test Windows trước khi gộp `main`, chạy lại E2E với `MaxConcurrency=6`, `-FastDrop -BatchSizes 1,10,100,1000,5000` |
| 4 | Đo trên máy Linux thật (GĐ6) | Ubuntu 22.04/24.04 nhiều nhân, chạy gói `.deb` dưới systemd |
| 5 | Cập nhật `docs/bao-cao-tong-hop-linux-2026-11-06.md` | GĐ2 bước 2.4: ⏳ → ✅ |
