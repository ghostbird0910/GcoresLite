using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using GcoresLite.Infrastructure;
using GcoresLite.Infrastructure.Logging;

namespace GcoresLite.Services.Cache;

/// <summary>
/// 封面缓存：内存 LRU（按解码后字节计费）+ 磁盘（按 sha1 命名）。
/// 刻意不用 BitmapImage.UriSource —— 那会进入 WPF 内部 URI 缓存，长期运行内存只涨不落。
/// </summary>
public sealed class ImageCache
{
    private const long MemoryBudgetBytes = 48L * 1024 * 1024;
    private const int MaxConcurrentDownloads = 6;

    private readonly HttpClient _http;
    private readonly ILog _log;
    private readonly SemaphoreSlim _downloadGate = new(MaxConcurrentDownloads, MaxConcurrentDownloads);

    private readonly Dictionary<string, BitmapImage> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly object _lock = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapImage?>>> _inflight = new();
    private long _memoryBytes;
    private long _hits;
    private long _misses;

    public ImageCache(HttpClient http, ILog log)
    {
        _http = http;
        _log = log;
    }

    public string MemoryReport => $"hit={_hits} miss={_misses} mem={_memoryBytes / 1024 / 1024}MB";

    public async Task<BitmapImage?> GetAsync(string? url, int decodeWidth = 120, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (TryGetMemory(url, out var cached)) return cached;

        // 同一 URL 的并发请求合并成一次
        var lazy = _inflight.GetOrAdd(url, u => new Lazy<Task<BitmapImage?>>(
            () => LoadAsync(u, decodeWidth, ct), LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var result = await lazy.Value.ConfigureAwait(false);
            return result;
        }
        finally
        {
            _inflight.TryRemove(url, out _);
        }
    }

    private async Task<BitmapImage?> LoadAsync(string url, int decodeWidth, CancellationToken ct)
    {
        try
        {
            var path = CachePathFor(url);

            // 失败标记：24h 内不再重试，避免封面失效时形成重试风暴
            if (File.Exists(path + ".bad"))
            {
                var badAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(path + ".bad");
                if (badAge < TimeSpan.FromHours(24)) return null;
                TryDelete(path + ".bad");
            }

            byte[]? data = null;

            if (File.Exists(path))
            {
                data = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                Touch(path);
                Interlocked.Increment(ref _hits);
            }

            if (data is null)
            {
                Interlocked.Increment(ref _misses);
                data = await DownloadAsync(url, ct).ConfigureAwait(false);
                if (data is null)
                {
                    MarkBad(path);
                    return null;
                }

                await WriteAtomicAsync(path, data, ct).ConfigureAwait(false);
            }

            var bmp = Decode(data, decodeWidth);
            if (bmp is null)
            {
                MarkBad(path);
                TryDelete(path);
                return null;
            }

            PutMemory(url, bmp);
            return bmp;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _log.Warn($"封面加载失败 {url}：{ex.Message}");
            return null;
        }
    }

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                                           .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    /// <summary>先写 .part 再改名：半截文件永远不会被当成有效缓存。</summary>
    private static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken ct)
    {
        var tmp = path + ".part";
        await File.WriteAllBytesAsync(tmp, data, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    private static BitmapImage? Decode(byte[] data, int decodeWidth)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 立刻解码完成，不持有流
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze(); // 冻结后可跨线程使用，也才能被 GC 正常回收
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetMemory(string url, out BitmapImage image)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(url, out var found))
            {
                _lru.Remove(url);
                _lru.AddFirst(url);
                image = found;
                return true;
            }
        }

        image = null!;
        return false;
    }

    private void PutMemory(string url, BitmapImage image)
    {
        var cost = EstimateBytes(image);

        lock (_lock)
        {
            if (_memory.TryGetValue(url, out var old))
            {
                _memoryBytes -= EstimateBytes(old);
                _lru.Remove(url);
            }

            _memory[url] = image;
            _lru.AddFirst(url);
            _memoryBytes += cost;

            // 超预算时从最久未用的开始丢
            while (_memoryBytes > MemoryBudgetBytes && _lru.Last is { } last)
            {
                var key = last.Value;
                _lru.RemoveLast();
                if (_memory.Remove(key, out var evicted))
                    _memoryBytes -= EstimateBytes(evicted);
            }
        }
    }

    private static long EstimateBytes(BitmapImage bmp)
        => Math.Max(1, (long)bmp.PixelWidth * bmp.PixelHeight * 4);

    public static string CachePathFor(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(AppPaths.CoverCacheDir, hash + ".jpg");
    }

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { /* 不影响主流程 */ }
    }

    private static void MarkBad(string path)
    {
        try { File.WriteAllBytes(path + ".bad", Array.Empty<byte>()); } catch { /* ignore */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
