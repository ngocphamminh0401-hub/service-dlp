// [Thay đổi] Thêm namespace theo folder Reading/ để các file khác biết class này thuộc nhóm nào
// [Thay đổi] Mở rộng định dạng - Bước 1: từ "chỉ đọc .txt" thành BỘ ĐIỀU PHỐI — chọn IContentExtractor theo đuôi file,
// kiểm tra kích thước, bắt mọi lỗi trích xuất, giới hạn số ký tự. Không biết chi tiết định dạng nào (nằm ở Extractors/).
using System.Diagnostics;
using System.Reflection;
using System.Text;
using DlpService.Platform; // [Thay đổi] Tách lõi bước 4: IPathPolicy
using DlpService.Reading.Extractors; // [Thay đổi] H4: ExtractionLimitException
using DlpService.Text;

namespace DlpService.Reading;

/// <summary>Đọc file an toàn theo định dạng (txt, csv, docx, pptx, xlsx, xls, pdf...): không crash khi file thiếu,
/// quá lớn, bị khoá, thiếu quyền, hỏng hoặc có mật khẩu.</summary>
public class FileReaderService
{
    // [Thay đổi] Mở rộng định dạng: text trích ra tối đa 2 triệu ký tự. File nén (docx/xlsx...) 50MB có thể giải nén
    // ra hàng trăm MB text — vượt thì CẮT và vẫn quét phần đầu (DLP: quét 1 phần tốt hơn không quét).
    private const int MaxExtractedChars = 2_000_000;

