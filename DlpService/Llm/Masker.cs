// [Thay đổi] File mới (mục 4.11 - Masker hai lớp): che dữ liệu nhạy cảm trong chunk NGAY TRƯỚC khi gửi LLM, chạy
// sau ChunkSelector (mục 4.10). Không tự viết engine regex mới — dùng lại RegexScanner (bộ rule chính) cho lớp 1 và
// kiểm tra sau mask; chỉ thêm 2 regex "rộng" cho lớp 2.
using System.Text.RegularExpressions;
using DlpService.Scanning;
using DlpService.Scanning.Validation;

namespace DlpService.Llm;

/// <summary>Masker hai lớp: thay giá trị nhạy cảm bằng token giữ hình dạng, không gửi giá trị thật.</summary>
public static partial class Masker
{
    /// <summary>Mask 1 chunk. Trả về null nếu sau mask vẫn còn chuỗi khớp rule cứng (đã qua validator checksum) —
    /// chunk đó bị HUỶ, không gửi, thà thiếu ngữ cảnh còn hơn lộ dữ liệu thật.</summary>
    /// <param name="chunk">Chunk do ChunkSelector chọn (chưa mask).</param>
    /// <param name="scanner">Bộ quét rule chính (đã nạp rule JSON) — dùng lại cho lớp 1 và kiểm tra sau mask.</param>
    public static ChunkResult? Mask(ChunkResult chunk, RegexScanner scanner)
    {
        // Chunk "stats" là số liệu tổng hợp (tên rule + số đếm) do ChunkSelector tự tạo, không chứa giá trị thật
        if (chunk.Kind == "stats")
        {
            return chunk;
        }

        string text = MaskLayer1(chunk.Text, scanner);
        text = MaskLayer2(text);

        if (StillHasHardRuleMatch(text, scanner))
        {
            return null;
        }

        return chunk with { Text = text };
    }

    // Lớp 1: quét lại bằng ĐÚNG bộ rule chính, thay mỗi chỗ khớp bằng token giữ hình dạng
    private static string MaskLayer1(string text, RegexScanner scanner)
    {
        var matches = scanner.Scan(text).Matches;

        // Thay từ CUỐI văn bản về ĐẦU — thay ở cuối trước thì Index của các match còn lại (ở trước) không bị lệch
        foreach (var match in matches.OrderByDescending(m => m.Index))
        {
            string replacement = BuildReplacement(match);
            text = string.Concat(text.AsSpan(0, match.Index), replacement, text.AsSpan(match.Index + match.Value.Length));
        }
        return text;
    }

    // Lớp 2: regex "rộng", KHÔNG có validator/level_gate — bắt số dài hoặc email còn sót vì rule chính có điều kiện
    // phụ (từ khoá, negation, level_gate) mà lớp 1 không khớp.
    private static string MaskLayer2(string text)
    {
        text = BroadEmailPattern().Replace(text, "<EMAIL>");
        text = BroadDigitsPattern().Replace(text, m => $"<NUM{m.Length}>");
        return text;
    }

    // Kiểm tra sau mask (mục 4.11): quét lại lần nữa bằng bộ rule chính; còn chỗ khớp đã qua validator checksum
    // (CCCD, MST, Luhn, IBAN...) tức là mask đã bỏ sót bằng chứng "cứng" — không thể tin tưởng phần còn lại.
    private static bool StillHasHardRuleMatch(string text, RegexScanner scanner) =>
        scanner.Scan(text).Matches.Any(m => m.Validated);

    // Suy chuỗi thay thế theo RuleId (khớp từ khoá trong id, không liệt kê cứng từng rule — bộ rule có thể thêm/bớt
    // mà không phải sửa Masker). Rule chưa khớp từ khoá nào vẫn phải mask (fallback <PII>), không được bỏ qua.
    private static string BuildReplacement(Scanning.ScanMatch match)
    {
        string ruleId = match.RuleId?.ToLowerInvariant() ?? string.Empty;

        // Secret là trường hợp riêng: match.Value thường gồm cả NHÃN + giá trị ("password=Xk9#..."). Giữ nguyên
        // nhãn (mục 4.11: "kèm ngữ cảnh chung quanh (api_key=)"), chỉ che phần giá trị thật.
        if (ruleId.Contains("credentials") || ruleId.Contains("otp") || ruleId.Contains("cvv"))
        {
            return BuildSecretReplacement(match.Value);
        }

        return BuildMaskToken(match);
    }

    private static string BuildMaskToken(Scanning.ScanMatch match)
    {
        string ruleId = match.RuleId?.ToLowerInvariant() ?? string.Empty;

        if (ruleId.Contains("email")) return "<EMAIL>";
        if (ruleId.Contains("phone")) return "<PHONE>";
        if (ruleId.Contains("credit_card") || ruleId.Contains("iban") || ruleId.Contains("swift") || ruleId.Contains("bank_account")) return "<CARD>";
        if (ruleId.Contains("dob")) return "<DATE>";
        if (ruleId.Contains("income")) return "<MONEY>";
        if (ruleId == "vn_name_001") return "<NAME>";

        int digitCount = match.Value.Count(char.IsDigit);
        if (digitCount > 0 && digitCount >= match.Value.Length / 2)
        {
            return $"<NUM{digitCount}>"; // giữ hình dạng: số chữ số thật, không cố định "12" cho mọi loại số
        }

        return "<PII>";
    }

    // Tách nhãn ("password=", "token:") ra khỏi giá trị thật, dùng chung cách tách với SecretEntropyValidator
    // (ValidatorText.ValueAfterLabel) để độ dài/entropy tính trên ĐÚNG giá trị, không tính luôn cả nhãn.
    private static string BuildSecretReplacement(string matchValue)
    {
        int separator = matchValue.IndexOfAny([':', '=']);
        string label = separator >= 0 ? matchValue[..(separator + 1)] : string.Empty;
        string secretValue = ValidatorText.ValueAfterLabel(matchValue);
        return label + BuildSecretToken(secretValue);
    }

    private static string BuildSecretToken(string value)
    {
        double entropy = SecretEntropyValidator.ShannonEntropy(value);
        string level = entropy >= SecretEntropyValidator.MinEntropyBitsPerChar + 1 ? "high"
            : entropy >= SecretEntropyValidator.MinEntropyBitsPerChar ? "medium" : "low";
        return $"<SECRET len={value.Length} entropy={level}>";
    }

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial Regex BroadEmailPattern();

    [GeneratedRegex(@"\d{8,}")]
    private static partial Regex BroadDigitsPattern();
}
