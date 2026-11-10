# Buổi 20 (10/11/2026): Đánh giá bản Linux so với Windows trên `rawdata` — độ chính xác + hiệu năng

Tổng hợp: **`docs/bao-cao-danh-gia-linux-windows-2026-11-10.md`**. Chi tiết: `docs/danh-gia-doi-chieu-linux-windows.md` (độ chính xác),
`docs/danh-gia-hieu-nang-linux-windows.md` (hiệu năng).

| Hạng mục | Trạng thái |
|---|---|
| Ảnh 320 file Linux so với `docs/snapshot/baseline-2-llm-tat.tsv` | ✅ trùng 100 % từng cột |
| 25.485 file qua service Linux thật: Accuracy / F1 "nhạy cảm" | ✅ 81,6 % / 93,7 %, trùng Windows (lệch 1 file do thiếu `fin_int_00484.pdf` khi chép, coi là tương đương) |
| `DlpBaseline` 1 luồng trên Linux | ✅ 6,3 ms/file, 157,6 file/s (Windows 8,6–21,9 ms) |
| `DlpConcurrencyBench` 1–64 luồng | ✅ 318 file/s ở 1 luồng, bão hoà ~555 file/s ở 2 vCPU; 0 lệch kết quả |
| E2E service thật: tuần tự, tải đều 5/20/50, lô 1→5.000, burst, khởi động lại | ✅ `MaxConcurrency` 2 và 6; 0 mất / 0 trùng / 0 lỗi |
| GĐ2 bước 2.4 | ✅ Đạt |
| Cập nhật `docs/bao-cao-tong-hop-linux-2026-11-06.md` | ✅ |

## Thay đổi code (chỉ công cụ đo)
- `tools/shared/PowerThrottling.cs`: bỏ qua EcoQoS khi không phải Windows (trước đó `DllNotFoundException` trên Linux).
- `tools/eval/DlpBaseline/SampleLoader.cs`: đổi `\` thành `/` trong đường dẫn `_metadata.jsonl` (trước đó Linux không tìm thấy file nào).
- Mới: `scripts/doi-chieu-do-chinh-xac.py` (tính chỉ số từ `.tsv`), `tools/perf/do-e2e-linux.py` (bản Linux của `Measure-DlpServiceE2E.ps1`).

## Ghi chú / bài học
- Máy đo là VMware 2 vCPU / 3,8 GB: so **mỗi file / mỗi nhân** có ý nghĩa, so thông lượng tối đa chỉ tham khảo.
- Regex chạy cùng tốc độ hai bên (1,3 so với 1,5 ms/file). Chênh lệch nằm ở trích text (đọc đĩa, Defender trên Windows).
- Burst Linux chậm hơn thả lô vì RAM không giữ hết file trong bộ đệm (đọc đĩa 10–18 MB/s, CPU chưa kín).
- Log Linux chỉ ghi thời gian tới giây, nên script đo tự bấm giờ lúc thấy dòng `[Perf]` (đọc log mỗi 20 ms).
- Mỗi file vùng xám tạo 2 dòng Warning khi LLM tắt. Đây là cảnh báo nghiệp vụ dự kiến, không phải lỗi.

## Cần quyết định
1. `Watch:MaxConcurrency` mặc định Linux: giữ 2 hay 4.
2. Đo RAM sau 30 phút rảnh (2 phút sau tải chưa trả về).
3. Chạy lại E2E Windows với `MaxConcurrency=6` để có mốc so cùng code.