    // [Thay đổi] Giới hạn thời gian (Bước 11): 1 file đọc quá lâu (file bất thường, cấu trúc cố tình gây chậm)
    // sẽ bị dừng lại thay vì treo cả luồng quét — kiểm tra sau MỖI block để dừng sớm nhất có thể.
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);

    // Bảng tra: đuôi file (viết thường, có dấu chấm) → extractor xử lý đuôi đó
    private readonly Dictionary<string, IContentExtractor> _extractors = new(StringComparer.OrdinalIgnoreCase);

    // [Thay đổi] Mở rộng định dạng: đăng ký bảng mã Windows (1258 tiếng Việt, 1252...) — .NET mặc định chỉ có
    // UTF-8/UTF-16/ASCII. Cần cho CSV/TXT lưu bằng ANSI và cho file .xls cũ (ExcelDataReader).
    static FileReaderService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // [Thay đổi] Tách lõi bước 4: quy tắc đường dẫn theo hệ điều hành (file tạm nào bị bỏ qua) — trước đây chặn "~$" viết cứng trong IsSupported
    private readonly IPathPolicy _pathPolicy;

    /// <summary>Tạo service và tự nạp mọi IContentExtractor có trong project (qua reflection).</summary>
    /// <param name="pathPolicy">[Thay đổi] Tách lõi bước 4: quy tắc đường dẫn; null = CaseInsensitivePathPolicy (hành vi Windows như trước đây).</param>
    public FileReaderService(IPathPolicy? pathPolicy = null)
    {
        _pathPolicy = pathPolicy ?? new CaseInsensitivePathPolicy();
        // Tìm mọi class (không abstract) implement IContentExtractor, có constructor không tham số → tạo 1 instance
        // [Thay đổi] Tika native chế độ Replace: tạo instance TRƯỚC rồi mới sắp xếp, vì Priority chỉ biết được sau khi tạo
        // (Tika native đọc cấu hình lúc chạy). Priority cao đăng ký trước → thắng khi trùng đuôi; bằng nhau thì theo tên class.
        var extractors = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IContentExtractor).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false }
                        && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (IContentExtractor)Activator.CreateInstance(t)!)
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.GetType().FullName); // thứ tự cố định: trùng đuôi thì luôn cùng 1 bên thắng

        foreach (var extractor in extractors)
        {
            foreach (var extension in extractor.Extensions)
            {
                _extractors.TryAdd(extension, extractor); // trùng đuôi: giữ extractor nạp trước
            }
        }
    }

    /// <summary>Các đuôi file đọc được, ví dụ ".txt", ".docx" — FileWatcherService dùng để biết theo dõi file nào.</summary>
    public IReadOnlyCollection<string> SupportedExtensions => _extractors.Keys;

    // [Thay đổi] File mới (mục 4.10 - Context builder): tra bảng extractor có sẵn theo đuôi file — dùng khi cần tự
    // gọi Extract() trực tiếp (Worker LLM tự trích lại ở Pass 2, mục 4.9), KHÔNG đi qua TryReadFile (không có
    // timeout/cắt bớt ký tự của luồng quét regex — Pass 2 tự lo phần đó).
    /// <summary>Tìm extractor phù hợp với đuôi file; null nếu không hỗ trợ.</summary>
    /// <param name="filePath">Đường dẫn file.</param>
    // [Thay đổi] Magic bytes: chọn bộ đọc theo nội dung giống hệt TryReadFile — Pass 2 dùng bộ đọc khác Pass 1 thì text (và hash)
    // lệch nhau, job của file đổi đuôi sẽ luôn bị đánh "stale". Không đọc được byte đầu (file bị khoá...) → giữ bộ đọc theo đuôi;
    // Extract() sau đó sẽ tự ném lỗi đúng loại cho nơi gọi xử lý.
    public IContentExtractor? FindExtractor(string filePath)
    {
        string declaredExt = Path.GetExtension(filePath);
        if (!_extractors.TryGetValue(declaredExt, out var extractor))
        {
            return null;
        }

        try
        {
            return ResolveByContent(declaredExt, extractor, FileTypeSniffer.Detect(filePath), out _, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return extractor;
        }
    }

    /// <summary>File này có nên đọc không: đuôi được hỗ trợ VÀ không phải file khoá tạm của Office ("~$baocao.docx").</summary>
    /// <param name="filePath">Đường dẫn file.</param>
    public bool IsSupported(string filePath)
    {
        // Office tạo file "~$tên.docx" (vài trăm byte, chỉ chứa tên người đang mở) khi mở tài liệu — không phải
        // nội dung thật, đọc sẽ lỗi định dạng và làm rác log
        // [Thay đổi] Tách lõi bước 4: việc nhận biết file tạm do IPathPolicy quyết định (Windows: "~$"; Linux sau này: .swp, ~, .part...)
        if (_pathPolicy.IsTemporaryFile(filePath))
        {
            return false;
        }

        return _extractors.ContainsKey(Path.GetExtension(filePath));
    }

    /// <summary>Thử đọc nội dung file dưới dạng text, theo đúng định dạng của file.</summary>
    /// <param name="filePath">Đường dẫn file cần đọc.</param>
    /// <returns>Kết quả đọc: thành công kèm nội dung (và cảnh báo nếu có), hoặc thất bại kèm lý do.</returns>
    public FileReadResult TryReadFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new FileReadResult { Success = false, ErrorMessage = "File không tồn tại" };
        }

        if (!_extractors.TryGetValue(Path.GetExtension(filePath), out var extractor))
        {
            return new FileReadResult { Success = false, ErrorMessage = "Định dạng file không được hỗ trợ" };
        }

        // [Thay đổi] Magic bytes: nhận diện định dạng THẬT từ byte đầu file rồi chọn lại bộ đọc — file bị đổi đuôi (VD .docx → .txt)
        // được đọc bằng đúng parser thay vì bị đọc thành chữ rác rồi kết luận "sạch". Đặt TRƯỚC kiểm tra kích thước vì giới hạn
        // kích thước là của bộ đọc được chọn cuối cùng.
        DetectedFormat detected;
        try
        {
            detected = FileTypeSniffer.Detect(filePath);
        }
        catch (IOException)
        {
            return new FileReadResult { Success = false, ErrorMessage = "File đang bị khóa" };
        }
        catch (UnauthorizedAccessException)
        {
            return new FileReadResult { Success = false, ErrorMessage = "Không có quyền đọc file" };
        }

        extractor = ResolveByContent(Path.GetExtension(filePath), extractor, detected, out string? mismatchWarning, out string? contentError)!;
        if (contentError != null)
        {
            return new FileReadResult { Success = false, ErrorMessage = contentError };
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > extractor.MaxFileSizeBytes)
        {
            return new FileReadResult { Success = false, ErrorMessage = $"File quá lớn (> {extractor.MaxFileSizeBytes / 1024 / 1024}MB)" };
        }

        // [Thay đổi] Streaming (Bước 2): ghép dần từng ContentBlock vào buffer và DỪNG NGAY khi đủ MaxExtractedChars,
        // thay vì gọi Extract() nhận trọn 1 chuỗi rồi mới cắt — file lớn không còn phải đọc hết vào RAM.
        var sb = new StringBuilder();
        bool truncated = false;
        int unreadableBlocks = 0; // [Thay đổi] H3: số khối Unreadable (VD trang PDF scan)
        var timeoutWatch = Stopwatch.StartNew();
        try
        {
            foreach (var block in extractor.Extract(filePath))
            {
                if (timeoutWatch.Elapsed > ExtractionTimeout)
                {
                    return new FileReadResult { Success = false, ErrorMessage = $"Đọc file quá {ExtractionTimeout.TotalSeconds:0}s (nghi ngờ file bất thường)" };
                }

                // [Thay đổi] H3: khối đánh dấu "có nội dung nhưng không đọc được chữ" — chỉ đếm, không đem Text đi quét
                if (block.Kind == BlockKind.Unreadable)
                {
                    unreadableBlocks++;
                    continue;
                }

                // [Thay đổi] Bounded append: trước đây Append CẢ khối rồi mới so giới hạn → 1 khối 10 triệu ký tự làm buffer phình
                // 10 triệu rồi mới cắt. Giờ tính phần còn lại của ngân sách và chỉ nối đúng phần đó.
                int separator = sb.Length > 0 ? 1 : 0;
                int remaining = MaxExtractedChars - sb.Length - separator;
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }
                if (separator == 1)
                {
                    sb.Append('\n');
                }
                if (block.Text.Length > remaining)
                {
                    sb.Append(block.Text, 0, remaining);
                    truncated = true;
                    break; // đủ ngân sách — ngưng đọc tiếp phần còn lại của file, đỡ tốn thời gian/RAM
                }
                sb.Append(block.Text);
            }
        }
        // [Thay đổi] H4: vượt ngân sách giải nén (ZipLimits) — KHÔNG phải file hỏng. Phải đặt TRƯỚC "catch (Exception)" (C# thử catch từ trên
        // xuống, nhánh chung sẽ nuốt mất). Trước đây zip bomb → Success=false → không ghi DB → file lọt khỏi kết quả DLP và bị mở lại mỗi lượt
        // đối soát. Giờ: đọc "thành công" với nội dung rỗng + lý do limit_exceeded → pipeline (H3) ghi incomplete + Unknown + needs_review.
        catch (ExtractionLimitException ex)
        {
            return new FileReadResult
            {
                Success = true,
                Content = string.Empty,
                Warning = ex.Message, // chỉ loại giới hạn + con số, không có tên phần/nội dung
                IncompleteReason = IncompleteReasons.LimitExceeded
            };
        }
        catch (IOException)
        {
            return new FileReadResult { Success = false, ErrorMessage = "File đang bị khóa" };
        }
        catch (UnauthorizedAccessException)
        {
            return new FileReadResult { Success = false, ErrorMessage = "Không có quyền đọc file" };
        }
        catch (Exception ex)
        {
            // [Thay đổi] Mở rộng định dạng: thư viện đọc docx/xlsx/pdf ném đủ loại exception riêng khi file hỏng,
            // có mật khẩu, sai định dạng thật (đổi đuôi)... — bắt hết để 1 file lỗi không làm hỏng luồng xử lý.
            // Vì Extract() dùng yield return, exception có thể chỉ lộ ra khi foreach ở trên duyệt tới phần tử lỗi,
            // không nhất thiết ngay dòng đầu — nhưng vẫn bắt được ở đây vì catch bọc quanh cả vòng lặp.
            // Chỉ ghi TÊN loại lỗi, không ghi ex.Message (có thể chứa trích đoạn nội dung file).
            return new FileReadResult { Success = false, ErrorMessage = $"Không trích xuất được nội dung ({ex.GetType().Name}) — file hỏng, có mật khẩu hoặc sai định dạng" };
        }

        string content = sb.ToString();

        // [Thay đổi] Chuẩn hoá dùng chung (Text/Normalizer.cs): NFC, để MỌI định dạng đều được chuẩn hoá, không phụ
        // thuộc từng extractor. Lý do NFC: xem chú thích tại Normalizer.ToNfc.
        content = Normalizer.ToNfc(content);

        string? warning = null;
        bool needsOcr = false;
        string? incompleteReason = null; // [Thay đổi] H3: cờ "đọc chưa hết" (trước đây chỉ có chuỗi Warning, pipeline không dựa vào được)
        if (truncated || content.Length > MaxExtractedChars)
        {
            warning = $"Nội dung dài, chỉ quét {MaxExtractedChars:N0} ký tự đầu";
            incompleteReason = IncompleteReasons.Truncated;
            if (content.Length > MaxExtractedChars)
            {
                content = content[..MaxExtractedChars]; // chuẩn hoá NFC có thể làm chuỗi dài thêm chút ít
            }
        }
        else if (string.IsNullOrWhiteSpace(content) && fileInfo.Length > 0)
        {
            // File có dung lượng nhưng không trích được chữ nào — điển hình là PDF scan (toàn ảnh), cần OCR.
            // [Thay đổi] mục 1.3/4.8: đánh dấu NeedsOcr riêng (không chỉ là cảnh báo) để phân biệt với file THẬT SỰ
            // sạch — EscalationPolicy dựa vào cờ này để không đẩy xuống LLM (đọc không ra chữ thì gửi cũng vô ích).
            warning = "Không trích được text nào (có thể là PDF/ảnh scan — chưa hỗ trợ OCR)";
            needsOcr = true;
            incompleteReason = IncompleteReasons.NeedsOcr; // [Thay đổi] H3
        }
        else if (unreadableBlocks > 0)
        {
            // [Thay đổi] H3: có chữ ở một phần, phần còn lại (trang scan) không đọc được → chưa đủ căn cứ kết luận
            warning = $"{unreadableBlocks} phần không trích được chữ (trang ảnh scan — chưa hỗ trợ OCR), chỉ quét phần có chữ";
            incompleteReason = IncompleteReasons.UnreadablePages;
        }

        // [Thay đổi] Magic bytes: ghép cảnh báo đổi đuôi với cảnh báo khác (nếu có) — không để cảnh báo này đè cảnh báo kia
        if (mismatchWarning != null)
        {
            warning = warning == null ? mismatchWarning : mismatchWarning + "; " + warning;
        }

        return new FileReadResult { Success = true, Content = content, Warning = warning, NeedsOcr = needsOcr, IncompleteReason = incompleteReason };
    }

    // [Thay đổi] Magic bytes: các đuôi là văn bản thuần — nội dung "Text" ở đây là đúng loại, không phải đổi đuôi
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv", ".eml" };

    /// <summary>
    /// [Thay đổi] Magic bytes: chọn bộ đọc theo định dạng thật của nội dung.
    /// <list type="bullet">
    /// <item>Nhận ra định dạng cụ thể (docx, pdf, xls...) khác đuôi → dùng bộ đọc của định dạng thật + cảnh báo; chưa có bộ đọc cho
    /// định dạng thật (VD .doc khi chưa bật Tika) → báo lỗi rõ ràng, KHÔNG đọc rác.</item>
    /// <item>Nội dung là văn bản nhưng đuôi là định dạng nhị phân (VD .txt đổi thành .docx) → đọc như .txt + cảnh báo.</item>
    /// <item>Đuôi văn bản nhưng nội dung là zip/OLE không rõ loại (zip nén thường, Office có mật khẩu...) → báo lỗi, vì đọc như văn
    /// bản chỉ ra chữ rác và bị kết luận nhầm là "sạch".</item>
    /// <item>Còn lại (đúng đuôi, file rỗng, nhị phân lạ...) → giữ bộ đọc theo đuôi như trước.</item>
    /// </list>
    /// </summary>
    /// <returns>Bộ đọc sẽ dùng; null khi <paramref name="error"/> khác null.</returns>
    private IContentExtractor? ResolveByContent(string declaredExt, IContentExtractor byExtension, DetectedFormat detected,
        out string? warning, out string? error)
    {
        warning = null;
        error = null;

        string? realExt = FileTypeSniffer.CanonicalExtension(detected);
        if (realExt != null)
        {
            if (realExt.Equals(declaredExt, StringComparison.OrdinalIgnoreCase))
            {
                return byExtension; // đúng đuôi — trường hợp phổ biến nhất, không đổi gì
            }
            if (_extractors.TryGetValue(realExt, out var real))
            {
                warning = $"Đuôi {declaredExt} nhưng nội dung thật là {realExt} — đã đọc bằng bộ đọc {realExt}";
                return real;
            }
            error = $"Đuôi {declaredExt} nhưng nội dung thật là {realExt} — định dạng này chưa được hỗ trợ";
            return null;
        }

        bool declaredIsText = PlainTextExtensions.Contains(declaredExt);
        if (detected == DetectedFormat.Text && !declaredIsText && _extractors.TryGetValue(".txt", out var text))
        {
            warning = $"Đuôi {declaredExt} nhưng nội dung là văn bản thuần — đã đọc như .txt";
            return text;
        }
        if (detected is DetectedFormat.OtherZip or DetectedFormat.OtherOle && declaredIsText)
        {
            error = $"Đuôi {declaredExt} nhưng nội dung là file {(detected == DetectedFormat.OtherZip ? "zip" : "OLE")} không rõ loại (file nén, tài liệu có mật khẩu...) — không đọc như văn bản";
            return null;
        }
        return byExtension;
    }
}

