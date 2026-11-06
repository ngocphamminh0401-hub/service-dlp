// [Thay đổi] File mới (kế hoạch tách lõi, bước 1): bố cục thư mục "cạnh file chạy" — đúng hành vi của bản Windows hiện tại,
// nên thay vào là hết đổi gì (kết quả quét, vị trí DB, vị trí log đều giữ nguyên).
namespace DlpService.Platform;

/// <summary>Mọi thư mục nằm cạnh file chạy (<see cref="AppContext.BaseDirectory"/>): rules/, confidence/, data/, logs/.</summary>
public sealed class ExecutableAppLayout(string? baseDirectory = null) : IAppLayout
{
    // baseDirectory chỉ để test truyền thư mục giả; chạy thật thì để null → dùng thư mục chứa file chạy
    private readonly string _base = baseDirectory ?? AppContext.BaseDirectory;

    /// <inheritdoc/>
    public string RulesFolder => Path.Combine(_base, "rules");

    /// <inheritdoc/>
    public string LogFolder => Path.Combine(_base, "logs");

    /// <inheritdoc/>
    public string DefaultDbPath => Path.Combine(_base, "data", "dlp.db");

    /// <inheritdoc/>
    public string DefaultConfidenceModelPath => Path.Combine(_base, "confidence", "file-confidence-v1.json");

    /// <inheritdoc/>
    public string ResolvePath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(_base, path);
}
