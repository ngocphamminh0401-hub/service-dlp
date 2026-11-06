// [Thay đổi] File mới (eligibility filter + phân nhóm P1/P2/P3, kiến trúc mục 4.7-4.8): quyết định file nào được gửi LLM
// thẩm định và thuộc nhóm ưu tiên nào. Logic thuần (không đụng DB) → test độc lập được. Hạn ngạch + hàng đợi đầy kiểm tra
// ở ResultStore (cần đếm job trong DB, cùng transaction với lúc tạo job).
//
// Chưa làm (bước sau): điều kiện llm_cache theo pack_hash + file_hash đã xử lý ở cùng model_ver/prompt_ver (cần bước đóng gói
// của worker LLM); tín hiệu P2 theo tiêu đề cột + mật độ chuỗi số (cần extractor giữ cấu trúc cột).
using System.Globalization;
using System.Text;
using DlpService.Platform; // [Thay đổi] Tách lõi bước 4: IPathPolicy
using DlpService.Text;

namespace DlpService.Classification;

/// <summary>Nhóm ưu tiên gửi LLM. Giá trị số trùng với llm_job.priority (Storage.LlmJobPriority).</summary>
public enum EscalationGroup
{
    /// <summary>P1: có chỗ khớp, vùng xám.</summary>
    P1 = 1,
    /// <summary>P2: không khớp / tín hiệu yếu nhưng tên file hoặc thư mục đáng nghi.</summary>
    P2 = 2,
    /// <summary>P3: lấy mẫu file sạch để đo false negative của regex.</summary>
    P3 = 3
}

/// <summary>Kết quả xét 1 file.</summary>
/// <param name="Group">Nhóm được gửi LLM; null = không gửi.</param>
/// <param name="Reason">Lý do ngắn gọn để log (không chứa nội dung file).</param>
public sealed record EscalationDecision(EscalationGroup? Group, string Reason);

/// <summary>Cấu hình eligibility + phân nhóm, đọc từ appsettings.json mục "Llm".</summary>
public sealed class EscalationOptions
{
    // [Thay đổi] Tách lõi bước 2: công tắc tổng cho LLM (Llm:Enabled). false → KHÔNG file nào được xếp nhóm P1/P2/P3, không tạo llm_job,
    // vùng xám ghi final + needs_review (giữ điểm regex, cần người xem). Mặc định true để bản Windows hiện tại chạy như cũ.
    /// <summary>Bật/tắt toàn bộ đường LLM. Bản không có LLM (Linux) đặt false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Số ký tự text tối thiểu sau khi trích (ít hơn → không đủ ngữ cảnh cho LLM; PDF scan trích ra rỗng cũng bị loại).</summary>
    public int MinTextChars { get; set; } = 200;

    /// <summary>Thư mục không bao giờ gửi LLM (so theo tiền tố đường dẫn, không phân biệt hoa thường).</summary>
    public List<string> ExcludedPaths { get; set; } = [];

    /// <summary>Từ khoá đáng nghi cho P2 (viết không dấu, VD "luong", "hopdong"). So theo từng từ trong tên file/thư mục.</summary>
    // Không gán sẵn giá trị mặc định: ConfigurationBinder NỐI THÊM vào list có sẵn chứ không thay thế → mặc định nằm ở appsettings.json
    public List<string> SuspiciousKeywords { get; set; } = [];

    /// <summary>Tỉ lệ lấy mẫu P3 (phần trăm, 0-100).</summary>
    public double SampleRatePct { get; set; } = 2;
}

