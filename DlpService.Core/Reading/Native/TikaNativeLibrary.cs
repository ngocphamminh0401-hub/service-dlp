// [Thay đổi] File mới (tích hợp Tika native): bọc tika_native.dll (GraalVM native-image) bằng P/Invoke qua con trỏ hàm.
// Mô hình học từ CsNgdpCc.dll của CrowdStrike: kiểm tra toàn vẹn DLL trước khi nạp, tạo isolate 1 lần, MỖI LẦN GỌI gắn luồng
// hiện tại vào isolate (graal_attach_thread) rồi tháo ra (graal_detach_thread) — nhờ vậy gọi được từ mọi luồng ThreadPool.
// Giao diện hàm C xem tools/tika-native/src/main/java/dlp/tika/NativeApi.java (ABI 2).
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DlpService.Reading.Native;

internal sealed class TikaNativeLibrary : IDisposable
{
    /// <summary>Phiên bản giao diện hàm mà code này hiểu; DLL báo khác thì từ chối.</summary>
    private const int ExpectedAbi = 2;

    // [Thay đổi] Sửa lỗi "cắt âm thầm": Tika được phép đọc DƯ khoảng này so với MaxChars. Trước đây Tika tự dừng đúng ở MaxChars
    // (= giới hạn của FileReaderService) nên C# không biết phần sau đã bị bỏ → không có cảnh báo "bị cắt", file bị coi là đã quét trọn.
    // Đọc dư → text vượt giới hạn → FileReaderService tự cắt + cảnh báo như mọi định dạng khác. Dư 4.096 (không phải 1) vì Java
    // chuẩn hoá NFC sau khi cắt, có thể làm text ngắn đi vài ký tự (tiếng Việt dạng tổ hợp).
    internal const int TruncationMargin = 4_096;

