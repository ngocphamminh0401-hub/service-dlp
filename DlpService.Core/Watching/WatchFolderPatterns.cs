// [Thay đổi] File mới (GĐ2, rủi ro R8): cho "Watch:Folders" nhận MẪU có dấu * / ? ở từng cấp thư mục, VD "/home/*/Documents" → thư mục
// Documents của MỌI người dùng có trên máy lúc khởi động. Máy người dùng Linux có nhiều tài khoản, không thể liệt kê sẵn trong cấu hình.
// Chỉ khớp TỪNG CẤP (không có "**" đệ quy) — tránh vô tình theo dõi cả cây /home (tốn inotify watch, xem docs/fsw-thu-muc-con-linux.md).
// Người dùng tạo SAU lúc khởi động chưa được thêm → GĐ3 ("người dùng mới", ke-hoach-trien-khai-linux.md 4.3).
// Dùng chung Windows/Linux (VD "C:/Users/*/Documents").
namespace DlpService.Watching;

/// <summary>Mở rộng mẫu thư mục theo dõi thành danh sách thư mục có thật.</summary>
public static class WatchFolderPatterns
{
    private static readonly char[] Wildcards = ['*', '?'];

    /// <summary>Đường dẫn có chứa ký tự mẫu (* hoặc ?) không.</summary>
    public static bool IsPattern(string path) => path.IndexOfAny(Wildcards) >= 0;

    /// <summary>Mở rộng 1 mẫu thành các thư mục ĐANG tồn tại, sắp xếp theo tên. Đường dẫn không có ký tự mẫu → trả lại chính nó
    /// (dạng tuyệt đối, kể cả khi chưa tồn tại — để nơi gọi báo lỗi "không tồn tại" như trước).</summary>
    public static IReadOnlyList<string> Expand(string pattern)
    {
        string full = Path.GetFullPath(pattern);
        if (!IsPattern(full))
        {
            return [full];
        }

        string root = Path.GetPathRoot(full) ?? "";
        var segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == "**"))
        {
            throw new ArgumentException($"Mẫu \"{pattern}\" dùng \"**\" (đệ quy) — không hỗ trợ, chỉ dùng * cho từng cấp thư mục", nameof(pattern));
        }

        // Không đi vào thư mục không có quyền đọc (VD home của người khác khi chạy tay không phải root) — bỏ qua thay vì ném lỗi
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
        IEnumerable<string> current = [root];
        foreach (var segment in segments)
        {
            current = IsPattern(segment)
                ? current.SelectMany(dir => SafeEnumerate(dir, segment, options))
                : current.Select(dir => Path.Combine(dir, segment)).Where(Directory.Exists);
        }
        return current.Order(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string segment, EnumerationOptions options)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, segment, options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
