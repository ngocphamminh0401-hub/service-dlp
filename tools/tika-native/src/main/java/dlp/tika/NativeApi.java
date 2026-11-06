// [Thay đổi] File mới (Bước 2.5): lớp vỏ native — các hàm C mà .NET gọi qua P/Invoke khi build thành DLL (native-image --shared).
// Mô hình lấy từ CsNgdpCc.dll của CrowdStrike: chu trình tạo/huỷ isolate (graal_* do GraalVM tự sinh) + các hàm Tika_* và
// đọc text theo từng khối (giống MappedBufferStream<TIKA_STATUS>::NextChunk).
//
// Quy ước chung:
//  - Đường dẫn file truyền vào là chuỗi UTF-8 kết thúc bằng NUL.
//  - Hàm trả int: >= 0 là thành công (giá trị tuỳ hàm), < 0 là lỗi. KHÔNG hàm nào để exception thoát ra ngoài.
//  - Mã trạng thái của tika_open khớp TikaExtractor.Status (0 ok, 2 không hỗ trợ, 3 hỏng, 4 có mật khẩu, 5 rỗng, 6 lỗi nội bộ gồm hết bộ nhớ, 7 quá hạn).
//  - Mỗi luồng .NET gọi vào phải đã graal_attach_thread (hoặc là luồng tạo ra isolate).
package dlp.tika;

import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicLong;

import org.graalvm.nativeimage.IsolateThread;
import org.graalvm.nativeimage.c.function.CEntryPoint;
import org.graalvm.nativeimage.c.type.CCharPointer;
import org.graalvm.nativeimage.c.type.CIntPointer;
import org.graalvm.nativeimage.c.type.CTypeConversion;

public final class NativeApi {

    /** Tăng khi đổi giao diện hàm, để phía .NET kiểm tra đang nạp đúng phiên bản DLL. */
    // [Thay đổi] Bước 2.6: ABI 2 — tika_init nhận (maxChars, timeoutMs), thêm tika_shutdown, thêm mã trạng thái 7 (TIMEOUT)
    private static final int ABI_VERSION = 2;

    private static volatile TikaExtractor extractor;
    private static final Object INIT_LOCK = new Object();

    /** Kết quả 1 lần trích đang chờ .NET đọc dần theo từng khối. */
    private static final class Session {
        final byte[] data;   // text UTF-8
        int pos;             // vị trí đã đọc tới
        Session(byte[] data) { this.data = data; }
    }

    private static final ConcurrentHashMap<Long, Session> SESSIONS = new ConcurrentHashMap<>();
    private static final AtomicLong NEXT_HANDLE = new AtomicLong(1);

    private NativeApi() { }

    /** Phiên bản giao diện hàm. */
    @CEntryPoint(name = "tika_abi_version")
    public static int abiVersion(IsolateThread thread) {
        return ABI_VERSION;
    }

    /**
     * Khởi tạo parser dùng chung (gọi 1 lần sau khi tạo isolate). Trả 0 nếu ok.
     * [Thay đổi] Bước 2.6: nhận cấu hình — maxChars (<= 0: mặc định 2.000.000) và timeoutMs mỗi file (<= 0: không giới hạn).
     * Gọi lại khi đã khởi tạo thì giữ cấu hình cũ (phải tika_shutdown trước nếu muốn đổi) — giống quy tắc "không đổi cấu hình
     * sau khi commit" thấy trong CsNgdpCc.dll.
     */
    @CEntryPoint(name = "tika_init")
    public static int init(IsolateThread thread, int maxChars, int timeoutMs) {
        try {
            if (extractor == null) {
                synchronized (INIT_LOCK) {
                    if (extractor == null) {
                        extractor = new TikaExtractor(maxChars, timeoutMs);
                    }
                }
            }
            return 0;
        } catch (Throwable e) {
            return -1;
        }
    }

    /**
     * [Thay đổi] Bước 2.6: huỷ parser (tương ứng Tika_DestroyParser của CrowdStrike): đóng mọi kết quả còn dở, bỏ tham chiếu
     * tới parser để bộ thu gom giải phóng được, rồi gọi thu gom rác. Sau đó có thể tika_init lại với cấu hình khác.
     * Việc huỷ isolate vẫn do graal_tear_down_isolate lo. Trả số kết quả còn dở bị đóng hộ (>= 0), hoặc -1 nếu lỗi.
     */
    @CEntryPoint(name = "tika_shutdown")
    public static int shutdown(IsolateThread thread) {
        try {
            // [Thay đổi] Thí nghiệm H6 (Serial GC): nếu đặt DLP_TIKA_EXP_GCSTATS=1, in số lần + tổng thời gian GC của isolate ra stderr
            // TRƯỚC khi dọn (để không tính lần System.gc() bên dưới). Mặc định tắt → hành vi không đổi.
            if ("1".equals(System.getenv("DLP_TIKA_EXP_GCSTATS"))) {
                printGcStats();
            }
            int left = SESSIONS.size();
            SESSIONS.clear();
            synchronized (INIT_LOCK) {
                extractor = null;
            }
            System.gc();
            return left;
        } catch (Throwable e) {
            return -1;
        }
    }

