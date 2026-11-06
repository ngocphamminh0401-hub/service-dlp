# tika-native — thử nghiệm biên dịch Apache Tika thành bản native (GraalVM)

Trạng thái: **đang thử nghiệm (đã có DLL, gọi được từ C#)**. Chưa gắn vào DlpService. Thiết kế dựa trên `CsNgdpCc.dll` của CrowdStrike
(xem `docs/bao-cao-so-sanh-tika.md` và lịch sử trao đổi).

## Cấu trúc
- `src/main/java/dlp/tika/TikaExtractor.java` — lõi trích text (thuần Java, chạy được trên JVM).
- `src/main/java/dlp/tika/NativeApi.java` — lớp vỏ `@CEntryPoint`: tika_abi_version, tika_init, tika_detect, tika_open, tika_read, tika_close, tika_gc (đọc text theo khối, mọi lỗi đổi thành mã, không ném exception ra ngoài).
- `src/main/java/dlp/tika/ExtractCli.java` — chương trình dòng lệnh: chạy trên JVM, làm bài tập mẫu cho tracing agent, và biên dịch thành `.exe` để thử.
- `src/main/resources/META-INF/native-image/dlp.tika/tika-native/reachability-metadata.json` — khai báo TAY các tài nguyên trong jar phải đưa vào bản native (schema XMLBeans của POI, bảng mã font PDFBox...).
- `native-config/` — cấu hình reflection... do tracing agent SINH RA (chạy lại bằng `run-agent.cmd`; có thể xoá và sinh lại).
- `*.cmd` — script build/chạy (đều `call D:\tools\graalvm\graal-env.cmd`). File .cmd phải ghi CRLF + ASCII.

## Quy trình
1. `build-jvm.cmd` — Maven build, chép thư viện vào `target\libs`.
2. `run-agent.cmd` — chạy trên JVM kèm tracing agent để sinh `native-config\`.
3. `build-cli-native.cmd` — `native-image` ra `build\tikacli.exe`.
4. `build-dll.cmd` — `native-image --shared` ra `build\tika_native.dll` + `tika_native.h` (kèm các DLL phụ của JDK như awt.dll, phải để cùng thư mục).
5. Chấm điểm bằng benchmark: `dotnet run -- corpus-score <corpus> --dir <thư-mục-kết-quả>` (xem `tools/tika-bench`).

## Cờ native-image bắt buộc (đã gặp lỗi khi thiếu)
- `-H:+AddAllCharsets` — thiếu thì POI lỗi `UnsupportedCharsetException: CP1252`.
- `--initialize-at-build-time=...XZUtils,LZMAUtils,ZstdUtils,BrotliUtils` — các lớp commons-compress dò thư viện nén tuỳ chọn; thiếu thì `NoSuchMethodError` lúc chạy.
- Thư viện `jakarta.mail-api` trong pom — thiếu thì `AngusActivationFeature` làm hỏng lúc build.

## Bài học
- Cấu hình do agent sinh chỉ phủ những gì lần chạy mẫu đã đụng tới. Chấm điểm trên chính tập đã huấn luyện cho kết quả đẹp giả: lần đầu corpus đạt 100% nhưng 435/773 file thật hỏng.
  Luôn chia tập huấn luyện / tập kiểm tra chưa từng thấy.
- Bật `DLP_TIKA_DEBUG=1` để in stack trace (mặc định tắt vì stack trace có thể chứa nội dung file).
- Cảnh báo `Log4j API could not find a logging provider` (từ POI) vô hại.

## Gọi từ C# (xem tools/tika-bench/DlpTikaBench/NativeEngine.cs)
- Nạp bằng đường dẫn đầy đủ; tính SHA-256 của DLL và so với mã băm ghim trước khi nạp.
- `graal_create_isolate` một lần; mọi luồng khác phải `graal_attach_thread` trước và `graal_detach_thread` sau.
- Chạy: `dotnet run -- corpus-extract-native <corpus> --dll <tika_native.dll> --out <thư-mục> [--threads N] [--gc-large] [--sha256 <mã>]`
- So sánh nhiều engine: `dotnet run -- corpus-compare <corpus> --engines "tên=thư-mục,tên=thư-mục" --md <file.md>`

## Cấu hình lúc chạy và giới hạn (Bước 2.6)
- `tika_init(maxChars, timeoutMs)`: giới hạn ký tự (mặc định 2.000.000) và hạn thời gian mỗi file (mặc định 30.000 ms). Quá hạn trả mã 7, không trả text dở dang.
  Timeout là kiểu CỘNG TÁC (kiểm tra ở mỗi sự kiện parse), không dừng được parser kẹt mà không phát sự kiện (VD xls lớn phải nạp cả workbook trước).
- `tika_shutdown()`: huỷ parser, đóng kết quả còn dở, thu gom rác; sau đó tika_init lại được. `graal_tear_down_isolate` vẫn do bên gọi.
- Giới hạn heap Java: tham số build, VD `build-dll.cmd 256` (-R:MaxHeapSize=256m). Thử: heap 64 MB chỉ làm 1 file (xls lớn 60.000 dòng) báo lỗi bộ nhớ (mã 6), tiến trình không sập.
  Bản dùng thử hiện tại build với 256 MB.
- ABI hiện tại = 2.

## Đóng gói
- `tika_native.dll` chỉ import DLL hệ thống + VC++ runtime (VCRUNTIME140, VCRUNTIME140_1, UCRT). Đã thử chạy corpus, 386 file thật và 98 file thật khác chỉ với MỘT DLL (không kèm awt.dll, fontmanager.dll...): kết quả giống hệt khi có đủ DLL.
- Các DLL phụ của JDK (awt, fontmanager, freetype, lcms, mlib_image, javajpeg, java, jvm: khoảng 4 MB) chỉ nạp khi gặp đường dùng AWT mà chưa thử; nếu thiếu thì file đó báo lỗi (không sập). Nên đặt cạnh DLL chính như bảo hiểm rẻ.

## Bản đang dùng (cập nhật 26/10/2026)
- `build\tika_native.dll` — SHA-256 `9be007f9284f035d8fd9fa9675ee800909007b815f3188e0932e7afee2d1ccc7`, build bằng
  `build-dll-exp.cmd 256 10 build-exp2` (heap 256 MB, young gen 10 %) rồi chép sang `build\`. Bản trước (`23b995a9…`) lưu ở `build-backup-23b995a9\`.
- Tối ưu so với bản trước (đo trên 4.000 file, xem `docs/bao-cao-hieu-nang-service-dlp-tika.md`): C1 bỏ NFC phía Java (C# đã làm);
  C2 nhận dạng nhanh khi đuôi file khớp chữ ký byte đầu (vẫn dùng AutoDetectParser → giữ chống zip bomb + đọc tài liệu nhúng;
  không khớp → nhận dạng gốc); C3 đọc docx/pptx kiểu SAX; 2a kiểm tra hạn mỗi 64 sự kiện. Text đầu ra giống hệt bản trước trên 684 file.
- Công tắc lúc chạy (biến môi trường = 1, chỉ để đo so sánh): `DLP_TIKA_EXP_NFC`, `DLP_TIKA_EXP_AUTODETECT`, `DLP_TIKA_EXP_DOM` (bật lại
  hành vi cũ), `DLP_TIKA_EXP_GCSTATS` (in số lần/thời gian GC khi `tika_shutdown`).
- Phía .NET: `TikaNative:Isolates` (mặc định 1, cấu hình đề xuất 2) — mỗi isolate có heap + Serial GC riêng nên GC không chặn lần đọc ở
  isolate khác. Đổi DLL thì PHẢI cập nhật `TikaNative:Sha256`.
- Đã thử và KHÔNG giữ: nâng young gen (`-R:MaximumYoungGenerationSizePercent` 25–40 %, heap 384/512 MB) — số lần GC gần như không đổi.

## Quy tắc an toàn khi gọi DLL từ .NET (đã gặp sự cố thật)
- Handle luồng của isolate (kết quả graal_create_isolate/graal_attach_thread) chỉ hợp lệ trên ĐÚNG luồng hệ điều hành đã attach.
- `graal_attach_thread` và `graal_detach_thread` phải nằm trong CÙNG MỘT lần gọi, cùng luồng, KHÔNG được trải qua `await` hay chuyển luồng.
- Đã gặp: dùng `Parallel.For` với `localInit` (attach) và `localFinally` (detach) làm tiến trình sập lúc huỷ isolate
  ("Fatal error: Must either be at a safepoint or in native mode"). Dùng luồng riêng tự attach/detach thì hết sập.