/// <summary>Eligibility filter + phân nhóm P1/P2/P3.</summary>
public static class EscalationPolicy
{
    /// <summary>Xét 1 file đã quét xong (đã có vùng của Router).</summary>
    /// <param name="zone">Vùng Router (NoMatch nếu không có chỗ khớp).</param>
    /// <param name="textLength">Số ký tự text đã trích.</param>
    /// <param name="filePath">Đường dẫn đầy đủ (để so ExcludedPaths).</param>
    /// <param name="relativePath">Đường dẫn tính từ thư mục đang theo dõi — chỉ phần này được xét từ khoá P2, để tên thư
    /// mục cha của watch-folder (VD "D:\Backup\...") không biến mọi file thành P2.</param>
    /// <param name="fileHash">SHA-256 (hex) của text — dùng để chọn mẫu P3 ổn định.</param>
    /// <param name="options">Cấu hình.</param>
    /// <param name="needsOcr">[Thay đổi] mục 1.3/4.8: file cần OCR mới đọc được (PDF/ảnh scan) — không biết nội
    /// dung thật sự nên KHÔNG được gửi LLM (gửi cũng vô ích, và không phải "sạch" thật).</param>
    public static EscalationDecision Decide(RouteZone zone, int textLength, string filePath, string relativePath,
        string fileHash, EscalationOptions options, bool needsOcr = false, IPathPolicy? pathPolicy = null)
    {
        // [Thay đổi] Tách lõi bước 2: LLM tắt → không bao giờ gửi, xét trước mọi điều kiện khác
        if (!options.Enabled)
        {
            return new EscalationDecision(null, "LLM tắt (Llm:Enabled=false)");
        }

        // Cần OCR: không biết nội dung thật sự, xét trước cả "chắc chắn" — file dạng này luôn có 0 chỗ khớp
        // (RouteZone.NoMatch) nên không đụng nhánh "chắc chắn", nhưng đặt lý do riêng để log không lẫn với "sạch"
        if (needsOcr)
        {
            return new EscalationDecision(null, "cần OCR (chưa hỗ trợ) — không đẩy LLM");
        }

        // Chắc chắn → final theo regex, không bao giờ tốn LLM (xét trước để không log lý do loại thừa)
        if (zone == RouteZone.Certain)
        {
            return new EscalationDecision(null, "chắc chắn");
        }

        // ---- Điều kiện cần (mục 4.8) ----
        if (textLength < options.MinTextChars)
        {
            return new EscalationDecision(null, $"quá ít text ({textLength} < {options.MinTextChars} ký tự)");
        }

        if (IsExcluded(filePath, options.ExcludedPaths, (pathPolicy ?? DefaultPathPolicy).Comparison))
        {
            return new EscalationDecision(null, "đường dẫn loại trừ");
        }

        // ---- Phân nhóm ----
        if (zone == RouteZone.Grey)
        {
            return new EscalationDecision(EscalationGroup.P1, "vùng xám");
        }

        // Còn lại: NoMatch / Low (S1 < T_low) — mục 4.7: chỉ xuống LLM nếu thuộc P2 hoặc P3
        string? keyword = FindSuspiciousKeyword(relativePath, options.SuspiciousKeywords);
        if (keyword != null)
        {
            return new EscalationDecision(EscalationGroup.P2, $"tên đáng nghi: {keyword}");
        }

        if (IsSampled(fileHash, options.SampleRatePct))
        {
            return new EscalationDecision(EscalationGroup.P3, $"lấy mẫu {options.SampleRatePct:0.##}%");
        }

        return new EscalationDecision(null, "sạch, không đáng nghi, không được chọn mẫu");
    }

    /// <summary>Chọn mẫu P3 theo file_hash thay vì ngẫu nhiên: cùng nội dung → luôn cùng kết quả (quét lại không làm lệch tỉ
    /// lệ, đo tái lập được). SHA-256 phân bố đều nên 8 ký tự hex đầu đủ làm "số ngẫu nhiên".</summary>
    public static bool IsSampled(string fileHash, double sampleRatePct)
    {
        if (sampleRatePct <= 0 || fileHash.Length < 8
            || !uint.TryParse(fileHash.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            return false;
        }

        // Chia 10.000 phần → hỗ trợ tỉ lệ lẻ tới 0,01%
        return value % 10_000 < sampleRatePct * 100;
    }

    /// <summary>Tìm từ khoá đáng nghi trong tên file + tên thư mục. So theo TỪ (sau khi bỏ dấu, tách từ) chứ không so chuỗi
    /// con, để "chatluong" không khớp "luong". Từ khoá được ghép tối đa 4 từ liền nhau: "hopdong" khớp "HopDong", "hop_dong",
    /// "Hợp đồng".</summary>
    /// <returns>Từ khoá khớp đầu tiên; null nếu không có.</returns>
    public static string? FindSuspiciousKeyword(string relativePath, IEnumerable<string> keywords)
    {
        // Bỏ đuôi file (".xlsx" không phải tín hiệu)
        string withoutExtension = Path.ChangeExtension(relativePath, null) ?? relativePath;
        var tokens = Normalizer.Tokenize(withoutExtension);

        foreach (var keyword in keywords)
        {
            string target = string.Concat(Normalizer.Tokenize(keyword));
            if (target.Length == 0)
            {
                continue;
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                var joined = new StringBuilder();
                for (int j = i; j < tokens.Count && j < i + 4; j++)
                {
                    joined.Append(tokens[j]);
                    if (joined.Length == target.Length && joined.ToString() == target)
                    {
                        return keyword;
                    }
                    if (joined.Length >= target.Length)
                    {
                        break;
                    }
                }
            }
        }
        return null;
    }

    // So tiền tố theo thư mục: "D:\Data\Tmp" loại "D:\Data\Tmp\a.txt" nhưng KHÔNG loại "D:\Data\Tmp2\a.txt"
    // [Thay đổi] Tách lõi bước 4: cách so sánh đường dẫn do IPathPolicy quyết định (trước đây viết cứng OrdinalIgnoreCase — sai trên Linux, nơi
    // "/Data/Tmp" và "/data/tmp" là 2 thư mục khác nhau). Không truyền chính sách → giữ hành vi cũ (không phân biệt hoa/thường).
    private static readonly IPathPolicy DefaultPathPolicy = new CaseInsensitivePathPolicy();

    private static bool IsExcluded(string filePath, IEnumerable<string> excludedPaths, StringComparison comparison)
    {
        string full = Path.GetFullPath(filePath);
        foreach (var excluded in excludedPaths)
        {
            if (string.IsNullOrWhiteSpace(excluded))
            {
                continue;
            }
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(excluded)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(folder, comparison))
            {
                return true;
            }
        }
        return false;
    }
}
