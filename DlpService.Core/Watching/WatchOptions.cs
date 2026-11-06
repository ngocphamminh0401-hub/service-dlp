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
}
