using System.Text.Json;

namespace GcoresLite.Infrastructure.Json;

/// <summary>
/// JSON:API 的 included 是平铺数组，关系只给 (type,id)。
/// 必须先建索引才能把「节目 → 分类/DJ/专辑」拼起来。
/// </summary>
public sealed class JsonApiIndex
{
    private readonly Dictionary<(string Type, string Id), JsonElement> _map = new();

    public static JsonApiIndex Build(JsonElement root)
    {
        var idx = new JsonApiIndex();
        if (JsonSafe.Prop(root, "data") is { } data) idx.Collect(data);
        if (JsonSafe.Prop(root, "included") is { } included) idx.Collect(included);
        return idx;
    }

    private void Collect(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) Collect(item);
                break;

            case JsonValueKind.Object:
                // 只索引真正的资源对象（有 type + id 字符串）
                if (node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String &&
                    node.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
                {
                    var idText = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
                    if (!string.IsNullOrEmpty(idText))
                        _map[(t.GetString()!, idText)] = node;
                }
                break;
        }
    }

    public JsonElement? Get(string type, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return _map.TryGetValue((type, id), out var el) ? el : null;
    }

    /// <summary>按关系路径取资源，例如 radio → category → data → id。</summary>
    public JsonElement? GetRelated(JsonElement owner, string relationshipName, string targetType)
    {
        var data = JsonSafe.Path(owner, "relationships", relationshipName, "data");
        if (data is null || data.Value.ValueKind != JsonValueKind.Object) return null;
        return Get(targetType, JsonSafe.Str(data.Value, "id"));
    }

    /// <summary>一对多关系（如 djs）返回全部资源。</summary>
    public IEnumerable<JsonElement> GetRelatedMany(JsonElement owner, string relationshipName, string targetType)
    {
        var data = JsonSafe.Path(owner, "relationships", relationshipName, "data");
        if (data is null || data.Value.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in data.Value.EnumerateArray())
        {
            var res = Get(targetType, JsonSafe.Str(item, "id"));
            if (res is not null) yield return res.Value;
        }
    }
}
