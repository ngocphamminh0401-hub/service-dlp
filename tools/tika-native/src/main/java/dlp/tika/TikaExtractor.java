// [Thay đổi] File mới (Bước 2.2): lõi trích text bằng Apache Tika, thuần Java (chạy được trên JVM thường).
// Lớp vỏ native (@CEntryPoint) ở bước sau chỉ gọi lại lớp này; không để logic nằm trong lớp vỏ.
// [Thay đổi] Bước 2.6: thêm timeout kiểu CỘNG TÁC (kiểm tra hạn ở mỗi sự kiện parse, không tạo luồng phụ nên không rò rỉ luồng
// chạy mãi) và giới hạn ký tự cấu hình được.
package dlp.tika;

import java.io.IOException;
import java.io.InputStream;
import java.io.StringWriter;
import java.nio.file.Path;
import java.text.Normalizer;

import org.apache.tika.detect.Detector;
import org.apache.tika.exception.EncryptedDocumentException;
import org.apache.tika.exception.WriteLimitReachedException;
import org.apache.tika.exception.ZeroByteFileException;
import org.apache.tika.io.TikaInputStream;
import org.apache.tika.metadata.Metadata;
import org.apache.tika.parser.AutoDetectParser;
import org.apache.tika.parser.ParseContext;
import org.apache.tika.sax.BodyContentHandler;
import org.apache.tika.sax.ContentHandlerDecorator;
import org.apache.tika.sax.WriteOutContentHandler;
import org.xml.sax.Attributes;
import org.xml.sax.ContentHandler;
import org.xml.sax.SAXException;

/** Trích text từ 1 file bằng Tika. Mọi lỗi đều đổi thành mã trạng thái, không ném exception ra ngoài. */
public final class TikaExtractor {

    /** Giới hạn ký tự mặc định, khớp với FileReaderService của DlpService (MaxExtractedChars). */
    public static final int MAX_CHARS = 2_000_000;

    /** Hạn thời gian mặc định cho 1 file, khớp với ExtractionTimeout (30 giây) của FileReaderService. */
    public static final int DEFAULT_TIMEOUT_MS = 30_000;

    /** Mã trạng thái (khớp bảng mã trong thiết kế Bước 2; số nguyên để trả qua ranh giới native). */
    public enum Status {
        OK(0), UNSUPPORTED(2), CORRUPT(3), ENCRYPTED(4), EMPTY(5), INTERNAL(6), TIMEOUT(7);

        public final int code;
        Status(int code) { this.code = code; }
    }

    /** Kết quả 1 lần trích. text luôn khác null (rỗng nếu không có). */
    public record Result(Status status, String text, boolean truncated, String mime, String detail) { }

    /** Ném ra khi quá hạn; được nhận ra ở extract() để trả TIMEOUT thay vì coi là file hỏng. */
    private static final class DeadlineExceededException extends SAXException {
        DeadlineExceededException() { super("quá hạn"); }
    }

    /**
     * Kiểm tra hạn ở mọi sự kiện parse. Hầu hết parser (docx, pdf, xlsx...) phát sự kiện liên tục nên dừng được sớm.
     * Giới hạn: nếu 1 parser kẹt trong vòng lặp bên trong mà KHÔNG phát sự kiện nào thì không dừng được.
     */
    private static final class DeadlineHandler extends ContentHandlerDecorator {
        // [Thay đổi] Tối ưu 2a: chỉ đọc đồng hồ mỗi CHECK_EVERY sự kiện (trước: MỌI sự kiện SAX — hàng nghìn lần/file). Hạn vẫn được
        // kiểm tra, chỉ trễ tối đa 64 sự kiện (cỡ micro giây) — không đáng kể so với hạn 30 giây.
        private static final int CHECK_EVERY = 64;
        private final long deadlineNanos; // 0 = không giới hạn
        private int events;

        DeadlineHandler(ContentHandler next, long deadlineNanos) {
            super(next);
            this.deadlineNanos = deadlineNanos;
        }

        private void check() throws SAXException {
            if (deadlineNanos != 0 && ++events % CHECK_EVERY == 0 && System.nanoTime() - deadlineNanos > 0) {
                throw new DeadlineExceededException();
            }
        }

        @Override
        public void startElement(String uri, String localName, String qName, Attributes atts) throws SAXException {
            check();
            super.startElement(uri, localName, qName, atts);
        }

