// [Thay đổi] File mới (H4 — chặn zip bomb theo ngân sách): cấu hình mục "Reading:Zip" trong appsettings.json. Thiếu mục nào → dùng mặc định
// dưới đây (chặt, tính theo MemoryMax=512M của systemd và MaxConcurrency=2). Giá trị sai (≤ 0, vô lý) → Validate() đưa về mặc định + cảnh báo.
namespace DlpService.Reading.Extractors;

/// <summary>Ngưỡng giải nén cho file Office dạng ZIP (.docx/.pptx/.xlsx). Đơn vị MB = 1024 × 1024 byte.</summary>
public sealed class ZipLimitOptions
{
    /// <summary>Số phần (entry) tối đa trong 1 file ZIP. Gói Office thật: vài chục – vài nghìn.</summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>Kích thước danh mục ZIP (central directory) tối đa — đọc trước khi .NET dựng object cho từng phần.</summary>
    public int MaxCentralDirectoryMB { get; set; } = 8;

    /// <summary>docx/pptx (nạp cả XML thành cây đối tượng — RAM ≈ 5–10 × XML): 1 phần nội dung tối đa.</summary>
    public int DomMaxEntryMB { get; set; } = 24;

    /// <summary>docx/pptx: tổng các phần nội dung tối đa.</summary>
    public int DomMaxTotalMB { get; set; } = 48;

    /// <summary>xlsx (ExcelDataReader đọc sheet theo từng hàng): 1 phần nội dung (sheet) tối đa.</summary>
    public int StreamingMaxEntryMB { get; set; } = 256;

    /// <summary>xlsx: sharedStrings.xml (nạp HẾT vào RAM, không đọc theo hàng) tối đa.</summary>
    public int StreamingMaxSharedStringsMB { get; set; } = 24;

    /// <summary>xlsx: tổng các phần nội dung tối đa.</summary>
    public int StreamingMaxTotalMB { get; set; } = 512;

    /// <summary>Tổng kích thước giải nén của MỌI phần (kể cả ảnh) tối đa — trần cuối cùng, bất kể phần đó là gì.</summary>
    public int MaxTotalAllEntriesMB { get; set; } = 1024;

    /// <summary>Tỉ lệ nén tối đa của 1 phần (giải nén / nén). XML thật ~10–30 ×; zip bomb (toàn số 0) &gt; 1000 ×.</summary>
    public double MaxCompressionRatio { get; set; } = 200;

    /// <summary>Chỉ xét tỉ lệ nén với phần giải nén lớn hơn mức này (file nhỏ nén cực tốt không bị bắt nhầm).</summary>
    public int RatioCheckMinMB { get; set; } = 1;

    /// <summary>Đưa giá trị sai về mặc định.</summary>
    /// <returns>Danh sách mô tả các giá trị đã sửa (rỗng nếu không sửa gì) — nơi gọi ghi cảnh báo.</returns>
    public List<string> Validate()
    {
        var d = new ZipLimitOptions();
        var fixedItems = new List<string>();
        void Fix<T>(string name, T value, bool ok, T def, Action<T> set)
        {
            if (!ok)
            {
                fixedItems.Add($"{name}={value} → {def}");
                set(def);
            }
        }

        Fix(nameof(MaxEntries), MaxEntries, MaxEntries > 0, d.MaxEntries, v => MaxEntries = v);
        Fix(nameof(MaxCentralDirectoryMB), MaxCentralDirectoryMB, MaxCentralDirectoryMB > 0, d.MaxCentralDirectoryMB, v => MaxCentralDirectoryMB = v);
        Fix(nameof(DomMaxEntryMB), DomMaxEntryMB, DomMaxEntryMB > 0, d.DomMaxEntryMB, v => DomMaxEntryMB = v);
        Fix(nameof(StreamingMaxEntryMB), StreamingMaxEntryMB, StreamingMaxEntryMB > 0, d.StreamingMaxEntryMB, v => StreamingMaxEntryMB = v);
        Fix(nameof(StreamingMaxSharedStringsMB), StreamingMaxSharedStringsMB, StreamingMaxSharedStringsMB > 0, d.StreamingMaxSharedStringsMB, v => StreamingMaxSharedStringsMB = v);
        // Tổng nhỏ hơn 1 phần là vô lý (không file nào lọt nổi phần lớn nhất được phép) → về mặc định
        Fix(nameof(DomMaxTotalMB), DomMaxTotalMB, DomMaxTotalMB >= DomMaxEntryMB, Math.Max(d.DomMaxTotalMB, DomMaxEntryMB), v => DomMaxTotalMB = v);
        Fix(nameof(StreamingMaxTotalMB), StreamingMaxTotalMB, StreamingMaxTotalMB >= StreamingMaxEntryMB, Math.Max(d.StreamingMaxTotalMB, StreamingMaxEntryMB), v => StreamingMaxTotalMB = v);
        Fix(nameof(MaxTotalAllEntriesMB), MaxTotalAllEntriesMB, MaxTotalAllEntriesMB > 0, d.MaxTotalAllEntriesMB, v => MaxTotalAllEntriesMB = v);
        Fix(nameof(MaxCompressionRatio), MaxCompressionRatio, MaxCompressionRatio > 1 && !double.IsNaN(MaxCompressionRatio), d.MaxCompressionRatio, v => MaxCompressionRatio = v);
        Fix(nameof(RatioCheckMinMB), RatioCheckMinMB, RatioCheckMinMB >= 0, d.RatioCheckMinMB, v => RatioCheckMinMB = v);
        return fixedItems;
    }
}
