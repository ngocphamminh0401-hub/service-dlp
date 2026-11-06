# Báo cáo đánh giá: Apache Tika so với bộ trích text hiện tại của DlpService

Ngày đo: 12/10/2026. Công cụ: `tools/tika-bench/` (DlpTestGen sinh file test, DlpTikaBench chạy đo). Số liệu thô: `tools/tika-bench/results/summary.md`.

> **Cập nhật 26/10/2026:** đã đo service chạy toàn bộ bằng Tika native (`TikaNative:Mode = Replace`) theo 5 hạng mục đánh giá và so với
> phương án bộ đọc cũ + Tika cho `.doc/.ppt/.rtf` (`Mode = Supplement`). Xem `docs/bao-cao-hieu-nang-service-dlp.md`.
> **Cập nhật cuối ngày 26/10/2026:** đã sửa 3 lỗi của Tika, tối ưu DLL (C1–C3) và thêm pool isolate; service chọn dùng **Tika native
> (`Mode = Replace`, `Isolates = 2`)**. Báo cáo đánh giá service theo 5 hạng mục: `docs/bao-cao-danh-gia-service-dlp.md`.

## 1. Kết luận ngắn

**Không nên thay thế bộ trích hiện tại bằng Tika.** Trên bộ test này, bộ hiện tại nhanh hơn, nhẹ hơn và đọc được những thứ Tika làm mất. Tika thắng ở ba điểm cụ thể: nhận dạng file bằng nội dung (file đổi đuôi), đọc file đính kèm trong email, và cứu được một phần nội dung của file bị cắt cụt. Hướng hợp lý là **giữ bộ hiện tại làm chính, dùng Tika làm lớp bổ sung có chọn lọc** (xem mục 7).

| Chỉ số | Bộ hiện tại | Tika Server | Bên tốt hơn |
|---|---|---|---|
| Thời gian trích file thường (trung vị) | 1,2–17 ms | 9–20 ms | Hiện tại (hơn 2–14 lần ở 7/8 định dạng, ngang ở pdf) |
| RAM nền | 39 MB | 248 MB | Hiện tại |
| RAM đỉnh (có file lớn) | 394 MB | 494 MB | Hiện tại (nhẹ) |
| Chất lượng: txt / csv | 100% | 75% / 70% | **Hiện tại** (Tika mất sạch file Windows-1258) |
| Chất lượng: docx, pptx, xlsx, xls, pdf | 100% | 100% | Hòa |
| Chất lượng: eml | 38,2% | 79,4% | Tika, nhưng mỗi bên bỏ sót một phần khác nhau |
| File đổi đuôi (6 file) | đọc đúng 2/6 | đọc đúng 6/6 | **Tika** |
| Lỗi trên file hợp lệ (174 file) | 3 lỗi + 1 đọc ra rác | 0 | Tika |
| Xử lý file rác/rỗng | từ chối êm hoặc trả text | luôn "thành công", nội dung trống | Xem 5.3 |

## 2. Cách đo

### Bộ file test
- Sinh bằng `DlpTestGen`: **200 file** gồm 8 định dạng (txt, csv, docx, pptx, xlsx, xls, pdf, eml).
- **20 file thường mỗi định dạng**, **8 file lớn** (khoảng 9–11 MB cho txt/csv/eml), **26 file lỗi cố ý** (cắt cụt, byte rác, rỗng), **6 file đổi đuôi**.
- Mỗi file chứa các giá trị đánh dấu **hoàn toàn giả**: số giống CCCD, họ tên tiếng Việt kèm mã `QX…`, email `@example.com`. Chúng nằm ở nhiều vị trí khác nhau: thân văn bản, bảng, header/footer, footnote, comment, text box (docx); ghi chú diễn giả (pptx); sheet thứ hai và sheet ẩn (xlsx/xls); dòng đầu/giữa/cuối và ô có dấu ngoặc kép (csv); tiêu đề, người gửi, thân plain/html, file đính kèm (eml).
- Đáp án đúng nằm trong `manifest.json`. **Chất lượng = % giá trị đánh dấu xuất hiện trong text trích ra** (so sau khi bỏ khoảng trắng, chuẩn hoá NFC, không phân biệt hoa thường).
- txt có 4 bảng mã (UTF-8, UTF-8 BOM, UTF-16 LE, Windows-1258); csv có 3 bảng mã (UTF-8, UTF-8 BOM, Windows-1258) và 2 dấu phân cách.

