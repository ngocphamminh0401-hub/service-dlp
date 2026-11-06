// [Thay đổi] File mới (GĐ3 bước 3.2): thư mục con nào KHÔNG theo dõi và KHÔNG quét. Máy người dùng Linux có hàng chục nghìn thư mục con
// trong home (.cache, .git, node_modules...) — mỗi thư mục tốn 1 inotify watch (máy này giới hạn 8192) mà không chứa tài liệu người dùng.
// Dùng chung cho: nguồn sự kiện (không gắn watch / không báo sự kiện) và lượt quét (khởi động, quét bù, đối soát) → hai bên luôn khớp nhau.
// Mặc định (None): không loại gì — đúng hành vi cũ của bản Windows.
using System.IO.Enumeration;

namespace DlpService.Platform;

/// <summary>Quy tắc bỏ qua thư mục con khi theo dõi và khi quét.</summary>
public sealed class WatchExclusion
{
    /// <summary>Không loại thư mục nào (hành vi cũ).</summary>
    public static WatchExclusion None { get; } = new(skipHidden: false, []);

    private readonly HashSet<string> _names;

    /// <param name="skipHidden">Bỏ thư mục ẩn (tên bắt đầu bằng ".") — quy ước của Linux/macOS.</param>
    /// <param name="directoryNames">Tên thư mục bỏ qua ở MỌI cấp (VD "node_modules", "__pycache__").</param>
    /// <param name="comparer">So sánh tên; null = phân biệt hoa/thường (Linux).</param>
    public WatchExclusion(bool skipHidden, IEnumerable<string> directoryNames, StringComparer? comparer = null)
    {
        SkipHidden = skipHidden;
        _names = new HashSet<string>(directoryNames.Where(n => !string.IsNullOrWhiteSpace(n)), comparer ?? StringComparer.Ordinal);
    }

    /// <summary>Có bỏ thư mục ẩn không.</summary>
    public bool SkipHidden { get; }

    /// <summary>Các tên thư mục bị bỏ qua.</summary>
    public IReadOnlyCollection<string> DirectoryNames => _names;

    /// <summary>Không loại gì cả?</summary>
    public bool IsEmpty => !SkipHidden && _names.Count == 0;

    /// <summary>Thư mục có TÊN này có bị bỏ qua không (chỉ xét tên, không xét thư mục cha).</summary>
    public bool IsExcludedName(string directoryName) =>
        (SkipHidden && directoryName.Length > 1 && directoryName[0] == '.') || _names.Contains(directoryName);

    /// <summary>Đường dẫn có nằm trong 1 thư mục bị bỏ qua (tính từ thư mục gốc đang theo dõi, KHÔNG xét chính thư mục gốc) không.</summary>
    /// <param name="root">Thư mục gốc đang theo dõi.</param>
    /// <param name="path">Đường dẫn file hoặc thư mục bên dưới gốc.</param>
    /// <param name="isDirectory">true: xét cả tên cuối (path là thư mục); false: tên cuối là tên file, không xét.</param>
    public bool IsExcludedPath(string root, string path, bool isDirectory)
    {
        if (IsEmpty) return false;
        string relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal)) return false;
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        int count = isDirectory ? segments.Length : segments.Length - 1;
        for (int i = 0; i < count; i++)
        {
            if (IsExcludedName(segments[i])) return true;
        }
        return false;
    }

    /// <summary>Liệt kê đệ quy mọi file dưới thư mục, bỏ thư mục bị loại trừ, KHÔNG đi theo symlink/junction (tránh vòng lặp và tránh quét
    /// thư mục nằm ngoài vùng theo dõi), bỏ qua thư mục không có quyền đọc thay vì ném lỗi.</summary>
    public IEnumerable<string> EnumerateFiles(string folder) => Enumerate(folder, directories: false);

    /// <summary>Liệt kê đệ quy mọi thư mục con (cùng quy tắc như <see cref="EnumerateFiles"/>).</summary>
    public IEnumerable<string> EnumerateDirectories(string folder) => Enumerate(folder, directories: true);

    private IEnumerable<string> Enumerate(string folder, bool directories)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // symlink (Linux) / junction (Windows)
        };
        return new FileSystemEnumerable<string>(folder, (ref FileSystemEntry e) => e.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory == directories && !(e.IsDirectory && IsExcludedName(e.FileName.ToString())),
            ShouldRecursePredicate = (ref FileSystemEntry e) => !IsExcludedName(e.FileName.ToString()),
        };
    }
}
