# Buổi 14: Đánh giá service với Tika native, sửa lỗi và tối ưu bộ trích text

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái |
|---|---|
| Chế độ `TikaNative:Mode` (Supplement / Replace) + `IContentExtractor.Priority` | ✅ Xong |
| Tham số `--extractor legacy\|supplement\|tika`, `--tika-dll`, `--tika-isolates` cho công cụ đo | ✅ Xong (`tools/shared/ExtractorSwitch.cs`) |
| Đo 5 hạng mục: bộ đọc cũ / Supplement / Tika (A-B-B-A lạnh/nóng) + E2E Tika lượt A `094454`, B `095603` | ✅ Xong |
| Báo cáo so sánh 2 phương án (lúc đầu chọn Supplement) | ✅ `docs/bao-cao-hieu-nang-service-dlp.md` |
| **Sửa 3 lỗi của Tika** (cắt âm thầm, file bị khoá báo là hỏng, Windows-1258 trả rỗng) | ✅ Xong + đo xác nhận |
| **Kiểm chứng nguyên nhân Tika tốn CPU** (H1–H6) bằng `DlpTikaBench tika-profile` | ✅ Xong |
| **Tối ưu Tika:** C1–C3 (DLL), 1a pool isolate (C#); thử C4 / 2a / H3 | ✅ Giữ C1–C3 + 1a (+2a vô hại); bỏ C4, H3 |
| Thay DLL `build\` (`23b995a9…` → `9be007f9…`), cấu hình `Isolates: 2` | ✅ Xong; 96/96 test |
| **Báo cáo đánh giá service (Tika native, 5 hạng mục)** | ✅ `docs/bao-cao-danh-gia-service-dlp.md` |
| Deploy bản tối ưu lên service | xem mục 5 |

## 1. Thay đổi code
- `Reading/Native/TikaNativeOptions.cs`: `TikaNativeMode { Supplement, Replace }`, `Mode`; `Isolates` (mặc định 1).
- `Reading/Native/TikaNativeLibrary.cs`: **pool K isolate** (chọn theo vòng tròn; attach/detach vẫn trong 1 lần gọi); Tika đọc dư
  `TruncationMargin = 4.096` ký tự để C# phát hiện phần bị cắt.
- `Reading/Native/TikaNativeRuntime.cs`: `ReplacesAll`; log ghi chế độ + số isolate.
- `Reading/IContentExtractor.cs` + `FileReaderService.cs`: `Priority` (cao thắng khi trùng đuôi).
- `Reading/Extractors/TikaNativeExtractor.cs`: Replace nhận 11 đuôi; txt/csv Tika trả rỗng → giải mã lại bằng `TextFileReader`;
  mã lỗi → thử mở file để ném đúng `IOException` / `UnauthorizedAccessException`.
- `tools/tika-native/.../TikaExtractor.java`: C1 bỏ NFC Java; C2 `FastDetector` (đuôi khớp chữ ký byte đầu → bỏ nhận dạng nặng, giữ
  `AutoDetectParser` để chống zip bomb và đọc tài liệu nhúng; đường nhanh lỗi thì đọc lại theo đường cũ); C3 SAX docx/pptx; 2a kiểm tra
  hạn mỗi 64 sự kiện; công tắc đo `DLP_TIKA_EXP_*`. `NativeApi.java`: in thống kê GC khi bật `DLP_TIKA_EXP_GCSTATS`.
- `tools/tika-native/build-dll-exp.cmd` (heap, young gen %, thư mục ra); `DlpTikaBench tika-profile` (bấm giờ tách phần, `--isolates`).
- Test: thêm 9 test (giải mã lại văn bản, file bị khoá, đọc thật qua DLL với 2 isolate khi đặt `DLP_TIKA_DLL`); 96/96 đạt.

## 2. Kết quả chính
- Độ chính xác (Tika): Accuracy 81,7 %, F1 "nhạy cảm" 93,7 %; 0/25.486 file đổi mức qua mọi bản sửa và tối ưu.
- Tối ưu: trích 1 file 7,2–7,6 → 6,1 ms; thông lượng đỉnh 5A 447 → 591 file/s (2 isolate). Text giống hệt trên 684 file.
- Nguyên nhân còn lại: H5 (AOT chậm hơn JIT khoảng 1,6–1,75 lần), H6 (Serial GC khoảng 46 % thời gian ở 16 luồng, đã giảm bằng isolate).

## 3. Bài học
- Bộ đệm file của Windows làm lệch số đo (lượt chạy sau nhanh hơn). Luôn đo A-B-B-A hoặc đọc trước file; chạy xen kẽ các biến thể.
- Nhiễu giữa các lượt khoảng 12–25 %; chỉ giữ thay đổi vượt mức nhiễu.
- Đọc mã (bytecode) của thư viện trước khi "đi đường tắt": gọi thẳng parser sẽ mất chống zip bomb + đọc tài liệu nhúng → đổi sang thay bộ nhận dạng.
- Nâng giới hạn young gen không làm Serial GC chạy thưa hơn; tách isolate mới hiệu quả.
- `tail -F` (Git Bash) giữ khoá file log; mỗi bước nên ghi log riêng.

## 4. Lỗi còn mở
File bị khoá không được thử lại; file đọc lỗi không có bản ghi; file > 2 triệu ký tự chỉ quét phần đầu; chưa giới hạn số file đồng
thời; eml chưa lấy tiêu đề/người gửi.

## 4b. Sửa triệt để lỗi Windows-1258 (cuối buổi)
- Phát hiện thêm: file Windows-1258 có nhiều chữ không dấu, Tika **không trả rỗng** mà đoán nhầm sang Windows-1252, ra chữ lỗi dấu
  ("Nguyá»…n"). Bản sửa cũ (chỉ chạy khi Tika trả rỗng) không bắt được: 3 file thử có 0/40, 0/10, 0/3 cụm tiếng Việt đúng.
- Sửa: `TikaNativeExtractor.IsLegacyEncodedText`: txt/csv không rỗng, không BOM, không có chữ ký nhị phân (zip/pdf/rtf/OLE), không có
  byte 0, và **không phải UTF-8 hợp lệ** (`Utf8.IsValid`) → giải mã bằng `TextFileReader`, không qua Tika. Giữ bản sửa cũ làm lớp bảo vệ thứ 2.
- Kiểm: 4 file thử đúng 100 % (0 ký tự lỗi dấu); corpus txt/csv 100 %, đổi đuôi 6/6; 98 file thật và 178 file corpus text không đổi;
  97/97 test; deploy lại và kiểm trên service thật: 14/14 file Windows-1258 được phát hiện đúng.

## 5. Trạng thái máy cuối buổi và việc còn mở
- ✅ Service đang chạy bản tối ưu + mọi bản sửa (Replace, 2 isolate, DLL `9be007f9…`), deploy lúc 17:02. Bản `publish\` trước buổi lưu ở
  `scratchpad/publish-backup`.
- ✅ `LlmSvc` đã chạy lại. ⚠️ Hàng đợi LLM có khoảng 16.874 job pending sinh ra từ file thử của các lượt đo (file đã xoá): cần dọn.
- Việc tiếp: E2E A + B với bản tối ưu (thay số [SV] trong báo cáo); `Watch:MaxConcurrency`; thử lại file bị khoá; JFR + PGO.