### Cách chạy
- **Bộ hiện tại** chạy trong một tiến trình .NET con riêng, qua đúng `FileReaderService.TryReadFile` mà service đang dùng. RAM = `PeakWorkingSet64` của tiến trình đó.
- **Tika Server 3.3.2** do tool tự khởi động (`java -jar`, Temurin 17, cổng 9999). Gọi `PUT /tika` với `Accept: text/plain`, không gửi tên file hay Content-Type, để Tika tự nhận dạng bằng nội dung. RAM = tổng WorkingSet của các tiến trình java mới sinh, lấy mẫu mỗi 100 ms.
- Có chạy khởi động 5 file trước khi đo. Trích text và chấm điểm tách riêng, nên chấm điểm không tính vào thời gian hay RAM.
- Cả hai bên cùng áp NFC và cùng cắt ở 2.000.000 ký tự.

### Giới hạn của phép đo (đọc trước khi dùng số liệu)
1. **Một lần chạy, một máy**, không lặp lại. Con số là ms ở mức vài chục, nên nhiễu 1–2 ms là bình thường.
2. Thời gian của Tika **gồm cả truyền file qua HTTP localhost**. Phần lớn chênh lệch ở file nhỏ là chi phí cố định này (khoảng 9 ms), không phải tốc độ phân tích.
3. **File lớn chỉ có 1 file mỗi định dạng**, nên chỉ để tham khảo.
4. Bộ test là **dữ liệu tổng hợp**. File docx/pptx/xlsx do tool tự ghi XML nên cả hai thư viện đều đọc được, nhưng tôi chưa mở thử bằng Word/PowerPoint/Excel. File xls tạo bằng thư viện `xlwt`, không phải do Excel lưu.
5. Tỉ lệ file Windows-1258 trong bộ test (25% txt, 30% csv) là **do tôi chọn**, không phản ánh tần suất thật trong dữ liệu của bạn.
6. **Chưa đo:** `.doc`, `.ppt`, `.rtf`, `.html` (bộ hiện tại không hỗ trợ), PDF scan/OCR, file có mật khẩu, nhiều request đồng thời, hiệu năng khi Tika chạy lâu.

## 3. Thời gian trích text / file

| Định dạng | Hiện tại: trung vị (p95) | Tika: trung vị (p95) | Tika / Hiện tại |
|---|---:|---:|---:|
| txt | 1,4 ms (2,2) | 9,3 ms (18,3) | 6,6x |
| csv | 1,8 ms (3,5) | 9,6 ms (14,2) | 5,3x |
| docx | 8,1 ms (26,2) | 17,2 ms (34,7) | 2,1x |
| pptx | 8,3 ms (18,5) | 20,0 ms (24,8) | 2,4x |
| xlsx | 2,5 ms (5,5) | 12,8 ms (20,3) | 5,2x |
| xls | 1,7 ms (6,1) | 10,2 ms (33,2) | 5,9x |
| pdf | 17,3 ms (52,1) | 20,4 ms (53,0) | 1,2x |
| eml | 1,2 ms (2,6) | 16,8 ms (39,2) | 13,6x |

Nhận xét:
- Với file nhỏ, bộ hiện tại nhanh hơn rõ rệt ở mọi định dạng, nhưng **cả hai đều ở mức vài chục ms**, nên với một service quét nền thì chênh lệch này hiếm khi là nút thắt.
- Trên file lớn thì khác: docx lớn (40.000 đoạn) bộ hiện tại mất 786 ms, Tika 175 ms; pdf lớn (150 trang) 657 ms so với 226 ms. Tika nhanh hơn ở hai ca đó, còn txt, csv, xls, xlsx lớn thì bộ hiện tại nhanh hơn hoặc ngang. Chỉ có 1 mẫu mỗi loại, nên đừng suy rộng.
- Tika có khoảng thời gian khởi động riêng (khoảng 1–2 giây, đo ở bước 1) mà phép đo trên không tính.

