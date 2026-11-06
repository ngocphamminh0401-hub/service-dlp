// [Thay đổi] File mới (kế hoạch tách lõi, bước 4): không khoá gì. Dùng cho test/tool không cần bảo vệ thư mục, và làm chỗ đứng cho
// nền tảng chưa có adapter. KHÔNG dùng cho service chạy thật — host phải đăng ký guard thật.
namespace DlpService.Platform;

/// <summary>Không đổi quyền thư mục nào.</summary>
public sealed class NoOpDataFolderGuard : IDataFolderGuard
{
    /// <inheritdoc/>
    public void Restrict(string folder)
    {
    }

    /// <inheritdoc/>
    public string Description => "(không khoá)";
}