        @Override
        public void characters(char[] ch, int start, int length) throws SAXException {
            check();
            super.characters(ch, start, length);
        }

        @Override
        public void ignorableWhitespace(char[] ch, int start, int length) throws SAXException {
            check();
            super.ignorableWhitespace(ch, start, length);
        }
    }

    // [Thay đổi] Tối ưu C1/C2/C3 (đã kiểm chứng bằng đo đạc, xem docs/bao-cao-hieu-nang-service-dlp-tika.md). Mỗi tối ưu có 1 công
    // tắc TẮT (biến môi trường = 1, đọc 1 lần lúc khởi tạo) để đo so sánh với hành vi cũ trong cùng 1 DLL:
    //   C1 bỏ NFC phía Java (C# FileReaderService đã chuẩn hoá NFC)        — tắt bằng DLP_TIKA_EXP_NFC
    //   C2 nhận dạng nhanh khi đuôi file khớp vài byte đầu                 — tắt bằng DLP_TIKA_EXP_AUTODETECT
    //   C3 đọc docx/pptx kiểu SAX (luồng) thay DOM (dựng cả cây đối tượng) — tắt bằng DLP_TIKA_EXP_DOM
    private static boolean expFlag(String name) { return "1".equals(System.getenv(name)); }
    private final boolean useNfc = expFlag("DLP_TIKA_EXP_NFC");
    private final boolean fullDetectOnly = expFlag("DLP_TIKA_EXP_AUTODETECT");
    private final boolean useDom = expFlag("DLP_TIKA_EXP_DOM");

    /** [Thay đổi] C2: khoá metadata RIÊNG (Tika không đọc) chứa đuôi file gốc — chỉ bộ nhận dạng nhanh dùng. Cố ý KHÔNG đặt
     *  RESOURCE_NAME_KEY: tên file sẽ làm bộ nhận dạng gốc đoán theo đuôi và hỏng khả năng phát hiện file đổi đuôi. */
    private static final String EXT_KEY = "X-DLP-Ext";

    /**
     * [Thay đổi] C2: bộ nhận dạng nhanh. Đuôi file khớp chữ ký vài byte đầu → trả ngay loại MIME (bỏ bước mở zip/OLE để đoán, chiếm
     * ~14–27 % thời gian trích). Không khớp (file đổi đuôi), không có đuôi (tài liệu NHÚNG bên trong file) → bộ nhận dạng gốc như cũ.
     * AutoDetectParser vẫn giữ nguyên: chống zip bomb (SecureContentHandler) và đọc tài liệu nhúng.
     */
    private static final class FastDetector implements Detector {
        private final Detector fallback;
        FastDetector(Detector fallback) { this.fallback = fallback; }

        @Override
        public org.apache.tika.mime.MediaType detect(InputStream in, Metadata m) throws IOException {
            String ext = m.get(EXT_KEY);
            if (ext != null && in != null && in.markSupported()) {
                byte[] h = new byte[8];
                in.mark(8);
                int n;
                try {
                    n = in.readNBytes(h, 0, 8);
                } finally {
                    in.reset();
                }
                String mime = match(ext, h, n);
                if (mime != null) {
                    return org.apache.tika.mime.MediaType.parse(mime);
                }
            }
            return fallback.detect(in, m);
        }

        private static String match(String ext, byte[] h, int n) {
            boolean zip = n >= 4 && h[0] == 'P' && h[1] == 'K' && h[2] == 3 && h[3] == 4;
            boolean pdf = n >= 4 && h[0] == '%' && h[1] == 'P' && h[2] == 'D' && h[3] == 'F';
            boolean ole = n >= 8 && (h[0] & 0xff) == 0xD0 && (h[1] & 0xff) == 0xCF && (h[2] & 0xff) == 0x11 && (h[3] & 0xff) == 0xE0
                    && (h[4] & 0xff) == 0xA1 && (h[5] & 0xff) == 0xB1 && (h[6] & 0xff) == 0x1A && (h[7] & 0xff) == 0xE1;
            boolean rtf = n >= 5 && h[0] == '{' && h[1] == '\\' && h[2] == 'r' && h[3] == 't' && h[4] == 'f';
            return switch (ext) {
                case "docx" -> zip ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document" : null;
                case "xlsx" -> zip ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : null;
                case "pptx" -> zip ? "application/vnd.openxmlformats-officedocument.presentationml.presentation" : null;
                case "pdf" -> pdf ? "application/pdf" : null;
                case "doc" -> ole ? "application/msword" : null;
                case "xls" -> ole ? "application/vnd.ms-excel" : null;
                case "ppt" -> ole ? "application/vnd.ms-powerpoint" : null;
                case "rtf" -> rtf ? "application/rtf" : null;
                default -> null; // txt, csv, eml...: nhận dạng gốc vốn đã rẻ
            };
        }
    }

