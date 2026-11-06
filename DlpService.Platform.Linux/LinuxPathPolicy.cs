// [Thay đổi] File mới (GĐ2 bước 2.2a): quy tắc đường dẫn của Linux.
//   - Đường dẫn PHÂN BIỆT hoa/thường ("/home/a/Doc" khác "/home/a/doc") → so sánh Ordinal. Dùng bản Windows (OrdinalIgnoreCase) trên Linux
//     sẽ làm Llm:ExcludedPaths loại trừ nhầm thư mục khác tên chỉ khác hoa/thường.
//   - File tạm/khoá của các ứng dụng hay gặp trên Linux. Nhiều loại có đuôi lạ (".part", ".docx#") nên đằng nào cũng bị bộ lọc đuôi bỏ qua;
//     liệt kê ở đây để chính sách đúng nghĩa của nó ("đây là file tạm") chứ không trông cậy vào việc đuôi tình cờ không được hỗ trợ.
//   Thuần logic chuỗi, không gọi API hệ điều hành → test chạy được trên mọi máy.
namespace DlpService.Platform.Linux;

/// <summary>Đường dẫn phân biệt hoa/thường; bỏ qua file tạm/khoá của Office, LibreOffice, vim, KDE/GNOME, trình duyệt.</summary>
public sealed class LinuxPathPolicy : IPathPolicy
{
    // Tiền tố tên file tạm
    private static readonly string[] TempPrefixes =
    [
        "~$",               // Office: file khoá khi mở tài liệu (gặp cả trên Linux khi file đến từ ổ chia sẻ Windows)
        ".~lock.",          // LibreOffice: file khoá ".~lock.baocao.docx#"
        ".goutputstream-",  // GNOME (gedit, GIO): file tạm khi lưu
    ];

    // Hậu tố tên file tạm (so không phân biệt hoa/thường — đây là đuôi do ứng dụng tự đặt, không phải tên người dùng chọn)
    private static readonly string[] TempSuffixes =
    [
        "~",            // bản sao lưu của vim/emacs/gedit: "baocao.txt~"
        ".swp", ".swo", ".swx", // vim: ".baocao.txt.swp"
        ".kate-swp",    // Kate (KDE)
        ".part",        // Firefox / nhiều trình tải: file đang tải dở
        ".crdownload",  // Chrome / Chromium: file đang tải dở
        ".tmp",         // LibreOffice và nhiều ứng dụng khác khi lưu
    ];

    /// <inheritdoc/>
    public StringComparison Comparison => StringComparison.Ordinal;

    /// <inheritdoc/>
    public bool IsTemporaryFile(string filePath)
    {
        string name = Path.GetFileName(filePath);
        if (name.Length == 0)
        {
            return false;
        }
        foreach (var prefix in TempPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        foreach (var suffix in TempSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
