// [Thay đổi] File mới (mục 4.10 - Context builder): ước lượng số token cho tiếng Việt.
namespace DlpService.Llm;

/// <summary>Ước lượng token khi chưa có tokenizer thật của model.</summary>
public static class TokenEstimator
{
    // Kiến trúc mục 4.10: "ước lượng token bằng số ký tự chia khoảng 3 đối với tiếng Việt, chừa dư". Tiếng Việt có
    // dấu + nhiều âm tiết ngắn nên tốn nhiều token hơn tiếng Anh (thường ước lượng ~4 ký tự/token).
    private const double CharsPerToken = 3.0;

    /// <summary>Ước lượng số token của 1 đoạn text. Làm tròn LÊN — "chừa dư" nghĩa là thà ước lượng dư còn hơn thiếu,
    /// tránh vượt ngân sách token thật của LLM.</summary>
    /// <param name="text">Đoạn text cần ước lượng.</param>
    public static int EstimateTokens(string text) =>
        text.Length == 0 ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken);

    /// <summary>Số ký tự tối đa để không vượt quá "tokenBudget" token, theo cùng công thức ước lượng ở trên (dùng để
    /// cắt bớt text cho vừa ngân sách).</summary>
    /// <param name="tokenBudget">Ngân sách token.</param>
    public static int MaxCharsForBudget(int tokenBudget) => (int)(tokenBudget * CharsPerToken);
}