    /** [Thay đổi] C3: ParseContext dùng chung cấu hình đọc docx/pptx kiểu SAX (trừ khi bật công tắc DLP_TIKA_EXP_DOM). */
    private ParseContext newContext() {
        ParseContext ctx = new ParseContext();
        if (!useDom) {
            org.apache.tika.parser.microsoft.OfficeParserConfig c = new org.apache.tika.parser.microsoft.OfficeParserConfig();
            c.setUseSAXDocxExtractor(true);
            c.setUseSAXPptxExtractor(true);
            ctx.set(org.apache.tika.parser.microsoft.OfficeParserConfig.class, c);
        }
        return ctx;
    }

    // 1 parser dùng chung cho mọi lần gọi: tự nhận dạng loại file bằng NỘI DUNG, không dựa vào đuôi file
    private final AutoDetectParser parser = new AutoDetectParser();
    private final Detector detector = parser.getDetector(); // bộ nhận dạng GỐC: tika_detect vẫn dùng nó (không đổi hành vi)

    {
        // [Thay đổi] C2: parser dùng bộ nhận dạng nhanh bọc bộ gốc. Không có EXT_KEY (tài liệu nhúng, hoặc tắt C2) → y hệt bộ gốc.
        parser.setDetector(new FastDetector(detector));
    }
    private final int maxChars;
    private final int timeoutMs;

    public TikaExtractor() {
        this(MAX_CHARS, DEFAULT_TIMEOUT_MS);
    }

    /** maxChars <= 0 hoặc timeoutMs <= 0: dùng mặc định / không giới hạn thời gian tương ứng. */
    public TikaExtractor(int maxChars, int timeoutMs) {
        this.maxChars = maxChars > 0 ? maxChars : MAX_CHARS;
        this.timeoutMs = Math.max(timeoutMs, 0);
    }

    /** Nhận dạng loại file (MIME) bằng nội dung. Trả "application/octet-stream" nếu không nhận ra hoặc lỗi. */
    public String detect(Path file) {
        try (InputStream in = TikaInputStream.get(file)) {
            return detector.detect(in, new Metadata()).toString();
        } catch (Exception e) {
            return "application/octet-stream";
        }
    }

    private static boolean causedByDeadline(Throwable e) {
        for (Throwable t = e; t != null; t = t.getCause()) {
            if (t instanceof DeadlineExceededException) {
                return true;
            }
        }
        return false;
    }

    /**
     * Trích text. Không gợi ý TÊN file cho Tika, để kết quả không phụ thuộc đuôi file (giống phép đo benchmark).
     * [Thay đổi] C2: thử đường nhanh (đuôi file chỉ dùng cho bộ nhận dạng nhanh, khớp chữ ký byte đầu mới có tác dụng). Đường nhanh
     * báo hỏng/lỗi nội bộ → đọc lại 1 lần bằng nhận dạng gốc, để file lạ (VD zip thường đặt đuôi .docx) xử lý y như trước.
     */
    public Result extract(Path file) {
        if (fullDetectOnly) {
            return extractOnce(file, null);
        }
        String name = file.getFileName().toString();
        int dot = name.lastIndexOf('.');
        String ext = dot >= 0 ? name.substring(dot + 1).toLowerCase(java.util.Locale.ROOT) : null;
        Result r = extractOnce(file, ext);
        if (ext != null && (r.status() == Status.CORRUPT || r.status() == Status.INTERNAL)) {
            return extractOnce(file, null);
        }
        return r;
    }

