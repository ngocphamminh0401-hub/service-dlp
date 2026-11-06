// [Thay đổi] File mới (kế hoạch tách lõi, bước 7): TEST KIẾN TRÚC chứng minh DlpService.Core "sạch" — không phụ thuộc
// thứ chỉ có trên Windows hoặc thuộc phần LLM. Test đọc chính file DlpService.Core.dll bằng reflection:
//   1) danh sách assembly mà Core tham chiếu (GetReferencedAssemblies) — so với danh sách cấm;
//   2) mọi hàm khai báo P/Invoke ([DllImport], và [LibraryImport] vì nó sinh ra DllImport bên dưới) — cấm gọi kernel32/advapi32.
// Giới hạn: GetReferencedAssemblies chỉ thấy assembly mà Core THỰC SỰ dùng trong code. Gói NuGet kéo theo gián tiếp nhưng không
// được gọi tới thì phải kiểm thêm bằng tay: dotnet list DlpService.Core package --include-transitive
using System.Reflection;
using System.Runtime.InteropServices;
using DlpService.Storage;

namespace DlpService.Tests.Architecture;

public class CoreDependencyTests
{
    // Lấy assembly Core qua 1 class chắc chắn nằm trong Core
    private static readonly Assembly Core = typeof(ResultStore).Assembly;

    // Tên assembly (hoặc tiền tố, kết thúc bằng '.') mà Core không được tham chiếu
    private static readonly string[] ForbiddenAssemblies =
    [
        "System.Security.AccessControl",
        "Grpc.",
        "System.Diagnostics.PerformanceCounter",
        "Microsoft.Extensions.Hosting.WindowsServices",
    ];

    // Thư viện native của Windows mà Core không được gọi thẳng
    private static readonly string[] ForbiddenNativeLibs = ["kernel32", "advapi32"];

    [Fact]
    public void Dung_assembly_Core()
    {
        // Chốt chặn: nếu ResultStore bị chuyển khỏi Core, 2 test dưới sẽ kiểm nhầm assembly mà vẫn xanh
        Assert.Equal("DlpService.Core", Core.GetName().Name);
    }

    [Fact]
    public void Core_khong_tham_chieu_assembly_rieng_cua_Windows_hay_LLM()
    {
        var violations = Core.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(name => ForbiddenAssemblies.Any(f => f.EndsWith('.')
                ? name.StartsWith(f, StringComparison.OrdinalIgnoreCase)
                : name.Equals(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(violations.Count == 0,
            "DlpService.Core tham chiếu assembly bị cấm: " + string.Join(", ", violations));
    }

    [Fact]
    public void Core_khong_goi_thang_vao_thu_vien_Windows()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var violations = GetLoadableTypes(Core)
            .SelectMany(t => t.GetMethods(all).Select(m => (Type: t, Method: m)))
            .Select(x => (x.Type, x.Method, Lib: x.Method.GetCustomAttribute<DllImportAttribute>()?.Value))
            .Where(x => x.Lib is not null && ForbiddenNativeLibs.Any(f => x.Lib.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Type.FullName}.{x.Method.Name} → {x.Lib}")
            .ToList();

        Assert.True(violations.Count == 0,
            "DlpService.Core có P/Invoke tới thư viện Windows:\n" + string.Join("\n", violations));
    }

    // GetTypes() ném lỗi nếu 1 type không nạp được (thiếu dependency) → vẫn lấy các type nạp được để kiểm
    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
