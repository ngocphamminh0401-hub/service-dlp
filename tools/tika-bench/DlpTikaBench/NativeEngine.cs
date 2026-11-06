// [Thay đổi] File mới (Bước 2.5): bộ gọi tika_native.dll từ C# (P/Invoke qua con trỏ hàm), mô phỏng "lớp 1" điều phối của
// CsNgdpCc.dll: kiểm tra toàn vẹn DLL trước khi nạp, tạo isolate, mỗi luồng phải attach, đọc text theo từng khối.
// Dùng con trỏ hàm + delegate (không cần bật unsafe). Chỉ dùng trong công cụ benchmark, chưa thuộc DlpService.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DlpTikaBench;

internal sealed class NativeEngine : IDisposable
{
    // Chữ ký khớp tika_native.h do native-image sinh ra
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateIsolateFn(IntPtr @params, out IntPtr isolate, out IntPtr thread);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AttachFn(IntPtr isolate, out IntPtr thread);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ThreadFn(IntPtr thread);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn(IntPtr thread, int maxChars, int timeoutMs); // [Thay đổi] ABI 2
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DetectFn(IntPtr thread, byte[] path, byte[] outBuf, int cap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long OpenFn(IntPtr thread, byte[] path, out int status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadFn(IntPtr thread, long handle, byte[] buf, int cap);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseFn(IntPtr thread, long handle);

    private readonly IntPtr _lib;
    private readonly CreateIsolateFn _create;
    private readonly AttachFn _attach;
    private readonly ThreadFn _detach, _teardown, _gc, _abi, _shutdown; // [Thay đổi] + _shutdown (huỷ parser)
    private readonly InitFn _init;
    private readonly DetectFn _detect;
    private readonly OpenFn _open;
    private readonly ReadFn _read;
    private readonly CloseFn _close;
    private IntPtr _isolate;
    private IntPtr _mainThread;

    public string Sha256 { get; }

    /// <param name="dllPath">Đường dẫn ĐẦY ĐỦ tới tika_native.dll (các DLL phụ của JDK nằm cùng thư mục).</param>
    /// <param name="expectedSha256">Nếu có: mã băm đã ghim; khác thì từ chối nạp (chống DLL bị thay).</param>
    public NativeEngine(string dllPath, string? expectedSha256 = null, int maxChars = 0, int timeoutMs = 30_000)
    {
        dllPath = Path.GetFullPath(dllPath);
        using (var fs = File.OpenRead(dllPath))
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        if (expectedSha256 != null && !string.Equals(expectedSha256, Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Mã băm DLL không khớp (tính ra {Sha256}); từ chối nạp.");
        }

        _lib = NativeLibrary.Load(dllPath);
        T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));
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
        _gc = Fn<ThreadFn>("tika_gc");
        _shutdown = Fn<ThreadFn>("tika_shutdown");

        if (_create(IntPtr.Zero, out _isolate, out _mainThread) != 0)
        {
            throw new InvalidOperationException("graal_create_isolate thất bại.");
        }
        if (_abi(_mainThread) != 2)
        {
            throw new InvalidOperationException("Phiên bản giao diện DLL không khớp (mong 2).");
        }
        if (_init(_mainThread, maxChars, timeoutMs) != 0)
        {
            throw new InvalidOperationException("tika_init thất bại.");
        }
    }

    /// <summary>Luồng tạo isolate dùng thẳng được; luồng khác phải gọi Attach trước và Detach sau.</summary>
    public IntPtr MainThread => _mainThread;

    public IntPtr Attach()
    {
        if (_attach(_isolate, out var t) != 0) throw new InvalidOperationException("graal_attach_thread thất bại.");
        return t;
    }

    public void Detach(IntPtr thread) => _detach(thread);

    public void CollectGarbage(IntPtr thread) => _gc(thread);

    public string Detect(IntPtr thread, string path)
    {
        var buf = new byte[256];
        int n = _detect(thread, Utf8Z(path), buf, buf.Length);
        return n >= 0 ? Encoding.UTF8.GetString(buf, 0, n) : $"(lỗi {n})";
    }

    /// <summary>Trích text. status: 0 ok, 5 rỗng, còn lại là lỗi (xem TikaExtractor.Status). Đọc theo khối 64 KB.</summary>
    public (int Status, string Text) Extract(IntPtr thread, string path)
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
            if (n < 0)
            {
                return (6, "");
            }
            return (0, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
        }
        finally
        {
            _close(thread, handle);
        }
    }

    private static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>[Thay đổi] Huỷ parser và đóng mọi kết quả còn dở; trả số kết quả còn dở bị đóng hộ (>= 0) hoặc -1 nếu lỗi.</summary>
    public int Shutdown(IntPtr thread) => _shutdown(thread);

    public void Dispose()
    {
        if (_mainThread != IntPtr.Zero)
        {
            _shutdown(_mainThread); // huỷ parser trước khi huỷ isolate
            _teardown(_mainThread);
            _mainThread = IntPtr.Zero;
        }
    }
}
