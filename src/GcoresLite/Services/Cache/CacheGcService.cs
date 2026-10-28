using GcoresLite.Infrastructure;
using GcoresLite.Infrastructure.Logging;

namespace GcoresLite.Services.Cache;

public sealed record CacheStats(long CoverBytes, long AudioBytes, long DbBytes, int CoverFiles, int AudioFiles)
{
    public long TotalBytes => CoverBytes + AudioBytes + DbBytes;

    public string CoverText => Format(CoverBytes);
    public string AudioText => Format(AudioBytes);
    public string DbText => Format(DbBytes);
    public string TotalText => Format(TotalBytes);

    public static string Format(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
    };
}

/// <summary>
/// 磁盘配额回收。超预算时按「最久未访问」删到 85% 水位，一次删够，避免每次写入都触发回收。
/// </summary>
public sealed class CacheGcService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    private readonly ILog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRunUtc = DateTime.MinValue;

    public CacheGcService(ILog log) => _log = log;

    public long CoverBudgetBytes { get; set; } = 200L * 1024 * 1024;
    public long AudioBudgetBytes { get; set; } = 2048L * 1024 * 1024;

    public CacheStats GetStats()
    {
        var (coverBytes, coverFiles) = Measure(AppPaths.CoverCacheDir);
        var (audioBytes, audioFiles) = Measure(AppPaths.AudioCacheDir);
        return new CacheStats(coverBytes, audioBytes, MeasureDb(), coverFiles, audioFiles);
    }

    /// <summary>节流执行，force=true 时无视间隔（设置页手动清理用）。</summary>
    public async Task RunAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && DateTime.UtcNow - _lastRunUtc < MinInterval) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && DateTime.UtcNow - _lastRunUtc < MinInterval) return;
            _lastRunUtc = DateTime.UtcNow;

            var freedCovers = await Task.Run(() => Sweep(AppPaths.CoverCacheDir, CoverBudgetBytes), ct)
                                        .ConfigureAwait(false);
            var freedAudio = await Task.Run(() => Sweep(AppPaths.AudioCacheDir, AudioBudgetBytes), ct)
                                       .ConfigureAwait(false);

            if (freedCovers + freedAudio > 0)
                _log.Info($"cache gc freed={CacheStats.Format(freedCovers + freedAudio)} " +
                          $"(covers={CacheStats.Format(freedCovers)} audio={CacheStats.Format(freedAudio)})");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>清空某一层缓存，返回释放的字节数。</summary>
    public Task<long> ClearAsync(string dir, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            long total = 0;
            if (!Directory.Exists(dir)) return 0L;

            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var len = file.Length;
                    file.Delete();
                    total += len;
                }
                catch { /* 单个文件删不掉就跳过 */ }
            }

            _log.Info($"cache cleared {dir} freed={CacheStats.Format(total)}");
            return total;
        }, ct);

    private long Sweep(string dir, long budget)
    {
        if (!Directory.Exists(dir)) return 0;

        var files = new List<FileInfo>();
        long total = 0;

        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                total += file.Length;
                files.Add(file);
            }
            catch { /* 文件被占用 */ }
        }

        if (total <= budget) return 0;

        // 删到 85% 水位，留出余量避免频繁触发
        var target = (long)(budget * 0.85);
        long removed = 0;

        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= target) break;
            try
            {
                var len = file.Length;
                file.Delete();
                total -= len;
                removed += len;
            }
            catch { /* 跳过 */ }
        }

        return removed;
    }

    private static (long Bytes, int Files) Measure(string dir)
    {
        if (!Directory.Exists(dir)) return (0, 0);

        long bytes = 0;
        var count = 0;

        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += file.Length;
                count++;
            }
            catch { /* 跳过 */ }
        }

        return (bytes, count);
    }

    private static long MeasureDb()
    {
        long size = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                var info = new FileInfo(AppPaths.DbPath + suffix);
                if (info.Exists) size += info.Length;
            }
            catch { /* 跳过 */ }
        }
        return size;
    }
}
