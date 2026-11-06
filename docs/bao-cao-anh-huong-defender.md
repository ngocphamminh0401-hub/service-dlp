# Báo cáo ảnh hưởng của Windows Defender lên service DLP

*Ngày đo: 22/10, 26/10 và 27/10/2026. Máy đo: Windows 11, CPU 20 luồng. Windows Defender bật bảo vệ thời gian thực.*

## Tóm tắt

- **Defender quét mỗi phiên bản của một file đúng 1 lần.** Với file mới, việc quét có thể diễn ra lúc file được ghi xong hoặc lúc file được mở lần đầu. Mở lại file chưa thay đổi thì không bị quét lại.
- **Service DLP chỉ phải chịu chi phí quét khi file đến bằng cách copy** (copy vào thư mục, giải nén bằng copy, đồng bộ...). File người dùng lưu từ ứng dụng đã được Defender quét ngay lúc lưu. Chi phí đó vẫn có kể cả khi không cài service.
- **Mỗi lần quét tốn khoảng 25–30 ms CPU.** Con số này gấp khoảng 2 lần chính service với cấu hình P/A 1 (13–15 ms/file), và bằng khoảng 2/3 service với cấu hình toàn Tika P/A 2 (41–42 ms/file).
- **Khi xử lý hàng loạt file, tổng CPU của service và Defender khoảng 15% với P/A 1 (đỉnh 28%) và khoảng 25% với P/A 2, đều vượt mục tiêu 10%.** Khi dùng bình thường (vài file mỗi phút), tổng CPU gần 0%.
- **RAM:** Defender thường trực khoảng 370–440 MB, có hay không có service cũng vậy. Khi quét hàng loạt, Defender chỉ tăng thêm khoảng 15 MB. Phần RAM tăng chủ yếu nằm ở service (thêm khoảng 110 MB khi xử lý hàng loạt).
- **Khuyến nghị:** giới hạn tốc độ xử lý của service theo ngân sách CPU **có tính cả Defender**, giới hạn số file xử lý cùng lúc, và bỏ qua file chưa thay đổi. **Không khuyến nghị** cấu hình loại trừ service khỏi Defender làm mặc định.

---

## 1. Defender bị kích hoạt khi service hoạt động như thế nào

### 1.1 Cơ chế

Khi một chương trình mở file để đọc nội dung, Defender chặn lệnh mở lại, quét file rồi mới cho chương trình đọc. Nếu file đã được quét và chưa bị sửa, Defender cho qua ngay.

```
Service mở file ──► Defender kiểm tra: file này (phiên bản này) đã quét chưa?
                        ├─ Chưa  → quét (~25–30 ms) → cho service đọc
                        └─ Rồi   → cho qua ngay (~0 ms)
                    ──► Service trích text, quét dữ liệu nhạy cảm, ghi kết quả
```

- Liệt kê thư mục, đọc kích thước hay ngày sửa của file **không** làm Defender quét. Chỉ việc mở file để đọc nội dung mới kích hoạt.
- Phần CPU của việc quét được tính cho tiến trình Defender (`MsMpEng.exe`), **không** nằm trong CPU của service. Vì vậy chỉ nhìn CPU của service sẽ thấy thấp hơn tải thật.

### 1.2 Quy tắc quan sát được

> Mỗi phiên bản của một file bị Defender quét **đúng 1 lần**, vào thời điểm **sớm hơn** trong hai thời điểm:
> 1. Khi chương trình ghi xong file. **Ngoại lệ:** file được tạo bằng **copy** thì Defender bỏ qua bước này.
> 2. Khi file được mở lần đầu, bởi bất kỳ chương trình nào.
>
> Kết quả quét **đi theo file** khi file được đổi tên hoặc chuyển thư mục trên cùng ổ đĩa. **Bản copy là file mới**, nên bị quét lại dù nội dung giống hệt file đã quét.

### 1.3 Áp dụng vào các tình huống thực tế

| Cách file xuất hiện trong thư mục được theo dõi | Defender quét khi service mở? | Ai chịu chi phí quét |
|---|---|---|
| **Copy vào** (kể cả copy từ một file đã từng được quét) | **Có**, khoảng 25–30 ms | **Service** |
| Ứng dụng **lưu file mới hoặc sửa rồi lưu** | Không | Ứng dụng lưu file, quét ngay lúc lưu |
| Ứng dụng **lưu kiểu Word** (ghi file tạm rồi đổi tên) | Không | Ứng dụng lưu file |
| File đã được mở trước đó rồi mới **chuyển vào** (cùng ổ đĩa) | Không | Chương trình đã mở trước đó |
| Service **mở lại** file chưa thay đổi | Không | Không ai |

