// [Thay đổi] File mới (GĐ3): các tuỳ chọn theo dõi đọc từ mục "Watch" của appsettings.json. Mặc định = hành vi cũ của bản Windows
// (không loại thư mục, không đối soát định kỳ, không làm mới mẫu, không ghi metric) — trừ việc tự dựng lại watcher khi lỗi (R6) bật cho mọi nền tảng.
using DlpService.Platform;

namespace DlpService.Watching;

/// <summary>Tuỳ chọn theo dõi thư mục.</summary>
public sealed record WatchOptions
{
    /// <summary>Thư mục con bỏ qua khi theo dõi và khi quét ("Watch:SkipHidden", "Watch:ExcludeDirectoryNames").</summary>
    public WatchExclusion Exclusion { get; init; } = WatchExclusion.None;

    /// <summary>Chu kỳ quét đối soát toàn bộ thư mục ("Watch:ReconcileIntervalMinutes"); Zero = tắt.
    /// Lưới an toàn cho mọi sự kiện bị mất (tràn hàng đợi, hết watch, file đổi lúc service tắt).</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.Zero;

    /// <summary>Chu kỳ mở rộng lại các mẫu "Watch:Folders" (VD /home/*/Documents) để bắt người dùng mới ("Watch:PatternRefreshSeconds"); Zero = tắt.</summary>
    public TimeSpan PatternRefreshInterval { get; init; } = TimeSpan.Zero;

    /// <summary>Chu kỳ ghi 1 dòng metric ("Watch:MetricsIntervalSeconds"); Zero = tắt.</summary>
    public TimeSpan MetricsInterval { get; init; } = TimeSpan.Zero;

    /// <summary>R6: chờ bao lâu rồi mới thử theo dõi lại thư mục bị lỗi (lần đầu); nhân đôi mỗi lần thất bại.</summary>
    public TimeSpan RecoveryInitialDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>R6: thời gian chờ tối đa giữa 2 lần thử theo dõi lại.</summary>
    public TimeSpan RecoveryMaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    // [Thay đổi] M5: thư mục gốc biến mất LIÊN TỤC quá lâu (bị xoá / chuyển đi) → xoá bản ghi bên dưới; bản ghi nằm ngoài mọi thư mục theo dõi quá
    // lâu → xoá. Đủ dài để rút ổ mạng/USB qua đêm, đổi tên tạm không bị xoá nhầm (xoá rồi phải quét lại toàn bộ khi có lại).
    /// <summary>Ân hạn trước khi xoá bản ghi của thư mục gốc đã mất / bản ghi ngoài mọi thư mục theo dõi ("Watch:RootMissingGraceHours").</summary>
    public TimeSpan RootMissingGrace { get; init; } = TimeSpan.FromHours(24);

    // [Thay đổi] M2: trần hàng chờ — số file KHÁC NHAU đang chờ xử lý (mỗi mục ~300 byte). Đầy → thư mục bị đánh dấu "dirty" để quét bù sau, KHÔNG mất việc.
    /// <summary>Số file khác nhau tối đa đang chờ xử lý ("Watch:MaxPendingFiles").</summary>
    public int MaxPendingFiles { get; init; } = 10_000;

    // [Thay đổi] M2: giãn nhịp — mỗi worker nghỉ khoảng này SAU mỗi file (không giữ chỗ xử lý khi nghỉ) để hạ CPU lúc burst. 0 = tắt (mặc định).
    /// <summary>Thời gian nghỉ giữa 2 file của mỗi worker ("Watch:MinMillisecondsBetweenFiles"); Zero = không nghỉ.</summary>
    public TimeSpan MinDelayBetweenFiles { get; init; } = TimeSpan.Zero;
}
