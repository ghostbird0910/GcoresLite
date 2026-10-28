using System.Text.Json;
using GcoresLite.Infrastructure.Json;
using GcoresLite.Infrastructure.Logging;
using GcoresLite.Models;

namespace GcoresLite.Services.Api;

/// <summary>
/// 把 JSON:API 的平铺结构还原成节目对象。
/// 关键点：真实节目在 included 里，data 只是外壳（latest-radios），关系全靠 (type,id) 索引拼。
/// </summary>
public sealed class RadioAssembler
{
    private readonly ILog _log;

    public RadioAssembler(ILog log) => _log = log;

    public PagedResult<RadioSummary> ParseList(JsonElement root, int offset)
    {
        var items = new List<RadioSummary>(24);
        var total = ReadTotal(root);
        var index = JsonApiIndex.Build(root);

        var data = JsonSafe.Prop(root, "data");
        if (data is null || data.Value.ValueKind != JsonValueKind.Array)
            return new PagedResult<RadioSummary>(items, total, offset);

        foreach (var node in data.Value.EnumerateArray())
        {
            try
            {
                var radio = ResolveRadioNode(node, index);
                if (radio is null) continue;

                var summary = BuildSummary(radio.Value, index, node);
                if (summary is not null) items.Add(summary);
            }
            catch (Exception ex)
            {
                // 单条脏数据不能毁掉整页
                _log.Warn($"跳过一条无法解析的节目：{ex.Message}");
            }
        }

        return new PagedResult<RadioSummary>(items, total, offset);
    }

    public PagedResult<AlbumSummary> ParseAlbums(JsonElement root, int offset)
    {
        var items = new List<AlbumSummary>(24);
        var total = ReadTotal(root);

        var data = JsonSafe.Prop(root, "data");
        if (data is null || data.Value.ValueKind != JsonValueKind.Array)
            return new PagedResult<AlbumSummary>(items, total, offset);

        foreach (var node in data.Value.EnumerateArray())
        {
            try
            {
                var album = BuildAlbum(node);
                if (album is not null) items.Add(album);
            }
            catch (Exception ex)
            {
                _log.Warn($"跳过一条无法解析的播单：{ex.Message}");
            }
        }

        return new PagedResult<AlbumSummary>(items, total, offset);
    }

    public AlbumSummary? ParseAlbum(JsonElement root)
    {
        var data = JsonSafe.Prop(root, "data");
        if (data is null || data.Value.ValueKind != JsonValueKind.Object) return null;
        return BuildAlbum(data.Value);
    }

