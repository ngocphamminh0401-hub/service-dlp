// [Thay đổi] File mới (checklist "chạy như Windows Service thật" - đổi hướng logging): xem FileLogger.cs.
// [Thay đổi] Ghi log qua hàng đợi + 1 luồng nền: đo service thật (buổi 12, 25.486 file) cho thấy cách cũ — MỖI dòng log
// lấy khoá chung rồi mở/ghi/đóng file — chỉ đạt ~1.070 dòng/giây; các luồng xử lý file phải xếp hàng chờ khoá log (bước
// "phân loại" mất TB 240 ms/file, thực chất là chờ ghi log) → thông lượng cả service bị giới hạn ở ~75 file/giây.
// Giờ FileLogger chỉ đưa dòng vào hàng đợi; luồng nền giữ file MỞ SẴN, ghi theo lô và flush sau mỗi lô.
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace DlpService.Logging;

/// <summary>Tạo 1 FileLogger cho mỗi category (VD "DlpService.Worker") — tất cả cùng ghi vào chung 1 file log theo ngày.
/// Tự tách file khi vượt MaxFileSizeBytes và tự xoá log cũ hơn RetentionDays ngày.</summary>
public class FileLoggerProvider : ILoggerProvider
{
    // [Thay đổi] Log rotation: file log 57MB làm Notepad mở rất chậm -> mỗi file tối đa 5MB, đầy thì sang phần mới
    private const long DefaultMaxFileSizeBytes = 5 * 1024 * 1024;

    // [Thay đổi] Log rotation: chỉ giữ log của 7 ngày gần nhất (tính cả hôm nay), cũ hơn thì xoá
    private const int RetentionDays = 7;

    private const string FilePrefix = "dlpservice-";
    private const string DateFormat = "yyyy-MM-dd";

    // [Thay đổi] Hàng đợi: tối đa bấy nhiêu dòng chờ ghi. Đầy thì luồng gọi Log phải CHỜ (không bỏ dòng, không phình RAM
    // vô hạn) — chỉ xảy ra khi đĩa chậm bất thường.
    private const int QueueCapacity = 50_000;

    // [Thay đổi] Số lần thử + thời gian chờ khi mở/ghi file lỗi (file bị khoá tạm thời) — giữ như logger cũ
    private const int MaxWriteAttempts = 3;
    private const int RetryDelayMs = 50;