    // Chữ ký khớp tika_native.h do native-image sinh ra
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateIsolateFn(IntPtr @params, out IntPtr isolate, out IntPtr thread);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AttachFn(IntPtr isolate, out IntPtr thread);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ThreadFn(IntPtr thread);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn(IntPtr thread, int maxChars, int timeoutMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DetectFn(IntPtr thread, byte[] path, byte[] outBuf, int cap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long OpenFn(IntPtr thread, byte[] path, out int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadFn(IntPtr thread, long handle, byte[] buf, int cap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseFn(IntPtr thread, long handle);

    private readonly CreateIsolateFn _create;
    private readonly AttachFn _attach;
    private readonly ThreadFn _detach, _teardown, _abi, _shutdown;
    private readonly InitFn _init;
    private readonly DetectFn _detect;
    private readonly OpenFn _open;
    private readonly ReadFn _read;
    private readonly CloseFn _close;
    // [Thay đổi] Tối ưu 1a: nhiều isolate (heap + GC riêng); mỗi lần gọi chọn 1 isolate theo vòng tròn
    private readonly IntPtr[] _isolates;
    private int _next = -1;

    /// <summary>[Thay đổi] Số isolate đang dùng — để ghi log.</summary>
    public int IsolateCount => _isolates.Length;

    // Các lần gọi giữ khoá đọc; Dispose giữ khoá ghi → không huỷ isolate khi còn lần gọi đang chạy dở (sẽ làm sập tiến trình)
    private readonly ReaderWriterLockSlim _gate = new();
    private bool _disposed;

    /// <summary>SHA-256 (hex thường) của file DLL đã nạp — để ghi log.</summary>
    public string Sha256 { get; }

    private TikaNativeLibrary(string dllPath, string sha256, TikaNativeOptions options)
    {
        Sha256 = sha256;
        IntPtr lib = NativeLibrary.Load(dllPath);
        T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
        _create = Fn<CreateIsolateFn>("graal_create_isolate");
        _attach = Fn<AttachFn>("graal_attach_thread");
        _detach = Fn<ThreadFn>("graal_detach_thread");
        _teardown = Fn<ThreadFn>("graal_tear_down_isolate");
        _abi = Fn<ThreadFn>("tika_abi_version");
        _init = Fn<InitFn>("tika_init");
        _detect = Fn<DetectFn>("tika_detect");
        _open = Fn<OpenFn>("tika_open");
        _read = Fn<ReadFn>("tika_read");
        _close = Fn<CloseFn>("tika_close");
        _shutdown = Fn<ThreadFn>("tika_shutdown");

        // [Thay đổi] Tối ưu 1a: tạo K isolate (giới hạn 1–8), mỗi isolate khởi tạo parser riêng. Lỗi giữa chừng → huỷ các isolate đã tạo.
        int count = Math.Clamp(options.Isolates, 1, 8);
        _isolates = new IntPtr[count];
        int created = 0;
        try
        {
            for (; created < count; created++)
            {
                _isolates[created] = CreateIsolate(options);
            }
        }
        catch
        {
            for (int i = 0; i < created; i++)
            {
                if (_attach(_isolates[i], out IntPtr t) == 0)
                {
                    _teardown(t);
                }
            }
            throw;
        }
    }

    // [Thay đổi] Tách từ constructor cũ (logic giữ nguyên): tạo 1 isolate, kiểm tra ABI, tika_init, rồi tháo luồng tạo ra
    private IntPtr CreateIsolate(TikaNativeOptions options)
    {
        if (_create(IntPtr.Zero, out IntPtr isolate, out IntPtr creator) != 0)
        {
            throw new TikaNativeException("graal_create_isolate thất bại");
        }
        try
        {
            if (_abi(creator) != ExpectedAbi)
            {
                throw new TikaNativeException($"Phiên bản giao diện DLL không khớp (mong {ExpectedAbi})");
            }
            // [Thay đổi] Sửa lỗi "cắt âm thầm": cộng TruncationMargin; MaxChars <= 0 nghĩa là "dùng mặc định của DLL" thì giữ nguyên
            int maxChars = options.MaxChars > 0 ? options.MaxChars + TruncationMargin : options.MaxChars;
            if (_init(creator, maxChars, options.TimeoutMs) != 0)
            {
                throw new TikaNativeException("tika_init thất bại");
            }
        }
        catch
        {
            _teardown(creator); // khởi tạo dở thì huỷ isolate luôn, không để rò rỉ
            throw;
        }
        // Luồng tạo isolate cũng chỉ là 1 luồng như các luồng khác: tháo ra để mọi lần gọi sau đi cùng 1 đường (attach → gọi → detach).
        // Isolate vẫn sống khi không còn luồng nào gắn vào.
        _detach(creator);
        return isolate;
    }

    /// <summary>Nạp DLL sau khi kiểm tra mã băm. Ném exception nếu không nạp được (người gọi bắt và ghi log).</summary>
    public static TikaNativeLibrary Load(TikaNativeOptions options, string baseDirectory)
    {
        string path = Path.GetFullPath(Path.IsPathRooted(options.DllPath) ? options.DllPath : Path.Combine(baseDirectory, options.DllPath));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Không thấy tika_native.dll", path);
        }

        bool pinned = !string.IsNullOrWhiteSpace(options.Sha256);
        if (!pinned && options.RequireSha256)
        {
            throw new InvalidOperationException("Chưa ghim Sha256 cho DLL (RequireSha256 = true) nên không nạp");
        }

        string actual;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        if (pinned && !string.Equals(actual, options.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Mã băm DLL không khớp mã đã ghim (thực tế {actual}) nên không nạp");
        }
        // Ghi chú: giữa lúc băm và lúc nạp vẫn có 1 khe hở nhỏ nếu kẻ tấn công ghi được vào thư mục chứa DLL;
        // thư mục này cần được bảo vệ quyền ghi (chỉ Administrator/SYSTEM).
        return new TikaNativeLibrary(path, actual, options);
    }

    private static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    // Gắn luồng hiện tại vào isolate, chạy, rồi tháo ra
    private T WithThread<T>(Func<IntPtr, T> action)
    {
        _gate.EnterReadLock();
        try
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TikaNativeLibrary));
            }
            // [Thay đổi] Tối ưu 1a: chọn isolate theo vòng tròn (uint để không âm khi bộ đếm tràn)
            IntPtr isolate = _isolates[(uint)Interlocked.Increment(ref _next) % (uint)_isolates.Length];
            if (_attach(isolate, out IntPtr thread) != 0)
            {
                throw new TikaNativeException("graal_attach_thread thất bại");
            }
            try
            {
                return action(thread);
            }
            finally
            {
                _detach(thread);
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>Trích text. Trả (mã trạng thái, text): 0 ok, 2 không hỗ trợ, 3 hỏng, 4 có mật khẩu, 5 rỗng, 6 lỗi nội bộ/hết bộ nhớ, 7 quá hạn.</summary>
    public (int Status, string Text) Extract(string path) => WithThread(thread =>
    {
        long handle = _open(thread, Utf8Z(path), out int status);
        if (handle == 0)
        {
            return (status, "");
        }
        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[64 * 1024];
            int n;
            while ((n = _read(thread, handle, buf, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
            }
            return n < 0 ? (6, "") : (0, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
        }
        finally
        {
            _close(thread, handle);
        }
    });

    /// <summary>Nhận dạng loại file (MIME) bằng nội dung. Chưa dùng trong luồng chính; để dành cho bước nhận dạng đổi đuôi.</summary>
    public string Detect(string path) => WithThread(thread =>
    {
        var buf = new byte[256];
        int n = _detect(thread, Utf8Z(path), buf, buf.Length);
        return n >= 0 ? Encoding.UTF8.GetString(buf, 0, n) : "application/octet-stream";
    });

    public void Dispose()
    {
        _gate.EnterWriteLock();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (IntPtr isolate in _isolates) // [Thay đổi] Tối ưu 1a: huỷ từng isolate
            {
                if (_attach(isolate, out IntPtr thread) == 0)
                {
                    _shutdown(thread);   // huỷ parser, đóng kết quả còn dở
                    _teardown(thread);   // huỷ isolate (hàm này cũng tháo luồng)
                }
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }
}