## 4. RAM đỉnh

| | Nền sau khởi động | Đỉnh (file thường) | Đỉnh (gồm file lớn) |
|---|---:|---:|---:|
| Bộ hiện tại (tiến trình .NET) | 39 MB | 94 MB | 394 MB |
| Tika Server (java) | 248 MB | 363 MB | 494 MB |

- Tika chiếm **khoảng 250 MB ngay cả khi rảnh** (JVM + nạp các parser), và chạy thêm một tiến trình. Bộ hiện tại nằm trong tiến trình của service, chỉ tốn thêm khi đang xử lý.
- Đỉnh 394 MB của bộ hiện tại đến từ giai đoạn file lớn; ở giai đoạn file thường chỉ 94 MB.
- Số của Tika lấy mẫu mỗi 100 ms nên có thể thấp hơn đỉnh thật một chút. Heap của JVM có thể giới hạn bằng `-Xmx`, nhưng tôi chưa thử.

## 5. Chất lượng text và độ tin cậy

### 5.1 Chất lượng theo định dạng (% giá trị đánh dấu tìm thấy, file thường)

| Định dạng | Số marker | Hiện tại | Tika |
|---|---:|---:|---:|
| txt | 180 | 100% | **75%** |
| csv | 220 | 100% | **70%** |
| docx | 420 | 100% | 100% |
| pptx | 300 | 100% | 100% |
| xlsx | 300 | 100% | 100% |
| xls | 300 | 100% | 100% |
| pdf | 180 | 100% | 100% |
| eml | 680 | **38,2%** | 79,4% |

Với docx, pptx, xlsx, xls, pdf, cả hai bên đều tìm thấy mọi giá trị, kể cả header/footer, footnote, comment, text box, ghi chú diễn giả, sheet ẩn và tiếng Việt có dấu. Ở nhóm này Tika **không thêm giá trị gì** so với bộ hiện tại.

### 5.2 Hai phát hiện lớn

**(a) Tika mất toàn bộ file văn bản dùng bảng mã Windows-1258.** 5 file txt và 6 file csv lưu bằng Windows-1258 (bảng mã ANSI tiếng Việt cũ) đều trả về kết quả rỗng, mà HTTP vẫn là `200` (thành công). Lệnh `detect` cho thấy Tika nhận file đó là `application/octet-stream` chứ không phải text. Bộ hiện tại đọc các file này đúng 100%. Với DLP, đây là **mất dữ liệu âm thầm**, nguy hiểm hơn một lỗi báo rõ. Có thể thử khắc phục bằng cách gửi kèm gợi ý bảng mã, nhưng việc đó cần biết trước bảng mã của từng file, tôi chưa thử.

**(b) Với email, mỗi bên bỏ sót một phần khác nhau.** Kiểm tra trên một file eml mẫu:
- Bộ hiện tại đọc được tiêu đề, người gửi, thân plain, thân html và file đính kèm `.txt`, nhưng **không đọc file đính kèm `.docx`** (0/21 giá trị).
- Tika đọc được cả đính kèm `.docx`, nhưng **không có tiêu đề, người gửi và thân plain** trong text trả về. Nó chọn phần html của `multipart/alternative`. Tiêu đề và người gửi nằm ở metadata (gọi `/meta` mới thấy), không nằm trong text.
- Vì vậy điểm eml của Tika (79,4%) cao hơn chủ yếu nhờ đính kèm; nếu dùng Tika cho email thì cần gọi thêm metadata và tự ghép lại.

### 5.3 File đổi đuôi (nội dung thật khác đuôi)

| File | Hiện tại | Tika |
|---|---|---|
| docx đặt tên `.txt` | **"thành công" nhưng đọc ra rác** (nội dung ZIP), 0/21 giá trị | 21/21 |
| pdf đặt tên `.docx` | báo lỗi | 9/9 |
| xlsx đặt tên `.xls` | đọc đúng 15/15 | 15/15 |
| docx đặt tên `.pdf` | báo lỗi | 21/21 |
| txt đặt tên `.docx` | báo lỗi | 9/9 |
| xls đặt tên `.xlsx` | đọc đúng 15/15 | 15/15 |

