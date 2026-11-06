namespace DlpService.Classification;

/// <summary>Hợp đồng mọi module phân loại theo domain (Finance, HR, ...) phải tuân theo.</summary>
public interface IClassifierModule
{
    /// <summary>Tên domain, ví dụ "Finance".</summary>
    string Domain { get; }

    /// <summary>Xem xét file và trả về kết luận theo luật của domain này.</summary>
    /// <param name="context">Dữ liệu file cần xem xét: đường dẫn, nội dung, các chỗ đã khớp regex.</param>
    /// <returns>Kết luận của domain này, hoặc null nếu domain không quan tâm file này.</returns>
    ClassificationResult? Classify(ScanContext context);
}

