using System.Net;
using System.Net.Http;
using System.Text;
using GcoresLite.Infrastructure.Logging;

namespace GcoresLite.Services.Api;

public sealed class ApiException : Exception
{
    public ApiException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>节目不可播放（会员专享 / 无音频 / 非音频内容）。</summary>
public sealed class NotPlayableException : Exception
{
    public NotPlayableException(string message) : base(message) { }
}

/// <summary>全局共享的 HttpClient 配置。API 请求与媒体请求分开，避免头部互相污染。</summary>
public static class GcoresHttp
{
    public static HttpClient CreateApiClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 6,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiEndpoints.BaseUrl),
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.api+json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "GcoresLite/1.0 (Windows; WPF)");
        client.DefaultRequestHeaders.Referrer = new Uri(ApiEndpoints.Referer);
        return client;
    }

    public static HttpClient CreateMediaClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "GcoresLite/1.0 (Windows; WPF)");
        client.DefaultRequestHeaders.Referrer = new Uri(ApiEndpoints.Referer);
        return client;
    }

    /// <summary>把 query 字典编码成 JSON:API 要求的 page[limit]= 形式。</summary>
    public static string BuildQuery(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (value is null) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        return sb.ToString();
    }

    /// <summary>带退避的 GET：408/429/5xx 重试两次，其余直接抛。</summary>
    public static async Task<T> GetJsonAsync<T>(
        HttpClient http,
        string url,
        Func<string, T> parse,
        ILog log,
        CancellationToken ct)
    {
        var delays = new[] { 300, 900 };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                                                .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return parse(body);
                }

                var status = (int)response.StatusCode;
                var retriable = status is 408 or 429 || status >= 500;
                if (!retriable || attempt >= delays.Length)
                    throw new ApiException($"接口返回 {status}（{url}）");

                log.Warn($"HTTP {status} 重试 {attempt + 1} 次：{url}");
            }
            catch (ApiException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (attempt >= delays.Length)
                    throw new ApiException($"网络请求失败：{ex.Message}", ex);

                log.Warn($"网络异常重试 {attempt + 1} 次：{ex.Message}");
            }

            await Task.Delay(delays[attempt], ct).ConfigureAwait(false);
        }
    }
}