/// <summary>Kết quả của 1 lần đọc file.</summary>
public class FileReadResult
{
    /// <summary>Đọc có thành công hay không.</summary>
    public bool Success { get; set; }

    /// <summary>Nội dung file, chỉ có giá trị khi Success = true.</summary>
    public string? Content { get; set; }

    /// <summary>Lý do thất bại, chỉ có giá trị khi Success = false.</summary>
    public string? ErrorMessage { get; set; }

    // [Thay đổi] Mở rộng định dạng: đọc thành công nhưng có điều cần lưu ý (nội dung bị cắt, không có text...)
    /// <summary>Cảnh báo khi đọc thành công nhưng chưa trọn vẹn; null nếu không có gì bất thường.</summary>
    public string? Warning { get; set; }

    // [Thay đổi] mục 1.3/4.8: file có dung lượng nhưng không trích được chữ nào (PDF/ảnh scan) — cần OCR mới đọc
    // được, không phải file thật sự sạch. EscalationPolicy dùng cờ này để không đẩy xuống LLM.
    /// <summary>true nếu file cần OCR (chưa hỗ trợ) mới đọc được nội dung.</summary>
    public bool NeedsOcr { get; set; }

    // [Thay đổi] H3: đọc thành công nhưng CHƯA HẾT nội dung → pipeline không được kết luận "sạch"/"final" (xem IncompleteReasons)
    /// <summary>Lý do đọc chưa hết (giá trị trong <see cref="IncompleteReasons"/>); null nếu đã đọc trọn file.</summary>
    public string? IncompleteReason { get; set; }

