# Buổi 7 — Extractor streaming, giữ cấu trúc, thêm .eml, chặn file độc hại

## 0. Tổng kết nhanh

| Hạng mục | Trạng thái | Bước |
|---|---|---|
| Đổi `IContentExtractor` sang trả `IEnumerable<ContentBlock>` (streaming, giữ cấu trúc) | ✅ Xong | 1 |
| `FileReaderService` tiêu thụ stream, dừng sớm khi đủ ký tự | ✅ Xong | 2 |
| Chuyển Txt, Csv, Docx, Excel, Pdf, Pptx sang interface mới | ✅ Xong (6 file) | 3-8 |
| Extractor mới cho `.eml` (tự viết MIME parser) | ✅ Xong | 9 |
| Giới hạn thời gian (30s) + độ sâu đệ quy (20 lớp) | ✅ Xong | 11 |
| Chặn zip bomb (.docx/.xlsx/.pptx) | ✅ Xong | 12 |
| Test bằng file độc hại mẫu (`tools/safety/DlpExtractorSafety`) | ✅ Xong, tất cả PASS | 13 |

**Chưa làm:** Bước 10 (đổi nơi tiêu thụ — Classification/Scanning — để dùng trực tiếp `ContentBlock` có cấu trúc thay vì chuỗi phẳng qua `FileReaderService`). `FileReadResult.Content` hiện vẫn là 1 chuỗi để không phá vỡ `FileWatcherService`/`RegexScanner` hiện có.

---

## 1. Vì sao đổi kiến trúc đọc file

Yêu cầu: "Extractor streaming cho txt/csv, docx, xlsx, pdf (text layer), eml, pptx; giữ cấu trúc (tiêu đề, cột, sheet)".

Trước đây `IContentExtractor.Extract(string filePath)` trả **toàn bộ text 1 lần** (đọc hết file vào RAM rồi mới xử lý), và bảng bị "làm phẳng" thành 1 dòng nối `" | "` — không biết dòng nào thuộc sheet nào, cột nào.

**Giải pháp:** `ContentBlock` — 1 khối nội dung mang theo `Kind` (Heading/Paragraph/TableHeader/TableRow/SectionMarker), `ContextPath` (breadcrumb: `["Sheet1"]`, `["Slide 2", "Ghi chú"]`...), `ColumnNames` (tên cột cho `TableRow`), `SourceIndex` (số dòng/trang/slide). Extractor `yield return` dần từng block; `FileReaderService` ghép lại và **dừng ngay** khi đủ `MaxExtractedChars` — không cần đọc hết file khổng lồ trước.

---

## 2. Giới hạn cứng (chống file độc hại)

| Loại tấn công | Chưa xử lý được (trước buổi này) | Cách chặn |
|---|---|---|
| File "quét mãi không xong" | Có thể chạy vô hạn định | `FileReaderService`: đếm giờ mỗi vòng lặp, > 30s thì dừng, báo lỗi |
| Cấu trúc lồng quá sâu (content control, multipart) làm tràn stack | `StackOverflowException` — **không bắt được bằng try/catch**, crash cả service | Thêm tham số `depth` vào các hàm đệ quy (`DocxExtractor.AppendBlocks`, `EmlMimeParser.Parse`), > 20 lớp thì ném `InvalidDataException` TRƯỚC khi đệ quy tới lớp đó |
| Zip bomb (.docx/.xlsx/.pptx là file ZIP; 1 phần nén vài KB giải nén ra hàng trăm GB) | Có thể làm hết RAM khi thư viện đọc cố giải nén | `ExtractorHelpers.EnsureNotZipBomb`: soi trước `entry.Length` (kích thước SAU giải nén, đọc thẳng từ header ZIP) của từng phần — > 200MB thì từ chối, KHÔNG giải nén thử |

Quan trọng: zip bomb và độ sâu quá lớn phải chặn **trước khi xảy ra** (không thể "bắt lỗi rồi bỏ qua" như lỗi thường, vì OOM/StackOverflow không cho code kịp phản ứng).

---

## 3. `.eml` — bộ phân tích MIME tự viết

Project chưa có thư viện đọc email nào → tự viết `EmlMimeParser.cs` (tối giản, đủ cho email do Outlook/Gmail tạo, không đảm bảo mọi email dị dạng hiếm gặp):

- Tách header/thân bằng dòng trống đầu tiên; unfold dòng tiếp nối (RFC822).
- Giải mã tiêu đề có dấu kiểu `=?UTF-8?B?...?=` / `=?UTF-8?Q?...?=` (RFC 2047) — Subject/From tiếng Việt hiện đúng thay vì chuỗi mã hoá thô.
- Đệ quy tách `multipart/*` theo boundary (có giới hạn độ sâu — mục 2).
- Giải mã `base64` / `quoted-printable` (kể cả dòng nối mềm `=` cuối dòng), rồi decode đúng charset khai trong `Content-Type`.
- Bỏ qua phần không phải `text/*` (ảnh, pdf đính kèm...) — không đưa vào nội dung quét. `text/html` được bỏ thẻ trước khi quét.

