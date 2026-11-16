// [Thay đổi] File mới (H2 — cache size/mtime có thể bỏ qua nội dung đã đổi): "dấu thay đổi" của 1 file do HỆ ĐIỀU HÀNH đặt, người dùng
// thường không tự đặt lại được (khác mtime: `touch -r` / `cp -p` / `tar x` đặt lại được mtime). Dùng thêm vào cache tầng 1 ở lượt quét
// khởi động / đối soát (lúc không có sự kiện watcher để biết file có bị ghi hay không).
// Linux: dev + inode + ctime (LinuxFileChangeToken). Windows: chưa có (NoOpFileChangeToken) — NTFS ChangeTime đặt lại được qua API, hướng
// chắc chắn là USN journal (nợ kỹ thuật).
namespace DlpService.Platform;

/// <summary>Đọc dấu thay đổi của file: chuỗi đổi mỗi khi nội dung/metadata của file đổi (kể cả khi mtime bị đặt lại).</summary>
public interface IFileChangeToken
{
    /// <summary>Dấu thay đổi hiện tại của file.</summary>
    /// <returns>null nếu nền tảng không hỗ trợ hoặc đọc lỗi (file vừa bị xoá, không có quyền) — nơi gọi dùng lại hành vi cũ (chỉ size/mtime).</returns>
    string? Get(string path);
}

/// <summary>Không có dấu thay đổi (Windows, test): cache tầng 1 chỉ dựa vào size/mtime như trước.</summary>
public sealed class NoOpFileChangeToken : IFileChangeToken
{
    /// <summary>Dùng chung, không có trạng thái.</summary>
    public static readonly NoOpFileChangeToken Instance = new();

    /// <inheritdoc/>
    public string? Get(string path) => null;
}