Đây là điểm Tika thắng rõ. Đáng chú ý nhất là ca docx đặt tên `.txt`: bộ hiện tại **không báo lỗi** mà trả về chuỗi rác. Với DLP, kẻ muốn lọt dữ liệu chỉ cần đổi đuôi file. Excel thì ổn vì ExcelDataReader tự nhận loại theo nội dung.

## 6. Tỉ lệ lỗi

### 6.1 File hợp lệ (174 file: thường, lớn, đổi đuôi)

| | Hiện tại | Tika |
|---|---:|---:|
| Số file trích thất bại | 3 (1,7%) | 0 (0%) |
| Ca đọc ra rác nhưng báo thành công | 1 (docx đặt tên `.txt`) | 0 |

Cả 3 lỗi của bộ hiện tại đều thuộc nhóm đổi đuôi. **Trên file đúng đuôi, bộ hiện tại không lỗi file nào.** Cần cộng thêm hai lỗi âm thầm của Tika ở 5.2(a) (11 file Windows-1258 trả rỗng nhưng vẫn "thành công"), nên con số 0% của Tika không phản ánh hết.

### 6.2 File lỗi cố ý (26 file)

| Loại | Số file | Hiện tại | Tika |
|---|---:|---|---|
| Cắt cụt (docx/pptx/xlsx/xls/pdf, 2 mức) | 10 | 10 từ chối êm | 8 từ chối (HTTP 422), 2 trả một phần nội dung (pptx, xlsx cắt 70%) |
| Byte rác | 8 | 5 từ chối, 3 trả text (txt/csv/eml đọc coi như văn bản) | 8 trả "thành công" với nội dung trống |
| Rỗng | 8 | 5 từ chối, 3 trả kết quả rỗng (txt/csv/eml) | 8 từ chối (HTTP 422) |

- **Không bên nào treo hoặc sập**, và Tika Server không phải khởi động lại lần nào.
- Tika cứu được một phần nội dung từ file cắt cụt (538 và 409 ký tự). Với DLP, đọc được một phần còn hơn không, nhưng cần ghi cờ "chưa đọc trọn".
- Với byte rác, Tika trả `200` và rỗng. Nếu client chỉ nhìn mã HTTP, file sẽ bị coi là **"sạch"**. Client phải kiểm tra nội dung trống và xử lý như "chưa đọc được".

## 7. Khuyến nghị

1. **Giữ bộ hiện tại làm đường chính** cho 8 định dạng đang hỗ trợ: nhanh hơn, nhẹ hơn, không phụ thuộc Java, và không bị lỗi Windows-1258.
2. **Có thể thêm Tika như lớp bổ sung** cho các ca bộ hiện tại yếu, và chỉ khi bạn chấp nhận chi phí vận hành (JRE, một tiến trình thêm khoảng 250–500 MB RAM, server không có xác thực nên phải giữ ở localhost):
   - file mà đuôi và nội dung không khớp, hoặc bộ hiện tại báo lỗi;
   - file đính kèm trong email (đặc biệt docx/pdf);
   - các định dạng chưa hỗ trợ (`.doc`, `.ppt`, `.rtf`, `.html`), nhưng **đây là điều tôi chưa đo**, cần thử trước khi tin.
3. **Có cách rẻ hơn Tika cho lỗ hổng "đổi đuôi":** nhận dạng loại file bằng vài byte đầu (`PK` cho zip/Office, `%PDF`, `D0 CF 11 E0` cho xls/doc cũ) rồi chọn extractor theo đó. Cách này sửa được ca docx đặt tên `.txt` mà không cần Java. Theo quy trình của dự án, tôi chưa viết gì; nếu bạn muốn, tôi sẽ đưa Change Brief.
4. **Nếu dùng Tika:** coi kết quả rỗng là "chưa đọc được" chứ không phải "sạch"; gọi thêm `/meta` cho email; đặt giới hạn thời gian và dung lượng; chạy thêm một lần đo trên dữ liệu thật của bạn trước khi quyết định.
5. **Việc nên làm tiếp để kết luận chắc hơn:** lặp lại phép đo nhiều lần, thêm file `.doc/.ppt/.rtf`, PDF scan, file có mật khẩu, và đo khi nhiều file được gửi đồng thời.

