// [Thay đổi] File mới (bước 5.7 cấu trúc): cho phép trường "keywords" trong JSON là MẢNG (rule cũ)
// hoặc OBJECT {primary, secondary} (rule mới như FIN-013) mà vẫn đọc vào cùng 1 kiểu KeywordGroup
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DlpService.Scanning;

public class KeywordGroupJsonConverter : JsonConverter<KeywordGroup>
{
    public override KeywordGroup Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Dạng cũ: "keywords": ["a", "b", "c"] -> coi toàn bộ là Primary
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? new List<string>();
            return new KeywordGroup { Primary = list };
        }

        // Dạng mới: "keywords": { "primary": [...], "secondary": [...] }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            var primary = ReadStringArray(root, "primary");
            var secondary = ReadStringArray(root, "secondary");

            return new KeywordGroup { Primary = primary, Secondary = secondary };
        }

        throw new JsonException("Trường \"keywords\" phải là mảng chuỗi hoặc object {primary, secondary}");
    }

    private static List<string> ReadStringArray(JsonElement root, string propertyName)
    {
        // PropertyNameCaseInsensitive không áp dụng thủ công ở đây nên tự thử cả 2 cách viết
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            return prop.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
        }
        return new List<string>();
    }

    public override void Write(Utf8JsonWriter writer, KeywordGroup value, JsonSerializerOptions options)
    {
        // Engine chỉ đọc rule từ JSON, không cần ghi lại nên không cài hàm này
        throw new NotSupportedException("KeywordGroup chỉ dùng để đọc, không ghi ra JSON.");
    }
}
