// [Thay đổi] Tối ưu A2 (Vòng 3): chỉ mục "từ khoá nào có mặt ở BẤT KỲ ĐÂU trong file" — thay cho hàng nghìn lần content.Contains(k, OrdinalIgnoreCase)
// mỗi lần quét (1.134 từ khoá × văn bản 2M ký tự; trên văn bản sạch BuildTelemetry chiếm ~46% thời gian Scan).
// Ý tưởng: dựng 1 lần lúc tạo scanner một SearchValues<string> chứa MỌI từ khoá; mỗi file chỉ quét 1 lượt bằng IndexOfAny để biết từ khoá nào xuất hiện.
// Ngữ nghĩa giữ nguyên string.Contains(k, StringComparison.OrdinalIgnoreCase) (có test đối chiếu).
using System.Buffers;

namespace DlpService.Scanning;

/// <summary>Tập từ khoá dùng chung của 1 scanner (dựng 1 lần). Mỗi file tạo <see cref="Presence"/> riêng.</summary>
internal sealed class KeywordUniverse
{
    private readonly string[] _keywords;
    private readonly Dictionary<string, int> _indexOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly SearchValues<string>? _search;
    // Từ khoá nhóm theo 2 ký tự đầu (đã viết hoa bất biến; từ khoá 1 ký tự: ký tự thứ 2 = 0xFFFF) để chỉ thử vài ứng viên mỗi lần IndexOfAny trúng
    // (nhóm theo 1 ký tự đầu làm file dày từ khoá chậm hơn cách cũ ~15% vì mỗi lần trúng phải thử ~60 ứng viên)
    private readonly Dictionary<int, int[]> _byPrefix;
    private const char NoSecondChar = '\uFFFF';

    // char.ToUpperInvariant đi qua ICU từng ký tự (trace: 12% thời gian Scan trên file dày từ khoá) → tra bảng dựng 1 lần cho cả 65.536 ký tự
    private static readonly char[] UpperTable = BuildUpperTable();

    private static char[] BuildUpperTable()
    {
        var table = new char[char.MaxValue + 1];
        for (int c = 0; c <= char.MaxValue; c++)
        {
            table[c] = char.ToUpperInvariant((char)c);
        }
        return table;
    }

    private static int PrefixKey(char first, char second) => (UpperTable[first] << 16) | UpperTable[second];

    public KeywordUniverse(IEnumerable<string> keywords)
    {
        var list = new List<string>();
        foreach (var k in keywords)
        {
            if (!string.IsNullOrEmpty(k) && _indexOf.TryAdd(k, list.Count))
            {
                list.Add(k);
            }
        }
        _keywords = list.ToArray();
        _search = _keywords.Length > 0 ? SearchValues.Create(_keywords, StringComparison.OrdinalIgnoreCase) : null;
        _byPrefix = Enumerable.Range(0, _keywords.Length)
            .GroupBy(i => PrefixKey(_keywords[i][0], _keywords[i].Length > 1 ? _keywords[i][1] : NoSecondChar))
            .ToDictionary(g => g.Key, g => g.ToArray());
    }

    public Presence ForContent(string content) => new(this, content);

    /// <summary>Kết quả tìm từ khoá cho 1 file; tính lười ở lần hỏi đầu tiên.</summary>
    internal sealed class Presence
    {
        private readonly KeywordUniverse _u;
        private readonly string _content;
        private bool[]? _present;

        public Presence(KeywordUniverse u, string content)
        {
            _u = u;
            _content = content;
        }

        /// <summary>Tương đương <c>content.Contains(k, OrdinalIgnoreCase)</c> (từ khoá rỗng → true như string.Contains).</summary>
        public bool Contains(string keyword)
        {
            if (keyword.Length == 0)
            {
                return true;
            }
            if (!_u._indexOf.TryGetValue(keyword, out int idx))
            {
                // Không thuộc tập đã dựng (không nên xảy ra) → quay về cách cũ cho an toàn
                return _content.Contains(keyword, StringComparison.OrdinalIgnoreCase);
            }
            return (_present ??= Compute())[idx];
        }

        /// <summary>Có ít nhất 1 từ khoá (bỏ từ khoá rỗng nếu <paramref name="skipEmpty"/>) xuất hiện trong file.</summary>
        public bool ContainsAny(IEnumerable<string> keywords, bool skipEmpty)
        {
            foreach (var k in keywords)
            {
                if (skipEmpty && k.Length == 0)
                {
                    continue;
                }
                if (Contains(k))
                {
                    return true;
                }
            }
            return false;
        }

        private bool[] Compute()
        {
            var present = new bool[_u._keywords.Length];
            if (_u._search is null)
            {
                return present;
            }
            int remaining = present.Length;
            ReadOnlySpan<char> span = _content;
            int pos = 0;
            while (pos < span.Length && remaining > 0)
            {
                int rel = span[pos..].IndexOfAny(_u._search);
                if (rel < 0)
                {
                    break;
                }
                int at = pos + rel;
                var tail = span[at..];
                // ứng viên từ khoá ≥ 2 ký tự (khớp 2 ký tự đầu) + từ khoá 1 ký tự (nếu có)
                if (tail.Length > 1 && _u._byPrefix.TryGetValue(PrefixKey(tail[0], tail[1]), out var candidates))
                {
                    remaining -= Mark(present, candidates, tail);
                }
                if (_u._byPrefix.TryGetValue(PrefixKey(tail[0], NoSecondChar), out var single))
                {
                    remaining -= Mark(present, single, tail);
                }
                pos = at + 1; // tiến 1 ký tự: từ khoá có thể chồng nhau
            }
            return present;
        }

        private int Mark(bool[] present, int[] candidates, ReadOnlySpan<char> tail)
        {
            int found = 0;
            foreach (int i in candidates)
            {
                if (!present[i] && tail.StartsWith(_u._keywords[i], StringComparison.OrdinalIgnoreCase))
                {
                    present[i] = true;
                    found++;
                }
            }
            return found;
        }
    }
}