Mỗi tình huống đều đo trên 50–100 file docx/pdf, và tất cả file trong cùng tình huống cho kết quả giống nhau. Chi tiết ở Phụ lục A.

---

## 2. Ảnh hưởng đo được

### 2.1 CPU khi service xử lý file

Kết quả lấy từ các lượt đo service thật, trong đó file được thả vào thư mục theo dõi **bằng copy** (trường hợp service phải chịu chi phí quét). Đơn vị là % CPU của cả máy (100% = 20 luồng), dạng trung bình / cao nhất.

| Tải | P/A 1 (bộ đọc cũ + Tika): service | Defender | **Tổng** | P/A 2 (toàn Tika): **tổng** |
|---|---|---|---|---|
| Từng file một | 2,2 / 5,4 | 0,6 | **2,8** / 5,8 | 1,7 / 3,5 |
| Đều 5 file/giây | 0,6 / 4,7 | 1,1 | **1,7** / 5,4 | 2,0 / 3,8 |
| Đều 20 file/giây | 2,0 / 2,6 | 3,8 | **5,8** / 7,4 | 8,5 / 10,5 |
| Đều 50 file/giây | 4,7 / 6,2 | 8,2 | **13,0** / 17,3 | 20,4 / 24,2 |
| **Hàng loạt 25.486 file** | 6,7 / 13,6 | 8,6 | **15,3 / 28,1** | 25,5 / 30,4 |

*Ở tải đều, phần CPU của Defender lớn hơn phần của service.*

### 2.2 Chi phí trên mỗi file

| | Giá trị | Nguồn |
|---|---|---|
| Thời gian Defender quét 1 file docx/pdf | 23–31 ms (pdf khoảng 29 ms, docx khoảng 17 ms) | Bản ghi của chính Defender |
| CPU Defender trên mỗi file | khoảng 33–35 ms | Đo CPU, hai cách đo độc lập cho kết quả khớp nhau |
| CPU service trên mỗi file (P/A 1) | 13–15 ms | Báo cáo hiệu năng |
| CPU service trên mỗi file (P/A 2) | 41–42 ms | Báo cáo hiệu năng |
| **Tổng cho mỗi file đến bằng copy** | **khoảng 45–50 ms (P/A 1), khoảng 70–75 ms (P/A 2)** | |

### 2.3 Tốc độ tối đa nếu giữ tổng CPU dưới 10%

| Máy | Ngân sách 10% | P/A 1: tốc độ tối đa / quét lần đầu 100.000 file | P/A 2: tốc độ tối đa / quét lần đầu 100.000 file |
|---|---|---|---|
| Máy đo (20 luồng) | 2.000 ms CPU mỗi giây | khoảng 35–40 file/giây / khoảng 45 phút | khoảng 25–28 file/giây / khoảng 1 giờ |
| Laptop văn phòng 4 luồng (ước tính) | 400 ms CPU mỗi giây | khoảng 8 file/giây / khoảng 3,5 giờ | khoảng 5 file/giây / khoảng 5,5 giờ |

*Ước tính cho laptop 4 luồng giả định chi phí mỗi file như trên máy đo. Trên CPU chậm hơn, con số thực tế sẽ thấp hơn.*

### 2.4 RAM

Đo khi copy 1.000 file docx/pdf vào thư mục theo dõi. Service xử lý xong trong khoảng 18 giây (khoảng 55 file/giây). Lấy mẫu mỗi giây.

| Tiến trình | Rảnh | Khi xử lý 1.000 file (cao nhất) | 60 giây sau khi xong |
|---|---|---|---|
| Defender (`MsMpEng`) | 368–371 MB (riêng: 423 MB) | **386 MB** (riêng: 438 MB) | 372 MB |
| Service DLP | 176 MB (riêng: 262 MB) | **288 MB** (riêng: 302 MB) | 245 MB |

*Bộ nhớ đang dùng (Working Set). "Riêng" là phần bộ nhớ chỉ tiến trình đó dùng (Private Bytes).*