Test nhanh (script tạm, đã xoá): email multipart giả có Subject/From tiếng Việt mã hoá, thân `quoted-printable` + `text/html` `base64` → giải mã đúng tất cả, kể cả dòng nối mềm.

---

## 4. Test file độc hại (`tools/safety/DlpExtractorSafety`)

Công cụ tự tạo file mẫu rồi chạy qua `FileReaderService` THẬT:

| Kịch bản | Kỳ vọng | Kết quả |
|---|---|---|
| Zip bomb `.docx`/`.xlsx`/`.pptx` (1 entry ~250MB sau giải nén) | Bị từ chối, xong trong < 10s | PASS — 0.03-0.04s |
| `.docx` lồng 30 lớp content control | Bị từ chối (không crash) | PASS — 0.04s |
| `.eml` lồng 30 lớp multipart | Bị từ chối (không crash) | PASS — 0.03s |
| `.txt` 11MB (giới hạn 10MB) | Bị từ chối | PASS — tức thì |
| Đối chứng: `.docx` lồng 5 lớp (trong hạn mức) | Đọc được bình thường | PASS |
| Đối chứng: `.csv` 5MB bình thường | Đọc được bình thường | PASS |

**Tất cả PASS**, không có trường hợp nào chặn nhầm file hợp lệ. Báo cáo: `tools/safety/results/safety-<ngày giờ>.md`.

**Giới hạn của bộ test:** chưa ép được timeout 30s thật sự xảy ra (mọi kịch bản độc hại đều bị chặn ở lớp kiểm tra kích thước/độ sâu, xong trong mili-giây trước khi kịp chạm ngưỡng thời gian) — coi là hợp lý vì mục tiêu là chặn CÀNG SỚM CÀNG TỐT.

---

## 5. Bảng file đã thay đổi trong buổi 7

| File | Thay đổi | Lý do |
|---|---|---|
| `Reading/IContentExtractor.cs` | Thêm `BlockKind`, `ContentBlock`; `Extract()` trả `IEnumerable<ContentBlock>` | Bước 1 — hợp đồng streaming + giữ cấu trúc |
| `Reading/FileReaderService.cs` | Tiêu thụ block theo stream, dừng sớm; thêm đếm giờ (timeout 30s) | Bước 2, 11 |
| `Reading/Extractors/TxtExtractor.cs` | Mỗi dòng = 1 `Paragraph` block | Bước 3 |
| `Reading/Extractors/CsvExtractor.cs`, `ExtractorHelpers.cs` (`SplitCsvLine`) | Dòng đầu = `TableHeader`, còn lại = `TableRow` kèm `ColumnNames` | Bước 4 |
| `Reading/Extractors/DocxExtractor.cs` | Style "Heading"/"Title" → `BlockKind.Heading`; bảng → `TableHeader`/`TableRow`; header/footer/footnote/comment → `ContextPath`; thêm tham số `depth` | Bước 5, 11 |
| `Reading/Extractors/ExcelExtractor.cs` | Mỗi sheet → `SectionMarker` + `ContextPath`; cột reset theo từng sheet | Bước 6 |
| `Reading/Extractors/PdfExtractor.cs` | Mỗi trang → `SectionMarker`, dòng → `Paragraph` kèm `SourceIndex` = số trang | Bước 7 |
| `Reading/Extractors/PptxExtractor.cs` | Slide/Ghi chú → `ContextPath`; bảng → `TableHeader`/`TableRow` | Bước 8 |
| `Reading/Extractors/EmlMimeParser.cs` (mới), `EmlExtractor.cs` (mới) | Bộ phân tích MIME tối giản + extractor cho `.eml` | Bước 9 |
| `Reading/Extractors/ExtractorHelpers.cs` (`EnsureNotZipBomb`) | Soi trước kích thước giải nén từng phần ZIP | Bước 12 |
| `tools/safety/DlpExtractorSafety/*` (mới) | Test bằng file độc hại mẫu (zip bomb, lồng sâu, quá khổ) | Bước 13 |

---

## 6. Việc có thể làm tiếp

| Việc | Ghi chú |
|---|---|
| Bước 10 — dùng `ContentBlock` có cấu trúc ở `Classification`/`Scanning` | Hiện tại `FileReadResult.Content` vẫn là chuỗi phẳng; báo cáo "phát hiện ở sheet/cột/trang nào" chưa dùng được dữ liệu cấu trúc đã có sẵn |
| Thêm sample `.eml` thật (không phải tự tạo) vào bộ eval `tools/eval/DlpBaseline` | Bộ dữ liệu đo hiện chỉ có docx/pdf |
| OCR cho PDF scan | Vẫn chưa hỗ trợ (đã có cảnh báo từ trước) |