## 8. Cách chạy lại

```powershell
# Sinh bộ file test (cần python có xlwt để tạo .xls)
cd D:\Service-DLP\tools\tika-bench\DlpTestGen
dotnet run -- ..\corpus <đường-dẫn-python-có-xlwt> 20

# Chạy benchmark (cần java trong PATH và tika-server-standard-3.3.2.jar)
cd D:\Service-DLP\tools\tika-bench\DlpTikaBench
dotnet run -- corpus-all ..\corpus --jar D:\tools\tika\tika-server-standard-3.3.2.jar --out ..\results
```
Kết quả nằm ở `tools/tika-bench/results/summary.md`. Thư mục `results/` và `corpus/` chứa text và file tổng hợp (không có dữ liệu thật), có thể xoá và sinh lại bất cứ lúc nào.

---

## 9. Bổ sung (13/10/2026): bản native của Tika (GraalVM) — Bước 2

Đã biên dịch Tika thành `tika_native.dll` bằng GraalVM native-image và gọi từ C# (xem `tools/tika-native/README.md`). Số liệu dưới đây là **cùng bộ 200 file** ở mục 2, cùng cách chấm điểm. Một lần chạy, một máy.

### 9.1 Kết quả bốn chỉ số (3 engine)

| Chỉ số | Bộ hiện tại | Tika Server | **Tika native (DLL)** |
|---|---|---|---|
| Thời gian trung vị / file thường | 1,2–17 ms | 9–20 ms | 2–8 ms (riêng eml 98 ms) |
| RAM nền | 39 MB | 248 MB | 80 MB |
| RAM đỉnh (file thường / gồm file lớn) | 94 / 394 MB | 363 / 494 MB | 145 / 307 MB |
| Chất lượng | 100% ở 7/8 định dạng trừ eml | giống bản native | **giống Tika Server từng chỉ số** (txt 75%, csv 70%, eml 79,4%, 5 định dạng 100%) |
| File đổi đuôi (6 file) | đọc đúng 2/6 | 6/6 | 6/6 |
| Lỗi trên 174 file hợp lệ | 3 | 0 | 0 |

Ghi chú về cách đọc: RAM của "Bộ hiện tại" và "Tika native" đều là **đỉnh của cả tiến trình .NET** (bản native gồm cả isolate Java nằm trong tiến trình đó); RAM của Tika Server là tổng các tiến trình java, **chưa gồm** tiến trình .NET gọi nó. Thời gian của Tika Server gồm cả truyền qua HTTP.

### 9.2 Độ tin cậy của bản native
- **Tổng quát hoá (quan trọng nhất):** lần đo đầu cho kết quả 100% trên corpus nhưng đó là phép thi bằng đề đã học (cấu hình sinh từ chính corpus). Thử trên 773 file thật chưa huấn luyện thì **hỏng 435 file** (374 PDF, 61 docx) vì thiếu tài nguyên trong jar (bảng mã font của PDFBox, schema XMLBeans của POI). Sau khi khai báo tài nguyên và huấn luyện agent trên một nửa file thật:
  - 386 file thật chưa từng thấy: **0 lỗi, 378 giống hệt bản JVM**; 8 file PDF thiếu 1–2 URL hyperlink (chưa rõ nguyên nhân).
  - 98 file thật khác (`.doc`, `.ppt`, `.xls`, `.xlsx`, `.rtf`, `.csv` từ thư mục chương trình trên máy): **98/98 giống hệt bản JVM**, dù bản native chưa được huấn luyện trên các loại này.
  - Corpus tổng hợp thứ hai (hạt giống khác): kết quả giống hệt corpus đầu.