    /** [Thay đổi] C2: 1 lần trích; ext != null → bộ nhận dạng nhanh được dùng đuôi này (qua khoá metadata riêng EXT_KEY). */
    private Result extractOnce(Path file, String ext) {
        String mime = "application/octet-stream";
        long deadline = timeoutMs == 0 ? 0 : System.nanoTime() + timeoutMs * 1_000_000L;
        try (TikaInputStream in = TikaInputStream.get(file)) {
            StringWriter writer = new StringWriter();
            WriteOutContentHandler limited = new WriteOutContentHandler(writer, maxChars);
            ContentHandler handler = new DeadlineHandler(new BodyContentHandler(limited), deadline);
            Metadata metadata = new Metadata();
            if (ext != null) {
                metadata.set(EXT_KEY, ext);
            }
            boolean truncated = false;
            try {
                parser.parse(in, handler, metadata, newContext()); // [Thay đổi] C3: ParseContext cấu hình SAX cho docx/pptx
            } catch (SAXException e) {
                // Chạm giới hạn ký tự không phải lỗi: giữ phần đã đọc, đánh dấu bị cắt (giống FileReaderService)
                // [Thay đổi] Tika 3.x: hàm kiểm tra là static của WriteLimitReachedException (bản 2.x nằm ở WriteOutContentHandler)
                if (WriteLimitReachedException.isWriteLimitReached(e)) {
                    truncated = true;
                } else {
                    throw e;
                }
            }
            String detected = metadata.get(Metadata.CONTENT_TYPE);
            if (detected != null) {
                mime = detected;
            }
            // [Thay đổi] C1: KHÔNG chuẩn hoá NFC ở đây nữa — FileReaderService (C#) đã chuẩn hoá NFC cho mọi định dạng; làm 2 lần tốn
            // ~6 % thời gian trích mà text cuối giống hệt (đã kiểm). DLP_TIKA_EXP_NFC=1 bật lại để đo so sánh / để ExtractCli tự chấm.
            String text = useNfc ? Normalizer.normalize(writer.toString(), Normalizer.Form.NFC) : writer.toString();
            if (text.isBlank()) {
                // Tách "rỗng" khỏi "ok": Tika Server trả HTTP 200 kèm nội dung trống nên dễ bị coi nhầm là file sạch
                return new Result(Status.EMPTY, "", false, mime, "không có text");
            }
            return new Result(Status.OK, text, truncated, mime, null);
        } catch (EncryptedDocumentException e) {
            return new Result(Status.ENCRYPTED, "", false, mime, "file có mật khẩu");
        } catch (ZeroByteFileException e) {
            return new Result(Status.EMPTY, "", false, mime, "file rỗng");
        } catch (IOException | SAXException | org.apache.tika.exception.TikaException e) {
            // [Thay đổi] Bước 2.6: quá hạn (kể cả khi bị bọc trong TikaException) là TIMEOUT, không phải file hỏng.
            // Không trả text dở dang: kết luận dựa trên nửa file dễ sai hơn là báo "chưa đọc xong" để người dùng xử lý.
            if (causedByDeadline(e)) {
                return new Result(Status.TIMEOUT, "", false, mime, "quá hạn " + timeoutMs + " ms");
            }
            // Chỉ ghi TÊN loại lỗi, không ghi e.getMessage() (có thể chứa trích đoạn nội dung file)
            // [Thay đổi] Bước 2.3: chế độ gỡ lỗi như nhánh INTERNAL bên dưới (chỉ khi đặt DLP_TIKA_DEBUG)
            if (System.getenv("DLP_TIKA_DEBUG") != null) {
                e.printStackTrace();
            }
            return new Result(Status.CORRUPT, "", false, mime, e.getClass().getSimpleName());
        } catch (Throwable e) {
            // Kể cả lỗi hết bộ nhớ/tràn stack: không được để thoát ra ngoài (sau này là ranh giới native)
            // [Thay đổi] Bước 2.3: chế độ gỡ lỗi (CHỈ khi đặt biến môi trường DLP_TIKA_DEBUG) in stack trace ra stderr để tìm
            // lớp/tài nguyên còn thiếu khi build native. Mặc định tắt: stack trace/thông báo lỗi có thể chứa trích đoạn nội dung.
            if (System.getenv("DLP_TIKA_DEBUG") != null) {
                e.printStackTrace();
            }
            return new Result(Status.INTERNAL, "", false, mime, e.getClass().getSimpleName());
        }
    }
}
