using System.Text.Json;

namespace GcoresLite.Infrastructure.Json;

/// <summary>
/// 容错读取器。机核是老系统，2010 年的数据字段大量缺失、类型混用（bool 可能是 0/1），
/// 所以这里所有读取都必须"读不到给默认值"，绝不抛异常。
/// </summary>
public static class JsonSafe
{
    public static JsonElement? Prop(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

    public static JsonElement? Path(JsonElement e, params string[] names)
    {
        var cur = e;
        foreach (var n in names)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(n, out cur))
                return null;
        }
        return cur;
    }

    public static string? Str(JsonElement e, string name)
    {
        var v = Prop(e, name);
        if (v is null) return null;
        return v.Value.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number => v.Value.ToString(),
            _ => null
        };
    }

    public static int Int(JsonElement e, string name, int fallback = 0)
    {
        var v = Prop(e, name);
        if (v is null) return fallback;
        return v.Value.ValueKind switch
        {
            JsonValueKind.Number => v.Value.TryGetInt32(out var i) ? i : (int)v.Value.GetDouble(),
            JsonValueKind.String => int.TryParse(v.Value.GetString(), out var s) ? s : fallback,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => fallback
        };
    }

    public static bool Bool(JsonElement e, string name, bool fallback = false)
    {
        var v = Prop(e, name);
        if (v is null) return fallback;
        return v.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.Value.TryGetInt32(out var i) && i != 0,
            JsonValueKind.String => bool.TryParse(v.Value.GetString(), out var b) ? b : fallback,
            _ => fallback
        };
    }

    public static IEnumerable<JsonElement> Array(JsonElement e, string name)
    {
        var v = Prop(e, name);
        if (v is null || v.Value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in v.Value.EnumerateArray()) yield return item;
    }

    public static DateTimeOffset? Date(JsonElement e, string name)
    {
        var s = Str(e, name);
        return DateTimeOffset.TryParse(s, out var d) ? d : null;
    }
}