- **RAM của Defender gần như cố định.** Quét hàng loạt chỉ làm tăng khoảng 15 MB, rồi tự trả lại. Phần RAM này có sẵn trên mọi máy bật Defender, không do service gây ra.
- **RAM của service tăng khoảng 110 MB** khi xử lý hàng loạt, và sau 60 giây chưa trả lại hết. Theo báo cáo hiệu năng (mục 6.4), service tiếp tục trả dần RAM khi máy rảnh lâu hơn, và không thấy dấu hiệu rò rỉ bộ nhớ.
- CPU trong cùng lượt đo: Defender trung bình **8,6%** (cao nhất 12,6%), service trung bình 13,4% (cao nhất 21,6%). Từ đó suy ra Defender tốn khoảng 26 ms CPU mỗi file, khớp với mục 2.2.

*Ghi chú: theo CPU và RAM đo được, service đang cài trên máy đo có vẻ chạy cấu hình toàn Tika (P/A 2). Trong lúc đo, dịch vụ LLM vẫn chạy nhưng gần như không dùng CPU.*

---

## 3. Phương án giải quyết

Chi phí quét của Defender **không tránh được** với file mới đến bằng copy. Mục tiêu là **kiểm soát** chi phí này, không phải loại bỏ nó. **Phương án 1–5 là đề xuất, chưa được triển khai. Phương án 6 đã có sẵn trong service.**

| # | Phương án | Giảm được gì | Mức ưu tiên |
|---|---|---|---|
| 1 | **Giới hạn tốc độ theo ngân sách CPU, tính cả Defender.** Service theo dõi CPU của chính nó và của Defender trong vài giây gần nhất. Nếu tổng vượt ngân sách thì tạm dừng trước khi mở file tiếp theo | Giữ tổng CPU dưới mục tiêu ở mọi mức tải. Đây là **phương án duy nhất chặn được mức trần** | **Bắt buộc** |
| 2 | **Giới hạn số file xử lý cùng lúc** (hiện tại không giới hạn, có lúc 19–32 file song song) | Cắt đỉnh CPU và đỉnh RAM của service khi xử lý hàng loạt. Đây là phương án chính cho phần RAM, vì RAM của Defender gần như cố định. Thông lượng gần như không giảm, vì đã đạt đỉnh ở 4–8 luồng | Cao |
| 3 | **Bỏ qua file chưa thay đổi:** so ngày sửa và kích thước với kết quả đã lưu trước khi mở file | Bỏ hẳn cả phần của service lẫn của Defender với file cũ. Hiệu quả lớn nhất khi khởi động lại máy hoặc quét lại | Cao |
| 4 | **Quét toàn bộ lần đầu chỉ khi máy rảnh hoặc đang cắm sạc** (service đã có sẵn bộ kiểm tra máy rảnh) | Dồn phần tốn nhất vào lúc người dùng không cảm nhận được | Trung bình |
| 5 | Hạ mức ưu tiên tiến trình service, bật chế độ tiết kiệm năng lượng (EcoQoS) | Người dùng ít bị giật máy hơn. **Không** giảm được phần của Defender | Thấp |
| 6 | Gộp các lần sửa liên tiếp của cùng một file (debounce) | Một file được lưu nhiều lần liên tiếp chỉ bị xử lý 1 lần | **Đã có** (300 ms) |

**Cái giá phải trả:** khi xử lý hàng loạt, service sẽ chậm hơn. Ví dụ với P/A 1 trên máy đo, 25.486 file sẽ mất khoảng 11 phút thay vì 6–7 phút như hiện nay.

### Về phương án loại trừ service khỏi Defender

Bộ phận IT có thể cấu hình Defender **không quét file do service mở** (loại trừ theo tiến trình). Làm vậy sẽ tiết kiệm khoảng 25–30 ms CPU cho mỗi file. **Không khuyến nghị dùng làm mặc định**, vì:

- Service **mở và phân tích nội dung** file từ mọi nguồn (tải từ mạng, file đính kèm email...). Hiện tại Defender quét file **trước** khi service phân tích. Đó là một lớp bảo vệ nếu file được tạo ra để tấn công bộ đọc PDF hoặc DOCX.
- Với file đến bằng copy, Defender đã bỏ qua lúc ghi. Nếu loại trừ thêm service, file đó có thể **không được ai quét** cho đến khi người dùng tự mở.

Nếu IT vẫn muốn áp dụng (ví dụ trên máy chủ file có lượng file rất lớn), cần đánh giá rủi ro trên trước và ghi nhận đây là quyết định có chủ đích.