    private readonly string _logFolder;
    private readonly long _maxFileSizeBytes;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    // [Thay đổi] Hàng đợi dòng log + luồng nền duy nhất ghi ra file
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), QueueCapacity);
    private readonly Thread _writerThread;
    private int _disposed; // 0/1, dùng Interlocked để Dispose chỉ chạy 1 lần

    // [Thay đổi] Trạng thái file đang ghi — CHỈ luồng nền đọc/ghi các biến này (không cần khoá)
    private DateTime _currentDate = DateTime.MinValue; // ngày của file đang ghi; MinValue = chưa khởi tạo
    private int _currentPart;                          // 0 = "dlpservice-<ngày>.log", 1 = ".1.log", 2 = ".2.log"...
    private StreamWriter? _writer;                     // file đang mở; null = chưa mở / vừa đóng do lỗi

    /// <summary>Tạo provider; log sẽ ghi vào file trong logFolder (tự tạo thư mục nếu chưa có).</summary>
    /// <param name="logFolder">Thư mục chứa file log, ví dụ "&lt;thư mục chạy&gt;/logs".</param>
    /// <param name="maxFileSizeBytes">[Thay đổi] Kích thước tối đa 1 phần log (mặc định 5 MB; test truyền nhỏ để thử xoay vòng).</param>
    public FileLoggerProvider(string logFolder, long maxFileSizeBytes = DefaultMaxFileSizeBytes)
    {
        _logFolder = logFolder;
        _maxFileSizeBytes = maxFileSizeBytes;
        Directory.CreateDirectory(_logFolder);

        // IsBackground: luồng này không giữ tiến trình sống khi service dừng (Dispose/ProcessExit lo xả hàng đợi)
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "DlpFileLogWriter" };
        _writerThread.Start();

        // [Thay đổi] Host .NET KHÔNG gọi Dispose cho provider thêm bằng AddProvider(instance) → tự xả hàng đợi khi tiến trình
        // thoát bình thường (service dừng), để không mất các dòng log cuối (VD "Service stopped").
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
    {
        // [Thay đổi] Log rotation: truyền chính provider để logger đưa dòng vào hàng đợi chung
        return _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));
    }

    // [Thay đổi] Gọi từ FileLogger.Log: chỉ đưa dòng vào hàng đợi (nhanh, không đụng tới đĩa). Hàng đợi đầy → chờ. Provider đã
    // dừng (đang tắt service) → bỏ dòng, không ném lỗi: logger không bao giờ được làm sập service.
    internal void Enqueue(string line)
    {
        try
        {
            _queue.Add(line);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding đã được gọi (service đang dừng)
        }
    }

    // [Thay đổi] Luồng nền: lấy 1 dòng (chờ nếu rỗng), rồi lấy thêm mọi dòng đang có sẵn → ghi cả lô → flush 1 lần.
    // Flush sau mỗi lô để người/tool đọc log thấy dòng mới gần như ngay lập tức.
    private void WriterLoop()
    {
        foreach (string first in _queue.GetConsumingEnumerable())
        {
            WriteLine(first);
            while (_queue.TryTake(out string? next))
            {
                WriteLine(next);
            }
            Flush();
        }
        CloseWriter();
    }

    // Ghi 1 dòng vào file hiện tại (mở/xoay vòng nếu cần). Lỗi → thử lại tối đa MaxWriteAttempts lần rồi BỎ dòng đó.
    private void WriteLine(string line)
    {
        for (int attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            try
            {
                EnsureWriter();
                _writer!.WriteLine(line);
                return;
            }
            catch (Exception) when (attempt < MaxWriteAttempts)
            {
                // File bị khoá tạm thời / bị xoá giữa chừng → đóng để lần thử sau mở lại
                CloseWriter();
                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception)
            {
                // Lần thử cuối vẫn lỗi (hết quyền, hết dung lượng...) → bỏ dòng này, không log lại lỗi (logger đang hỏng)
                CloseWriter();
                return;
            }
        }
    }

    private void Flush()
    {
        try
        {
            _writer?.Flush();
        }
        catch (Exception)
        {
            CloseWriter();
        }
    }

    // [Thay đổi] Log rotation: đảm bảo đang mở ĐÚNG file. Sang ngày mới → tìm phần lớn nhất đã có của hôm nay (restart service
    // vẫn ghi tiếp đúng phần) + dọn log cũ. File hiện tại >= MaxFileSize → sang phần mới. Kích thước đọc từ stream đang mở
    // (rẻ), không gọi FileInfo mỗi dòng như trước.
    private void EnsureWriter()
    {
        DateTime today = DateTime.Now.Date;
        if (today != _currentDate)
        {
            CloseWriter();
            _currentDate = today;
            _currentPart = FindLatestPart(today);
            DeleteOldLogs(today);
        }

        if (_writer != null && _writer.BaseStream.Length >= _maxFileSizeBytes)
        {
            CloseWriter();
            _currentPart++;
        }

        while (_writer == null)
        {
            string path = BuildPath(today, _currentPart);
            // FileShare.ReadWrite | Delete: Notepad / tool đo đọc được file đang ghi (giữ như logger cũ)
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length >= _maxFileSizeBytes)
            {
                // Phần này đã đầy từ trước (VD restart service khi file vừa chạm 5 MB) → sang phần kế tiếp
                stream.Dispose();
                _currentPart++;
                continue;
            }
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception)
        {
            // Đóng lỗi (đĩa đầy khi flush nốt) — bỏ qua
        }
        _writer = null;
    }

    // Tên file theo ngày + số phần: phần 0 giữ nguyên tên cũ "dlpservice-2026-09-25.log" để tương thích file đã có
    private string BuildPath(DateTime date, int part)
    {
        string suffix = part == 0 ? "" : "." + part;
        return Path.Combine(_logFolder, $"{FilePrefix}{date.ToString(DateFormat, CultureInfo.InvariantCulture)}{suffix}.log");
    }

    // [Thay đổi] Log rotation: tìm số phần lớn nhất của 1 ngày đang có trong thư mục ("...25.log" = 0, "...25.3.log" = 3)
    private int FindLatestPart(DateTime date)
    {
        string datePart = date.ToString(DateFormat, CultureInfo.InvariantCulture);
        int latest = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_logFolder, $"{FilePrefix}{datePart}*.log"))
            {
                // Bỏ tiền tố "dlpservice-<ngày>" và đuôi ".log", phần còn lại là "" (phần 0) hoặc ".3" (phần 3)
                string middle = Path.GetFileNameWithoutExtension(file)[(FilePrefix.Length + datePart.Length)..];
                if (middle.StartsWith('.') && int.TryParse(middle[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int part))
                {
                    latest = Math.Max(latest, part);
                }
            }
        }
        catch (Exception)
        {
            // Lỗi khi dò thư mục log (mất quyền, thư mục bị xoá...) không được làm hỏng việc ghi log → bắt đầu từ phần 0
        }
        return latest;
    }

    // [Thay đổi] Log rotation: xoá file log có ngày (đọc từ TÊN file, không dùng ngày sửa file) cũ hơn RetentionDays.
    // Mỗi file bọc try/catch riêng: 1 file đang bị Notepad mở không xoá được thì bỏ qua, lần sang ngày sau thử lại.
    private void DeleteOldLogs(DateTime today)
    {
        DateTime oldestKept = today.AddDays(-(RetentionDays - 1)); // 7 ngày = hôm nay + 6 ngày trước

        try
        {
            foreach (var file in Directory.EnumerateFiles(_logFolder, $"{FilePrefix}*.log"))
            {
                string name = Path.GetFileName(file);
                if (name.Length < FilePrefix.Length + DateFormat.Length)
                {
                    continue;
                }

                string datePart = name.Substring(FilePrefix.Length, DateFormat.Length);
                if (!DateTime.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fileDate))
                {
                    continue; // không đúng mẫu tên của service -> không đụng vào
                }

                if (fileDate < oldestKept)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception)
                    {
                        // File đang bị khoá/mất quyền — bỏ qua, không làm hỏng việc ghi log
                    }
                }
            }
        }
        catch (Exception)
        {
            // Lỗi liệt kê thư mục — bỏ qua, lần sang ngày sau thử lại
        }
    }

    /// <inheritdoc/>
    /// <remarks>[Thay đổi] Ngừng nhận dòng mới, chờ luồng nền ghi hết hàng đợi (tối đa 5 giây) rồi đóng file.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _queue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(5));
        _loggers.Clear();
        GC.SuppressFinalize(this);
    }
}
