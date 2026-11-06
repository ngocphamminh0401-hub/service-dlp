// [Thay đổi] File mới (cài validator): đo độ "ngẫu nhiên" (entropy Shannon) của giá trị mật khẩu/token để loại giá trị
// mẫu/giả ("changeme", "12345678", "<your_token>"). Đăng ký trong Validators.ByName với tên "secret_entropy".
namespace DlpService.Scanning.Validation;

/// <summary>Kiểm tra giá trị sau nhãn (password=..., "token": "...") có đủ ngẫu nhiên để là bí mật thật hay không.</summary>
/// <remarks>
/// Entropy Shannon = số bit trung bình cần để mô tả 1 ký tự: H = −Σ p·log₂(p), với p là tỷ lệ xuất hiện của từng ký tự.
///   "aaaaaaaa"    → chỉ 1 loại ký tự                  → H = 0
///   "changeme"    → 7 loại ký tự, 'e' lặp 2 lần        → H ≈ 2,75 (bị loại)
///   "Xk9#mP2vQ7"  → 10 ký tự khác nhau                 → H ≈ 3,32 (đạt)
/// Chuỗi càng ngẫu nhiên, càng nhiều ký tự khác nhau thì H càng cao. Đây là BỘ LỌC, không phải bằng chứng chắc chắn:
/// mật khẩu yếu nhưng thật ("Abc@1234") vẫn đạt, nên không được tính là bằng chứng cho level_gate.
/// </remarks>
public static class SecretEntropyValidator
{
    /// <summary>Độ dài tối thiểu của giá trị.</summary>
    public const int MinLength = 8;

    /// <summary>Entropy tối thiểu (bit/ký tự).</summary>
    public const double MinEntropyBitsPerChar = 3.0;

    /// <summary>Số loại ký tự tối thiểu (chữ thường / chữ hoa / chữ số / ký hiệu).</summary>
    public const int MinCharClasses = 2;

    /// <summary>Giá trị dài từ mức này trở lên thì 1 loại ký tự vẫn chấp nhận (VD token toàn chữ thường rất dài).</summary>
    public const int LongSecretLength = 20;

    /// <summary>true nếu giá trị dài ≥ 8, không phải biến mẫu (&lt;...&gt;, ${...}, {{...}}), entropy ≥ 3,0 và có ≥ 2 loại ký tự.</summary>
    /// <param name="value">Chuỗi regex đã khớp, gồm cả nhãn ("password=...") hoặc chỉ giá trị ("sk-...").</param>
    public static bool IsLikelySecret(string value)
    {
        string secret = ValidatorText.ValueAfterLabel(value);

        if (secret.Length < MinLength || LooksLikeTemplateVariable(secret))
        {
            return false;
        }

        if (ShannonEntropy(secret) < MinEntropyBitsPerChar)
        {
            return false;
        }

        return CountCharClasses(secret) >= MinCharClasses || secret.Length >= LongSecretLength;
    }

    /// <summary>Entropy Shannon của chuỗi (bit/ký tự).</summary>
    public static double ShannonEntropy(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        // Đếm số lần xuất hiện của từng ký tự
        var counts = new Dictionary<char, int>();
        foreach (char c in text)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        // H = −Σ p·log₂(p)
        double entropy = 0;
        foreach (int count in counts.Values)
        {
            double p = (double)count / text.Length;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    // Đếm số loại ký tự có mặt: chữ thường, chữ hoa, chữ số, ký hiệu khác
    private static int CountCharClasses(string text)
    {
        bool lower = false, upper = false, digit = false, other = false;
        foreach (char c in text)
        {
            if (char.IsLower(c)) lower = true;
            else if (char.IsUpper(c)) upper = true;
            else if (char.IsDigit(c)) digit = true;
            else other = true;
        }
        return (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (other ? 1 : 0);
    }

    // Biến mẫu trong tài liệu hướng dẫn/cấu hình: <your_token>, ${DB_PASS}, {{api_key}}, $(SECRET), %PASSWORD%
    private static bool LooksLikeTemplateVariable(string text) =>
        (text.StartsWith('<') && text.EndsWith('>'))
        || text.StartsWith("${", StringComparison.Ordinal)
        || text.StartsWith("{{", StringComparison.Ordinal)
        || text.StartsWith("$(", StringComparison.Ordinal)
        || (text.StartsWith('%') && text.EndsWith('%'));
}
