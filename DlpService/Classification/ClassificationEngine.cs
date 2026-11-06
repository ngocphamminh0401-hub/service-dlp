// [Thay đổi] File mới (bước 5.2): engine tự nạp các module domain và gộp kết quả phân loại
namespace DlpService.Classification;

// (ILogger logger) = primary constructor, giống RuleLoader: nhận logger từ ngoài, dùng ở mọi hàm
// Engine chỉ biết interface IClassifierModule, không biết module cụ thể nào => thêm domain không phải sửa file này
/// <summary>Tự nạp mọi module domain (qua reflection) và gộp kết quả phân loại của chúng.</summary>
// [Thay đổi] Tách lõi bước 1: + rulesFolder (tuỳ chọn). Có giá trị thì module nào khai constructor nhận 1 tham số string sẽ được
// truyền thư mục rule này (CompoundRuleModule cần đọc compound.json); null → mọi module dùng constructor không tham số như cũ.
public class ClassificationEngine(ILogger logger, string? rulesFolder = null)
{
    // Danh sách module, nạp 1 lần lúc tạo engine rồi chỉ đọc (không thêm bớt) nên nhiều luồng dùng cùng lúc vẫn an toàn
    private readonly List<IClassifierModule> _modules = LoadModules(logger, rulesFolder);

    // Tìm mọi class implement IClassifierModule trong chương trình và tạo đối tượng từ chúng
    private static List<IClassifierModule> LoadModules(ILogger logger, string? rulesFolder)
    {
        var modules = new List<IClassifierModule>();

        // Assembly = "gói" chương trình đã biên dịch; GetTypes() lấy mọi class/interface/enum bên trong (reflection)
        foreach (var type in typeof(ClassificationEngine).Assembly.GetTypes())
        {
            // Chỉ lấy class thật sự implement IClassifierModule; bỏ chính interface và class abstract (không tạo đối tượng được)
            if (!typeof(IClassifierModule).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract)
            {
                continue;
            }

            // Mỗi module có try/catch riêng: 1 module lỗi chỉ bị bỏ qua, không làm chết cả engine
            try
            {
                // CreateInstance tạo đối tượng từ kiểu (cần constructor public không tham số); "is ... module" vừa kiểm tra vừa ép kiểu
                // [Thay đổi] Tách lõi bước 1: có thư mục rule và module có constructor (string) → truyền vào; không thì như cũ
                object? instance = rulesFolder != null && type.GetConstructor([typeof(string)]) != null
                    ? Activator.CreateInstance(type, rulesFolder)
                    : Activator.CreateInstance(type);
                if (instance is IClassifierModule module)
                {
                    modules.Add(module);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Không nạp được module {type}: {error}", type.Name, ex.Message);
            }
        }

        logger.LogInformation("Đã nạp {count} module: {domains}", modules.Count, string.Join(", ", modules.Select(m => m.Domain)));
        return modules;
    }

    /// <summary>Hỏi từng module đã nạp rồi gộp kết quả.</summary>
    /// <param name="context">Dữ liệu file cần phân loại.</param>
    /// <returns>Mức cao nhất trong các module và danh sách ý kiến của từng module.</returns>
    public (SensitivityLevel Level, List<ClassificationResult> Results) Classify(ScanContext context)
    {
        var results = new List<ClassificationResult>();

        foreach (var module in _modules)
        {
            try
            {
                var result = module.Classify(context);

                // null nghĩa là domain đó không quan tâm file này, không thêm vào
                if (result != null)
                {
                    results.Add(result);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Module {type} gặp lỗi: {error}", module.GetType().Name, ex.Message);
            }
        }

        // Không module nào báo gì thì coi như không nhạy cảm
        if (results.Count == 0)
        {
            return (SensitivityLevel.Public, results);
        }

        // Max(): lấy mức lớn nhất, làm được vì enum có thứ tự tăng dần (Public < ... < Restricted)
        return (results.Max(r => r.Level), results);
    }
}