---

## 4. Giới hạn của kết quả và việc chưa đo

- Chỉ đo trên **1 máy** (20 luồng) với **1 phiên bản Defender**. Chưa đo trên laptop 4–8 luồng.
- Mới thử copy bằng PowerShell. **Chưa thử copy bằng Explorer, robocopy hay từ USB.** Khả năng cao kết quả giống nhau, vì đều dùng chức năng copy của Windows, nhưng chưa xác nhận.
- Mới giả lập thao tác "lưu file" bằng script. **Chưa thử với Word, Excel hay trình duyệt khi tải file thật.**
- Việc Defender bỏ qua file copy lúc ghi (Defender ghi lý do là `Optimization`) **không có tài liệu chính thức**. Quy tắc ở mục 1.2 rút ra từ hành vi quan sát được trên khoảng 500 file.
- Máy đo có **thêm một phần mềm bảo mật khác** (`C:\Windows\Sensor`) chưa được đo. Trên máy doanh nghiệp có nhiều phần mềm bảo mật, tải thật có thể cao hơn.
- Các con số ở mục 2.1 lấy từ một lượt đo cho mỗi cấu hình.

---

## Phụ lục A. Các lượt đo

| Lượt | Ngày | Nội dung | Số file | Cách đo |
|---|---|---|---|---|
| Đọc lặp | 27/10 | Đọc 1.000 docx/pdf 2 lần liên tiếp | 1.000 | Bản ghi Defender |
| `_dtest-20261027-142652` | 27/10 | A: copy thẳng vào thư mục theo dõi. B: copy ra thư mục tạm, chờ 15 s, rồi chuyển vào | 100 + 100 | Bản ghi Defender + log service |
| `_dtest2-20261027-145503` | 27/10 | C1: sửa file tại chỗ. C2: lưu kiểu Word. D: chuyển vào file đã được mở trước | 50 × 3 | Bản ghi Defender + log service |
| `_dram-20261027-152915` | 27/10 | RAM và CPU của Defender cùng service khi xử lý hàng loạt (copy vào) | 1.000 | Đo theo tiến trình, mỗi giây |
| `e2e-20261022-101205` | 22/10 | Service thật, cấu hình P/A 1, nhiều mức tải | 25.486 | Đo theo tiến trình, mỗi giây |
| `e2e-20261026-095603` | 26/10 | Service thật, cấu hình P/A 2 | 25.486 | Đo theo tiến trình, mỗi giây |

*Lượt `e2e-20261022-143106` không được dùng cho bảng 2.1, vì lúc máy rảnh Defender đã chạy sẵn ở mức 4,2% CPU (nhiễu nền).*

### Kết quả chính của từng lượt

**Đọc lặp 1.000 file:** mỗi file bị quét đúng 1 lần, do chương trình đọc kích hoạt. Lần đọc 1 mất 28,1 s, trong đó khoảng 26 s là chờ Defender quét. Lần đọc 2 mất 0,1 s, vì Defender dùng lại kết quả đã quét.

**Chuỗi sự kiện Defender trên từng file** (tất cả file trong cùng kịch bản giống hệt nhau):

| Kịch bản | Thứ tự sự kiện |
|---|---|
| A: copy thẳng vào | Copy xong → *bỏ qua* → Service mở → **quét** |
| B: thư mục tạm rồi chuyển vào | Copy xong → *bỏ qua* → Chuyển vào → Service mở → **quét** |
| C1: sửa tại chỗ | Copy xong → *bỏ qua* → Service mở → **quét** → Ghi đè nội dung → **quét lúc ghi** → Service mở lại → *không quét* |
| C2: lưu kiểu Word | Copy xong → *bỏ qua* → Service mở → **quét** → Ghi file tạm → **quét lúc ghi** → Đổi tên → Service mở lại → *không quét* |
| D: chuyển vào file đã mở | Copy ra thư mục tạm → *bỏ qua* → Chương trình khác mở → **quét** → Chuyển vào → Service mở → *không quét* |

### Cách đo lại

```powershell
# PowerShell quyền Administrator: đo kịch bản đọc lặp, A và B, có bản ghi Defender
.\tools\perf\Measure-DefenderOverhead.ps1 -Record -StopLlmSvc
```

Các kịch bản C1, C2, D và phép đo RAM hiện mới có trong script thử nghiệm, chưa được đưa vào `tools/perf/`.