    /// <summary>true nếu đã trích được TOÀN BỘ nội dung có chữ của file.</summary>
    public bool IsComplete => IncompleteReason == null;
}

// [Thay đổi] H3: mã lý do ghi vào scan_result.incomplete_reason (chuỗi cố định để truy vấn/thống kê được, không phải câu tiếng Việt)
/// <summary>Các lý do một file chỉ được đọc một phần.</summary>
public static class IncompleteReasons
{
    /// <summary>Nội dung vượt giới hạn ký tự, phần sau không được quét.</summary>
    public const string Truncated = "truncated";

    /// <summary>Không trích được chữ nào (PDF/ảnh scan) — cần OCR.</summary>
    public const string NeedsOcr = "needs_ocr";

    /// <summary>Một phần (VD trang PDF scan) không trích được chữ.</summary>
    public const string UnreadablePages = "unreadable_pages";

    // [Thay đổi] H4
    /// <summary>File vượt giới hạn tài nguyên khi đọc (VD ngân sách giải nén ZIP) — không đọc được gì.</summary>
    public const string LimitExceeded = "limit_exceeded";

    // [Thay đổi] M4: lý do do tầng QUÉT đặt (Scanning.ScanResult.IncompleteReason trả đúng 2 chuỗi này)
    /// <summary>Có rule regex quá thời gian (timeout 1 s/lần tìm) — rule đó chưa quét hết.</summary>
    public const string RegexTimeout = "regex_timeout";

    /// <summary>Hết ngân sách thời gian quét của cả file (Scan:MaxSecondsPerFile) — các rule còn lại chưa chạy.</summary>
    public const string ScanBudget = "scan_budget";
}