- **Đa luồng:** chạy 4 luồng (mỗi luồng attach vào isolate) không lỗi, chất lượng không đổi. Chỉ là một lần thử, **không phải bằng chứng an toàn luồng**.
- **Ghim mã băm DLL:** ghim sai SHA-256 thì tool từ chối nạp (cơ chế học từ CsNgdpCc.dll).
- **`tika_gc`:** đỉnh RAM cuối giảm từ 307 xuống 267 MB, nhưng chỉ một lần đo, chưa đủ để kết luận.

### 9.3 Cái giá phải trả
- **Kích thước:** `tika_native.dll` 96 MB, kèm các DLL phụ của JDK (`awt.dll`, `fontmanager.dll`, `freetype.dll`, `java.dll`...) vì PDFBox kéo theo AWT. Vậy không phải "một DLL duy nhất".
- **Build:** khoảng 70–85 giây, đỉnh RAM khi build khoảng 3,6–4,3 GB, cần GraalVM JDK và Visual Studio Build Tools (chỉ lúc build, không cần trên máy chạy).
- **Bảo trì:** mỗi lần đổi phiên bản Tika hoặc thêm định dạng phải kiểm lại cấu hình; lỗi thiếu tài nguyên chỉ lộ ra lúc chạy với file cụ thể. Phải luôn kiểm thử trên file chưa từng dùng để huấn luyện.
- **Cách ly:** mã Tika chạy **trong tiến trình của service**. Một lỗi nghiêm trọng khi parse file xấu có thể làm sập cả service, khác với Tika Server (tiến trình riêng). Chưa đo.
- **Điểm yếu chưa giải thích:** eml chậm (khoảng 125 ms/file ổn định, trong khi Tika Server nóng chỉ 15–25 ms); 8 file PDF thiếu hyperlink.

### 9.4 Những gì bản native KHÔNG sửa (vì là hành vi của chính Tika)
- File Windows-1258 vẫn mất sạch (txt 75%, csv 70%): Tika nhận chúng là nhị phân và trả rỗng.
- Email: không đưa tiêu đề, người gửi, thân plain vào text (chỉ thân html và đính kèm).
- File rác trả "thành công" với nội dung rỗng; phía gọi phải coi rỗng là "chưa đọc được".

### 9.5 Kết luận cập nhật
Bản native **chứng minh được khả thi và đúng về chất lượng**, nhẹ hơn Tika Server khoảng 3 lần ở RAM nền (80 so với 248 MB) và nhanh hơn ở hầu hết định dạng, nhưng:
1. Không vượt bộ hiện tại ở 7/8 định dạng (txt, csv thậm chí kém hơn do lỗi Windows-1258).
2. Giá trị thật của nó (ở mức đã đo) là **nhận dạng bằng nội dung, đọc đính kèm email, và các định dạng cũ** (`.doc`, `.ppt`, `.rtf`) mà không cần Java hay tiến trình phụ.
3. Cái giá là DLL ~100 MB, quy trình build/kiểm thử phức tạp, và mất lớp cách ly tiến trình.

Khuyến nghị thực tế: nếu dùng, nên dùng bản native như **lớp bổ sung có chọn lọc** (khi bộ hiện tại báo lỗi hoặc đuôi file không khớp nội dung, và cho định dạng cũ), không thay thế bộ hiện tại; và khắc phục Windows-1258 ở phía C# (thử giải mã cp1258 khi Tika trả rỗng với file văn bản).

### 9.6 Bổ sung: huỷ parser, timeout, giới hạn bộ nhớ, `tika_detect`, đóng gói
| Hạng mục | Kết quả đo (một lần, một máy) |
|---|---|
| Huỷ parser | Đã có `tika_shutdown` (ABI 2); `NativeEngine.Dispose` gọi nó trước khi huỷ isolate. Chưa có phép đo riêng cho việc giải phóng bộ nhớ. |
| Timeout | Kiểu cộng tác. Hạn 3 ms: 117/200 file báo quá hạn (mã 7), không sập; file lớn dừng sau 3–40 ms. Hạn: xls lớn dừng sau 213 ms vì phải nạp cả workbook trước. Mặc định 30 giây. |
| Giới hạn heap | Heap 64 MB: chỉ 1 file (xls lớn) báo lỗi bộ nhớ, 199 file còn lại đúng, tiến trình không sập. Bản dùng thử build heap 256 MB: chất lượng không đổi, RAM đỉnh cuối khoảng 259 MB (so với 307 MB khi không đặt giới hạn). |
| `tika_detect` qua DLL | Trung vị 0,85 ms. Đúng cho mọi file nhị phân, kể cả 6/6 file đổi đuôi và file bị cắt cụt. File txt/csv Windows-1258 (5 + 6 file) bị nhận là `application/octet-stream`: đây chính là nguyên nhân gốc của lỗi mất dữ liệu Windows-1258. |
| Đóng gói | Chỉ cần `tika_native.dll` (+ VC++ runtime) cho mọi thứ đã thử, kể cả 484 file thật. Các DLL phụ của JDK (~4 MB) chưa thấy cần, nên giữ như bảo hiểm. |

