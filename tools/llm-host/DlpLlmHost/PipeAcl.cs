// [Thay đổi] File mới (mục 5.5 - "Bảo mật kênh": "tạo pipe với PipeSecurity chỉ cho SID của Regex service... từ
// chối mọi SID khác"): dựng PipeSecurity chỉ cấp quyền đọc/ghi cho ĐÚNG 1 tài khoản — mặc định user Windows hiện
// tại (chưa có tài khoản dịch vụ ảo thật kiểu NT SERVICE\RegexSvc — đó là việc cài đặt/triển khai, mục 6.1, chưa
// làm), có thể chỉ định tài khoản khác qua --allowed-account khi triển khai thật.
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

namespace DlpLlmHost;

[SupportedOSPlatform("windows")]
public static class PipeAcl
{
    /// <summary>Cấp <see cref="PipeAccessRights.ReadWrite"/> cho CHÍNH tài khoản đang chạy tiến trình này (bắt buộc —
    /// xem [Thay đổi] bên dưới) và cho <paramref name="allowedAccount"/> (bên gọi) nếu khác tài khoản đó — không thêm
    /// "Everyone"/"Authenticated Users" nào khác, nên mọi SID không được liệt kê đều bị từ chối ở tầng hệ điều hành
    /// (mặc định PipeSecurity không có ACE nào là DENY-ALL ngầm định).</summary>
    /// <param name="allowedAccount">Tên tài khoản của BÊN GỌI (Regex service), dạng "DOMAIN\Account" hoặc
    /// "NT AUTHORITY\SYSTEM". Null = chỉ cho phép đúng user Windows đang chạy tiến trình này (đủ cho môi trường dev/1
    /// user, chưa đạt mức cô lập tài khoản dịch vụ riêng).</param>
    public static PipeSecurity BuildPipeSecurity(string? allowedAccount)
    {
        // [Thay đổi] Vá lỗi (buổi 11): tạo PipeSecurity tuỳ chỉnh thì Windows KHÔNG tự cấp quyền ngầm định cho chính
        // tiến trình đang tạo pipe — DACL không liệt kê tài khoản của chính server thì CreateNamedPipe bị từ chối
        // thẳng (UnauthorizedAccessException, Kestrel bọc nhầm thành "address already in use"). Trước đây hàm này chỉ
        // thêm 1 ACE cho allowedAccount — "may mắn" không lộ lỗi vì khi đó allowedAccount VÔ TÌNH trùng tài khoản
        // chạy server; sau khi sửa allowedAccount về đúng tài khoản BÊN GỌI (khác tài khoản server) thì server crash
        // ngay khi khởi động (đã tái hiện thật, exception UnauthorizedAccessException tại NamedPipeServerStream.Create).
        SecurityIdentifier selfSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Không lấy được SID của user hiện tại.");

        // [Thay đổi] CreateNewInstance BẮT BUỘC phải có — Kestrel tạo nhiều instance của CÙNG pipe để phục vụ nhiều
        // kết nối đồng thời (pool), chỉ ReadWrite thì instance thứ 2 trở đi bị từ chối "Access denied" (đã gặp thật
        // khi test: pipe tên MỚI, chưa ai dùng, vẫn báo "already in use" ngay từ instance thứ 2 trong pool).
        const PipeAccessRights rights = PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance;

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(selfSid, rights, AccessControlType.Allow));

        if (allowedAccount != null)
        {
            var callerSid = (SecurityIdentifier)new NTAccount(allowedAccount).Translate(typeof(SecurityIdentifier));
            if (callerSid != selfSid)
            {
                security.AddAccessRule(new PipeAccessRule(callerSid, rights, AccessControlType.Allow));
            }
        }

        return security;
    }

    /// <summary>Kestrel Named Pipes transport mặc định tạo pipe với <see cref="PipeOptions.CurrentUserOnly"/> —
    /// .NET CẤM kết hợp cờ này với <see cref="PipeSecurity"/> tuỳ chỉnh (ném ArgumentException, đã gặp thật khi
    /// test). Muốn tự khai ACL riêng (không chỉ giới hạn "user hiện tại" mà còn hỗ trợ tài khoản dịch vụ khác khi
    /// triển khai thật) thì phải tự viết factory tạo pipe, bỏ cờ đó đi.</summary>
    public static NamedPipeServerStream CreateSecuredServerStream(CreateNamedPipeServerStreamContext context, PipeSecurity pipeSecurity)
    {
        var pipeOptions = context.PipeOptions & ~PipeOptions.CurrentUserOnly;
        return NamedPipeServerStreamAcl.Create(
            context.NamedPipeEndPoint.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            pipeOptions,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: pipeSecurity,
            inheritability: HandleInheritability.None,
            additionalAccessRights: default);
    }
}
