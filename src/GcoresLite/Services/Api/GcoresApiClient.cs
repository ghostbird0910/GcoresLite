using System.Net.Http;
using System.Text.Json;
using GcoresLite.Infrastructure.Logging;
using GcoresLite.Models;

namespace GcoresLite.Services.Api;

public sealed class GcoresApiClient
{
    private readonly HttpClient _http;
    private readonly RadioAssembler _assembler;
    private readonly AudioUrlResolver _urlResolver;
    private readonly ILog _log;

    // 机核接口有频率限制，全局最多 3 个并发请求
    private readonly SemaphoreSlim _gate = new(3, 3);

    public GcoresApiClient(HttpClient http, RadioAssembler assembler, AudioUrlResolver urlResolver, ILog log)
    {
        _http = http;
        _assembler = assembler;
        _urlResolver = urlResolver;
        _log = log;
    }

    /// <summary>最新节目。gcoresOnly=true 时只取机核自制。</summary>
    public Task<PagedResult<RadioSummary>> GetLatestAsync(
        int offset, int limit = 20, bool gcoresOnly = false, CancellationToken ct = default)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("page[limit]", limit.ToString()),
            new("page[offset]", offset.ToString()),
            new("include", "radio.category,radio.djs,album"),
            new("fields[radios]", ApiEndpoints.FieldsRadios),
            new("meta[radios]", ","),
            new("fields[categories]", ApiEndpoints.FieldsCategories),
            new("fields[albums]", ApiEndpoints.FieldsAlbums),
            new("fields[users]", ApiEndpoints.FieldsUsers)
        };

        if (gcoresOnly) query.Add(new("filter[type]", "gcores"));

        return FetchListAsync(ApiEndpoints.PathLatestRadios, query, offset, ct);
    }

    /// <summary>入驻播客，按发布时间倒序。</summary>
    public Task<PagedResult<RadioSummary>> GetJoinPodcastsAsync(
        int offset, int limit = 20, CancellationToken ct = default)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("page[limit]", limit.ToString()),
            new("page[offset]", offset.ToString()),
            new("sort", "-published-at"),
            new("include", "category,djs"),
            new("filter[owner-type]", "join_podcast"),
            new("fields[radios]", ApiEndpoints.FieldsRadios),
            new("fields[categories]", ApiEndpoints.FieldsCategories),
            new("fields[users]", ApiEndpoints.FieldsUsers)
        };

        return FetchListAsync(ApiEndpoints.PathRadios, query, offset, ct);
    }

    public Task<PagedResult<RadioSummary>> GetAlbumRadiosAsync(
        string albumId, int offset, int limit = 20, CancellationToken ct = default)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("page[limit]", limit.ToString()),
            new("page[offset]", offset.ToString()),
            new("sort", "published-at"),
            new("fields[radios]", ApiEndpoints.FieldsRadios)
        };

        return FetchListAsync(ApiEndpoints.AlbumRadios(albumId), query, offset, ct);
    }

    /// <summary>播单列表，按最近更新排序。</summary>
    public async Task<PagedResult<AlbumSummary>> GetAlbumsAsync(
        int offset, int limit = 20, CancellationToken ct = default)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("page[limit]", limit.ToString()),
            new("page[offset]", offset.ToString()),
            new("sort", "-updated-at"),
            new("fields[albums]", ApiEndpoints.FieldsAlbumsList)
        };

        var url = ApiEndpoints.PathAlbums + "?" + GcoresHttp.BuildQuery(query);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = await GcoresHttp.GetJsonAsync(_http, url, ParseRoot, _log, ct).ConfigureAwait(false);
            var result = _assembler.ParseAlbums(root, offset);
            _log.Info($"albums offset={offset} got={result.Items.Count} total={result.Total}");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>单个播单详情（带简介）。</summary>
    public async Task<AlbumSummary?> GetAlbumAsync(string id, CancellationToken ct = default)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("fields[albums]", ApiEndpoints.FieldsAlbumDetail)
        };

        var url = ApiEndpoints.Album(id) + "?" + GcoresHttp.BuildQuery(query);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = await GcoresHttp.GetJsonAsync(_http, url, ParseRoot, _log, ct).ConfigureAwait(false);
            return _assembler.ParseAlbum(root);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RadioDetail?> GetRadioDetailAsync(string id, CancellationToken ct = default)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("include", "category,user,djs,media,albums"),
            new("ctrlfields[radios]", "-content"),
            new("fields[categories]", ApiEndpoints.FieldsCategories),
            new("fields[users]", ApiEndpoints.FieldsUsers),
            new("fields[albums]", ApiEndpoints.FieldsAlbums)
        };

        var url = ApiEndpoints.Radio(id) + "?" + GcoresHttp.BuildQuery(query);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = await GcoresHttp.GetJsonAsync(_http, url, ParseRoot, _log, ct).ConfigureAwait(false);
            return _assembler.ParseDetail(root);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>拿到可直接播放的音频源。会员专享会抛 NotPlayableException。</summary>
    public async Task<AudioSource> ResolveAudioAsync(string id, CancellationToken ct = default)
    {
        var detail = await GetRadioDetailAsync(id, ct).ConfigureAwait(false)
                     ?? throw new ApiException("节目详情为空");

        return _urlResolver.Resolve(detail);
    }

    private async Task<PagedResult<RadioSummary>> FetchListAsync(
        string path, List<KeyValuePair<string, string?>> query, int offset, CancellationToken ct)
    {
        var url = path + "?" + GcoresHttp.BuildQuery(query);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = await GcoresHttp.GetJsonAsync(_http, url, ParseRoot, _log, ct).ConfigureAwait(false);
            var result = _assembler.ParseList(root, offset);
            _log.Info($"list {path} offset={offset} got={result.Items.Count} total={result.Total}");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static JsonElement ParseRoot(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}