### 9.7 Tích hợp vào DlpService (13/10/2026)
Đã thêm bộ đọc `TikaNativeExtractor` cho `.doc`, `.ppt`, `.rtf` (mặc định **tắt**, bật bằng `TikaNative:Enabled`, bắt buộc ghim `Sha256`). Các định dạng đang có không bị đổi.

| Kiểm tra | Kết quả |
|---|---|
| Test đơn vị (không cần DLL) | 31/31 đạt, gồm: mặc định tắt, thiếu DLL, thiếu/sai mã băm đều từ chối êm; tách dòng ghép lại đúng nội dung gốc |
| Kịch bản qua `FileReaderService` | Không cấu hình: `.doc/.ppt/.rtf` không đăng ký. Bật không ghim hoặc ghim sai: cảnh báo, service chạy tiếp. Bật đúng: đăng ký đủ 3 đuôi |
| Đọc thật 50 file (4 doc, 6 ppt, 40 rtf) | **50/50 giống hệt bản Tika trên JVM**; trung vị 1,5 ms (doc, ppt), 4,8 ms (rtf) |
| Song song 8 luồng × 150 lượt đọc | 150/150 giống hệt, không lỗi (mỗi lần gọi attach/detach isolate) |
| Quá hạn 1 ms | Báo `TikaNativeTimeoutException` qua `FileReaderService`, service không sập |
| Đóng gói | `-p:BundleTikaNative=true` copy DLL vào `native\tika`; build thường **không** copy (tránh mỗi project công cụ phình 100 MB) |

Chưa kiểm: chạy toàn bộ service (Worker + watcher) với `Enabled = true`; chỉ kiểm qua đúng `FileReaderService`. Chưa thử file `.doc/.ppt/.rtf` có mật khẩu, và chưa thử sập DLL giữa chừng.

### 9.8 Benchmark Tika native trên thư mục đang theo dõi và trên dữ liệu có nhãn (13/10/2026)
So bộ đọc hiện tại với Tika native trên **docx và pdf**, đo bằng chuỗi giống service (đọc → quét regex → phân loại → định tuyến). Công cụ: `folder-compare`, `labelled-compare` trong `tools/tika-bench`.

**(a) Thư mục `watch-folder` (773 file thật: 375 pdf, 375 docx, 23 txt rỗng, 61,8 MB; không file nào lỗi ở cả hai bên).** Đo 3 vòng xen kẽ, một luồng.
| Chỉ số | Bộ cũ | Tika native |
|---|---:|---:|
| Tổng thời gian trích 773 file | 10,0–11,5 s | 8,3–9,9 s |
| Trung vị / file (docx, pdf) | 5,1 / 18,5 ms | 5,0 / 14,6 ms |
| RAM nền, đỉnh (một luồng) | 75, 106–110 MB | 143, 192–205 MB |
| RAM đỉnh (4 luồng) | (chưa đo) | 270 MB; 773 file trong 5,7 s cả tiến trình |
| Cùng số chỗ khớp regex | 744/773 (96,2%) | |
| Cùng mức nhạy cảm | 772/773 | 1 file Internal → Public |
| Cùng vùng định tuyến | 765/773 | 8 file đổi vùng |
| Tổng số chỗ khớp | 4.106 | 4.097 |