    /** [Thay đổi] Thí nghiệm H6: in thống kê GC (chỉ số đếm, không có nội dung file). Bản native không hỗ trợ thì in lý do. */
    private static void printGcStats() {
        try {
            for (java.lang.management.GarbageCollectorMXBean b : java.lang.management.ManagementFactory.getGarbageCollectorMXBeans()) {
                System.err.println("[GCSTATS] " + b.getName() + " count=" + b.getCollectionCount() + " timeMs=" + b.getCollectionTime());
            }
            Runtime rt = Runtime.getRuntime();
            System.err.println("[GCSTATS] heapUsedMB=" + (rt.totalMemory() - rt.freeMemory()) / 1048576 + " heapTotalMB=" + rt.totalMemory() / 1048576
                    + " heapMaxMB=" + rt.maxMemory() / 1048576);
        } catch (Throwable e) {
            System.err.println("[GCSTATS] không lấy được: " + e.getClass().getSimpleName());
        }
    }

    /**
     * Nhận dạng loại file bằng nội dung. Ghi MIME (UTF-8, kết thúc NUL) vào out; cap là kích thước bộ đệm.
     * Trả độ dài chuỗi (không tính NUL), hoặc -1 lỗi, -2 bộ đệm quá nhỏ.
     */
    @CEntryPoint(name = "tika_detect")
    public static int detect(IsolateThread thread, CCharPointer path, CCharPointer out, int cap) {
        try {
            TikaExtractor ex = extractor;
            if (ex == null) {
                return -1;
            }
            byte[] mime = ex.detect(Path.of(CTypeConversion.toJavaString(path))).getBytes(StandardCharsets.UTF_8);
            if (mime.length + 1 > cap) {
                return -2;
            }
            for (int i = 0; i < mime.length; i++) {
                out.write(i, mime[i]);
            }
            out.write(mime.length, (byte) 0);
            return mime.length;
        } catch (Throwable e) {
            return -1;
        }
    }

    /**
     * Trích text của file và giữ kết quả trong bộ nhớ để đọc dần bằng tika_read.
     * Ghi mã trạng thái vào *status. Trả handle (> 0) nếu có text; trả 0 nếu không (xem status).
     */
    @CEntryPoint(name = "tika_open")
    public static long open(IsolateThread thread, CCharPointer path, CIntPointer status) {
        try {
            TikaExtractor ex = extractor;
            if (ex == null) {
                status.write(TikaExtractor.Status.INTERNAL.code);
                return 0;
            }
            TikaExtractor.Result r = ex.extract(Path.of(CTypeConversion.toJavaString(path)));
            status.write(r.status().code);
            if (r.status() != TikaExtractor.Status.OK) {
                return 0;
            }
            long handle = NEXT_HANDLE.getAndIncrement();
            SESSIONS.put(handle, new Session(r.text().getBytes(StandardCharsets.UTF_8)));
            return handle;
        } catch (Throwable e) {
            status.write(TikaExtractor.Status.INTERNAL.code);
            return 0;
        }
    }

    /**
     * Chép khối tiếp theo (tối đa cap byte UTF-8) vào buf. Không cắt giữa một ký tự nhiều byte.
     * Trả số byte đã chép; 0 = hết dữ liệu; -1 = handle không hợp lệ hoặc lỗi.
     */
    @CEntryPoint(name = "tika_read")
    public static int read(IsolateThread thread, long handle, CCharPointer buf, int cap) {
        try {
            Session s = SESSIONS.get(handle);
            if (s == null || cap <= 0) {
                return -1;
            }
            int remaining = s.data.length - s.pos;
            if (remaining <= 0) {
                return 0;
            }
            int n = Math.min(cap, remaining);
            // Lùi lại nếu điểm cắt rơi vào giữa 1 ký tự UTF-8 (byte tiếp nối có dạng 10xxxxxx)
            if (n < remaining) {
                while (n > 0 && (s.data[s.pos + n] & 0xC0) == 0x80) {
                    n--;
                }
                if (n == 0) {
                    return -1; // bộ đệm nhỏ hơn 1 ký tự
                }
            }
            for (int i = 0; i < n; i++) {
                buf.write(i, s.data[s.pos + i]);
            }
            s.pos += n;
            return n;
        } catch (Throwable e) {
            return -1;
        }
    }

    /** Giải phóng kết quả của 1 lần tika_open. Trả 0 nếu ok. */
    @CEntryPoint(name = "tika_close")
    public static int close(IsolateThread thread, long handle) {
        try {
            return SESSIONS.remove(handle) != null ? 0 : -1;
        } catch (Throwable e) {
            return -1;
        }
    }

    /** Yêu cầu thu gom rác (giống Common_InvokeGarbageCollection của CrowdStrike); dùng sau file lớn để giảm RAM. */
    @CEntryPoint(name = "tika_gc")
    public static int gc(IsolateThread thread) {
        try {
            System.gc();
            return 0;
        } catch (Throwable e) {
            return -1;
        }
    }
}
