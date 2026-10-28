using GcoresLite.Infrastructure.Logging;
using GcoresLite.Models;
using GcoresLite.Services.Api;

namespace GcoresLite.Services.Radio;

/// <summary>
/// 随机电台：跨来源取池 + 洗牌袋，保证「不重样、不自相矛盾」。
/// 池子在后台补充，绝不阻塞当前播放。
/// </summary>
public sealed class RadioService
{
    private const int BagTarget = 24;
    private const int LowWaterMark = 4;

    private readonly GcoresApiClient _api;
    private readonly Storage.ProgressRepository _progress;
    private readonly ILog _log;
    private readonly Random _rng = new();

    private readonly List<RadioSummary> _bag = new();
    private readonly HashSet<string> _played = new(StringComparer.Ordinal);
    private HashSet<string> _recentHistory = new(StringComparer.Ordinal);
    private bool _historyLoaded;
    private Task? _refilling;

    public RadioService(GcoresApiClient api, Storage.ProgressRepository progress, ILog log)
    {
        _api = api;
        _progress = progress;
        _log = log;
    }

    public int BagCount => _bag.Count;

    /// <summary>取下一首。池子不足时先补池。</summary>
    public async Task<RadioSummary?> NextAsync(CancellationToken ct = default)
    {
        if (!_historyLoaded)
        {
            try { _recentHistory = await _progress.GetRecentAsync(14).ConfigureAwait(false); }
            catch (Exception ex) { _log.Warn($"读取收听历史失败：{ex.Message}"); }
            _historyLoaded = true;
        }

        if (_bag.Count <= LowWaterMark)
            await RefillAsync(ct).ConfigureAwait(false);

        if (_bag.Count == 0)
        {
            // 极端情况（全被过滤），清空本轮已播，允许重来
            _played.Clear();
            await RefillAsync(ct).ConfigureAwait(false);
        }

        if (_bag.Count == 0) return null;

        var pick = _bag[0];
        _bag.RemoveAt(0);

        _played.Add(pick.Id);
        _ = SafeMarkPlayedAsync(pick.Id);

        // 剩得少就后台续池，不打断播放
        if (_bag.Count <= LowWaterMark)
            _refilling ??= Task.Run(() => RefillAsync(CancellationToken.None))
                               .ContinueWith(_ => _refilling = null, TaskScheduler.Default);

        return pick;
    }

    /// <summary>换台：清空袋子重新洗牌，不打断当前曲目。</summary>
    public async Task ReshuffleAsync(CancellationToken ct = default)
    {
        _bag.Clear();
        _played.Clear();
        await RefillAsync(ct).ConfigureAwait(false);
        _log.Info($"电台换台，新池 {_bag.Count} 首");
    }

    private async Task RefillAsync(CancellationToken ct)
    {
        try
        {
            var offsets = new[]
            {
                RandomOffset(360), // 最新
                RandomOffset(280), // 机核自制
                RandomOffset(560)  // 入驻播客
            };

            var latestTask = _api.GetLatestAsync(offsets[0], 20, false, ct);
            var gcoresTask = _api.GetLatestAsync(offsets[1], 20, true, ct);
            var podcastTask = _api.GetJoinPodcastsAsync(offsets[2], 20, ct);

            await Task.WhenAll(latestTask, gcoresTask, podcastTask).ConfigureAwait(false);

            var candidates = new List<RadioSummary>(60);
            candidates.AddRange(latestTask.Result.Items);
            candidates.AddRange(gcoresTask.Result.Items);
            candidates.AddRange(podcastTask.Result.Items);

            var fresh = candidates
                .Where(r => r.IsPlayable)
                .Where(r => r.DurationSeconds is >= 120 and <= 4 * 3600)
                .Where(r => !_played.Contains(r.Id))
                .Where(r => !_recentHistory.Contains(r.Id))
                .GroupBy(r => r.Id)
                .Select(g => g.First())
                .OrderBy(_ => _rng.Next())
                .Take(BagTarget - _bag.Count)
                .ToList();

            // 历史排除太狠时逐步放宽，保证电台永远不会「没得放」
            if (fresh.Count < 5)
            {
                _log.Info("电台候选过少，放宽历史过滤");
                fresh = candidates
                    .Where(r => r.IsPlayable)
                    .Where(r => !_played.Contains(r.Id))
                    .GroupBy(r => r.Id)
                    .Select(g => g.First())
                    .OrderBy(_ => _rng.Next())
                    .Take(BagTarget - _bag.Count)
                    .ToList();
            }

            _bag.AddRange(fresh);
            _log.Info($"电台补池 +{fresh.Count}，当前 {_bag.Count} 首");
        }
        catch (Exception ex)
        {
            _log.Warn($"电台补池失败：{ex.Message}");
        }
    }

    private int RandomOffset(int max) => _rng.Next(0, Math.Max(1, max / 20)) * 20;

    private async Task SafeMarkPlayedAsync(string radioId)
    {
        try { await _progress.MarkPlayedAsync(radioId).ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn($"记录收听历史失败：{ex.Message}"); }
    }
}