Text trích ra **khác nhau gần hết** (docx 292/375 khác nội dung, pdf 375/375 khác), nhưng kết luận gần như không đổi. Chín file có kết luận khác, xem ngữ cảnh đã che chữ số, nguyên nhân là **bố cục**, không phải đọc thiếu chữ:
- Bộ cũ nối ô bảng bằng ` | ` trên cùng một dòng; Tika đặt mỗi ô một dòng. Các rule "từ khoá + giá trị cùng câu" (VD "Số hộ chiếu | PZ…") mất khớp ở native (1 docx: Certain → Low).
- Bộ cũ cho PDF chèn nhãn "Trang N" vào giữa câu, làm hỏng cụm từ; native không có lỗi này nên bắt được thêm (`business_internal_001`).
- Native bắt thêm mã nhân viên trong bảng (`internal_code_001`); bộ cũ bắt thêm vài chỗ `vn_name_001` có vẻ là tên đường ("Nguyễn Trãi") hoặc tên mẫu ("Nguyễn Văn A").
- Tôi chỉ xem ngữ cảnh của 14 file; **không kết luận được bên nào đúng cho từng file** vì thư mục này không có nhãn.

**(b) Dữ liệu có nhãn `D:\dlp-classifier\rawdata` (12.743 mẫu × docx + pdf = 25.486 file; dữ liệu tổng hợp), 8 luồng.**
| Chỉ số | Bộ cũ | Tika native |
|---|---:|---:|
| Nhị phân "nhạy cảm": Precision / Recall / F1 | 87,2% / 96,5% / 91,6% | 87,1% / 96,6% / 91,6% |
| FP / FN | 1.812 / 452 | 1.823 / 441 |
| Đúng 4 mức (Accuracy / Macro-F1) | 77,4% / 77,3% | 77,4% / 77,3% |
| Ca khó (borderline): Recall | 92,7% | 93,0% |
| Đọc lỗi | 0 | 0 |
| Tổng thời gian trích cộng dồn các luồng | 739 s | 273 s |

Phép đo tái hiện đúng baseline cũ (87,2% / 96,5% / 77,4%). Hai bộ đọc cho cùng mức ở 25.406/25.486 file (99,7%); chỉ bộ cũ đúng: 37 file, chỉ native đúng: 42 file (chênh lệch này không có ý nghĩa thống kê).

**Đánh giá:** về độ chính xác DLP, Tika native **tương đương** bộ cũ ở docx và pdf (không hơn, không kém); nhanh hơn (1,2 lần ở một luồng trên `watch-folder`, khoảng 2,7 lần ở tổng thời gian cộng dồn trên dữ liệu có nhãn với 8 luồng; con số sau gồm tranh chấp giữa các luồng, nên đừng suy rộng); đổi lại tốn RAM nhiều hơn (nền khoảng gấp đôi, đỉnh khoảng gấp 1,8 lần). Không có lý do về chất lượng để thay bộ cũ ở docx/pdf. Giá trị của native vẫn là các định dạng mà bộ cũ không đọc được (`.doc`, `.ppt`, `.rtf`).
Bộ rule đã được điều chỉnh dựa trên bố cục text của bộ cũ; nếu sau này thay bộ đọc, cần đánh giá lại bằng dữ liệu có nhãn, đặc biệt các rule dựa vào "cùng câu/cùng dòng" trong bảng.

**Phát hiện về độ an toàn:** khi chạy thử bằng `Parallel.For` có `localInit` attach luồng và `localFinally` detach, GraalVM sập tiến trình lúc huỷ isolate (`Fatal error: Must either be at a safepoint or in native mode`). Nguyên nhân (đã xác nhận bằng cách sửa): phần dọn dẹp có thể chạy ở luồng khác luồng đã attach, mà handle isolate chỉ hợp lệ trên đúng luồng đó. Sửa bằng luồng riêng attach và detach trong cùng một luồng thì hết sập. Quy tắc cho mọi code gọi DLL: **attach và detach phải nằm trong cùng một lần gọi, cùng một luồng, không được trải qua `await`** (đúng như `TikaNativeLibrary.WithThread` đang làm).