    private static AlbumSummary? BuildAlbum(JsonElement node)
    {
        if (!string.Equals(JsonSafe.Str(node, "type"), "albums", StringComparison.Ordinal)) return null;

        var id = JsonSafe.Str(node, "id");
        var attrs = JsonSafe.Prop(node, "attributes");
        if (id is null || attrs is null) return null;

        var a = attrs.Value;
        var title = JsonSafe.Str(a, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        return new AlbumSummary
        {
            Id = id,
            Title = title!,
            Description = JsonSafe.Str(a, "description"),
            Cover = JsonSafe.Str(a, "cover"),
            RadioCount = JsonSafe.Int(a, "radios-count"),
            OwnerType = JsonSafe.Str(a, "owner-type") ?? string.Empty,
            IsPrivilege = JsonSafe.Bool(a, "is-require-privilege"),
            IsFree = JsonSafe.Bool(a, "is-free"),
            DisplayType = JsonSafe.Str(a, "display-type") ?? string.Empty,
            UpdatedAt = JsonSafe.Date(a, "updated-at")
        };
    }

    public RadioDetail? ParseDetail(JsonElement root)
    {
        var index = JsonApiIndex.Build(root);
        var data = JsonSafe.Prop(root, "data");
        if (data is null || data.Value.ValueKind != JsonValueKind.Object) return null;

        var summary = BuildSummary(data.Value, index);
        if (summary is null) return null;

        string? audioRaw = null;
        string? mediaType = null;
        var media = index.GetRelated(data.Value, "media", "medias");
        if (media is not null)
        {
            audioRaw = AttrString(media.Value, "audio");
            mediaType = AttrString(media.Value, "media-type");
        }

        return new RadioDetail
        {
            Summary = summary,
            Excerpt = Truncate(AttrString(data.Value, "excerpt"), 400),
            SummaryText = Truncate(AttrString(data.Value, "summary"), 300),
            AudioRaw = audioRaw,
            MediaType = mediaType,
            AlbumId = FirstRelationId(data.Value, "albums")
        };
    }

    public RadioSummary? BuildSummary(JsonElement radio, JsonApiIndex index, JsonElement? shell = null)
    {
        var id = JsonSafe.Str(radio, "id");
        var attrs = JsonSafe.Prop(radio, "attributes");
        if (id is null || attrs is null) return null;

        var a = attrs.Value;
        var title = JsonSafe.Str(a, "title");

        var category = index.GetRelated(radio, "category", "categories");
        var categoryName = category is null ? null : AttrString(category.Value, "name");

        var djs = index.GetRelatedMany(radio, "djs", "users")
                       .Select(u => AttrString(u, "nickname"))
                       .Where(n => !string.IsNullOrWhiteSpace(n))
                       .Select(n => n!)
                       .ToArray();

        var albumId = shell is null ? null : RelationId(shell.Value, "album");
        albumId ??= FirstRelationId(radio, "albums");

        string? albumTitle = null;
        if (albumId is not null && index.Get("albums", albumId) is { } album)
            albumTitle = AttrString(album, "title");

        return new RadioSummary
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? "未命名节目" : title!,
            DurationSeconds = JsonSafe.Int(a, "duration"),
            Cover = JsonSafe.Str(a, "cover") ?? JsonSafe.Str(a, "thumb"),
            PublishedAt = JsonSafe.Date(a, "published-at"),
            OwnerType = JsonSafe.Str(a, "owner-type") ?? string.Empty,
            IsPrivilege = JsonSafe.Bool(a, "is-require-privilege"),
            Category = string.IsNullOrWhiteSpace(categoryName) ? null : categoryName,
            Djs = djs,
            AlbumTitle = string.IsNullOrWhiteSpace(albumTitle) ? null : albumTitle
        };
    }

    private static JsonElement? ResolveRadioNode(JsonElement node, JsonApiIndex index)
    {
        if (string.Equals(JsonSafe.Str(node, "type"), "radios", StringComparison.Ordinal))
            return node;

        var radioId = RelationId(node, "radio");
        return radioId is null ? null : index.Get("radios", radioId);
    }

    private static int ReadTotal(JsonElement root)
    {
        var meta = JsonSafe.Prop(root, "meta");
        return meta is null ? 0 : JsonSafe.Int(meta.Value, "record-count");
    }

    private static string? RelationId(JsonElement node, string name)
    {
        var data = JsonSafe.Path(node, "relationships", name, "data");
        if (data is null || data.Value.ValueKind != JsonValueKind.Object) return null;
        return JsonSafe.Str(data.Value, "id");
    }

    private static string? FirstRelationId(JsonElement node, string name)
    {
        var data = JsonSafe.Path(node, "relationships", name, "data");
        if (data is null) return null;

        return data.Value.ValueKind switch
        {
            JsonValueKind.Array when data.Value.GetArrayLength() > 0 => JsonSafe.Str(data.Value[0], "id"),
            JsonValueKind.Object => JsonSafe.Str(data.Value, "id"),
            _ => null
        };
    }

    private static string? AttrString(JsonElement resource, string name)
    {
        var attrs = JsonSafe.Prop(resource, "attributes");
        return attrs is null ? null : JsonSafe.Str(attrs.Value, name);
    }

    private static string? Truncate(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}
