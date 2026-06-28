using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honus.Agent.Model;

/// 全局 JSON 约定:camelCase 字段名、snake_case 枚举值、省略 null。
/// 序列化与反序列化两端必须一致(服务器需用同样约定复算 canonical 哈希)。
public static class Json
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
