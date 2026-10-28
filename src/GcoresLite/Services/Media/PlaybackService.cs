using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GcoresLite.Infrastructure.Logging;
using GcoresLite.Models;
using GcoresLite.Services.Api;
using GcoresLite.Services.Cache;
using GcoresLite.Services.Radio;
using GcoresLite.Storage;

namespace GcoresLite.Services.Media;

/// <summary>
/// 播放状态中枢：解析音频地址、维护队列、驱动进度条、自动连播、续播落库。
/// UI 只和它打交道，不直接碰 NAudio。
/// </summary>
public sealed partial class PlaybackService : ObservableObject, IDisposable
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ResumeMin = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResumeTailGap = TimeSpan.FromSeconds(30);

    private readonly GcoresApiClient _api;
    private readonly IAudioPlayer _player;
    private readonly ImageCache _images;
    private readonly RadioService _radio;
    private readonly ProgressRepository _progress;
    private readonly ILog _log;
    private readonly DispatcherTimer _ticker;

    private readonly List<RadioSummary> _queue = new();
    private int _index = -1;
    private CancellationTokenSource? _cts;
    private bool _isDragging;
    private DateTime _lastSavedAt = DateTime.MinValue;

    public PlaybackService(
        GcoresApiClient api,
        IAudioPlayer player,
        ImageCache images,
        RadioService radio,
        ProgressRepository progress,
        ILog log)
    {
        _api = api;
        _player = player;
        _images = images;
        _radio = radio;
        _progress = progress;
        _log = log;

        _player.StateChanged += OnPlayerStateChanged;
        _player.PlaybackEnded += OnPlaybackEnded;

        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _ticker.Tick += OnTick;
        _ticker.Start();
    }

    [ObservableProperty] private string _title = "选一期节目开始收听";
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private BitmapImage? _cover;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _seekSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private string? _currentRadioId;
    [ObservableProperty] private bool _isRadioMode;
    [ObservableProperty] private string _radioHint = "从机核的自制和入驻播客里随机挑，不会重样";

    public bool HasTrack => CurrentRadioId is not null;

    /// <summary>普通播放：传入整个列表作为队列，支持上一首/下一首。</summary>
    public async Task PlayAsync(RadioSummary radio, IReadOnlyList<RadioSummary>? queue = null, int index = -1)
    {
        if (!radio.IsPlayable)
        {
            Subtitle = "这是会员专享节目，本工具不提供播放";
            return;
        }

        if (queue is { Count: > 0 })
        {
            _queue.Clear();
            _queue.AddRange(queue);
            _index = index >= 0 && index < _queue.Count ? index : 0;
        }
        else if (_index < 0 || _index >= _queue.Count || _queue[_index].Id != radio.Id)
        {
            _queue.Clear();
            _queue.Add(radio);
            _index = 0;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        CurrentRadioId = radio.Id;
        Title = radio.Title;
        Subtitle = "正在解析音频…";
        IsBusy = true;
        DurationSeconds = Math.Max(1, radio.DurationSeconds);
        DurationText = Fmt(DurationSeconds);
        PositionSeconds = 0;
        SeekSeconds = 0;
        PositionText = "0:00";

        _ = LoadCoverAsync(radio, ct);

        try
        {
            var source = await _api.ResolveAudioAsync(radio.Id, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            if (source.Duration > TimeSpan.Zero)
            {
                DurationSeconds = source.Duration.TotalSeconds;
                DurationText = Fmt(DurationSeconds);
            }

            var startAt = await ResolveResumePointAsync(radio, ct).ConfigureAwait(true);
            Subtitle = startAt > TimeSpan.Zero
                ? $"从 {Fmt(startAt.TotalSeconds)} 继续"
                : string.IsNullOrWhiteSpace(radio.DjText) ? "正在准备播放" : radio.DjText;

            var opened = await _player.OpenAsync(source, startAt, ct).ConfigureAwait(true);
            if (!opened)
            {
                Subtitle = "这一期暂时无法播放";
                return;
            }

            if (startAt > TimeSpan.Zero)
            {
                PositionSeconds = startAt.TotalSeconds;
                SeekSeconds = PositionSeconds;
                PositionText = Fmt(PositionSeconds);
            }

            _player.Play();
        }
        catch (NotPlayableException ex)
        {
            _log.Info($"不可播放 radio={radio.Id}：{ex.Message}");
            Subtitle = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // 用户切歌，静默丢弃
        }
        catch (Exception ex)
        {
            _log.Error($"播放失败 radio={radio.Id}", ex);
            Subtitle = "加载失败，检查网络后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>进入电台模式并播下一首。</summary>
    public async Task StartRadioAsync()
    {
        IsRadioMode = true;
        RadioHint = "正在挑选节目…";

        var next = await _radio.NextAsync().ConfigureAwait(true);
        if (next is null)
        {
            RadioHint = "取不到可播放的节目，检查网络后重试";
            return;
        }

        RadioHint = $"袋中剩余 {_radio.BagCount} 首";
        await PlayAsync(next).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ReshuffleRadioAsync()
    {
        RadioHint = "换台…";
        await _radio.ReshuffleAsync().ConfigureAwait(true);
        RadioHint = $"袋中剩余 {_radio.BagCount} 首";
        await StartRadioAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (!HasTrack) return;

        if (IsPlaying) _player.Pause();
        else _player.Play();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (IsRadioMode)
        {
            await StartRadioAsync().ConfigureAwait(true);
            return;
        }

        if (_queue.Count == 0) return;
        _index = (_index + 1) % _queue.Count;
        await PlayAsync(_queue[_index]).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (_queue.Count == 0) return;
        _index = (_index - 1 + _queue.Count) % _queue.Count;
        await PlayAsync(_queue[_index]).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SkipAsync(int seconds)
    {
        if (!HasTrack) return;

        var target = _player.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;

        await _player.SeekAsync(target).ConfigureAwait(true);
        PositionSeconds = target.TotalSeconds;
        SeekSeconds = PositionSeconds;
        PositionText = Fmt(PositionSeconds);
    }

    [RelayCommand]
    private Task SkipBackAsync() => SkipAsync(-15);

    [RelayCommand]
    private Task SkipForwardAsync() => SkipAsync(15);

    public void BeginSeek() => _isDragging = true;

    public async Task EndSeekAsync()
    {
        _isDragging = false;
        if (!HasTrack) return;

        var target = TimeSpan.FromSeconds(Math.Max(0, SeekSeconds));
        await _player.SeekAsync(target).ConfigureAwait(true);
        PositionSeconds = SeekSeconds;
        PositionText = Fmt(PositionSeconds);
    }

    public async Task SaveProgressAsync()
    {
        if (CurrentRadioId is not { } id) return;

        var position = (long)(_player.Position.TotalSeconds * 1000);
        var duration = (long)(DurationSeconds * 1000);
        var completed = duration > 0 && position >= duration - 30000;

        try
        {
            await _progress.SaveAsync(id, position, duration, completed).ConfigureAwait(false);
            _lastSavedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _log.Warn($"保存进度失败：{ex.Message}");
        }
    }

    /// <summary>只在「听了一部分、且离结尾还远」时续播，避免刚听两句就跳。</summary>
    private async Task<TimeSpan> ResolveResumePointAsync(RadioSummary radio, CancellationToken ct)
    {
        try
        {
            var saved = await _progress.GetAsync(radio.Id).ConfigureAwait(true);
            if (saved is null || saved.Completed) return TimeSpan.Zero;

            var position = TimeSpan.FromMilliseconds(saved.PositionMs);
            if (position < ResumeMin) return TimeSpan.Zero;

            var duration = saved.DurationMs > 0
                ? TimeSpan.FromMilliseconds(saved.DurationMs)
                : TimeSpan.FromSeconds(radio.DurationSeconds);

            if (duration > TimeSpan.Zero && position > duration - ResumeTailGap) return TimeSpan.Zero;

            return position;
        }
        catch (Exception ex)
        {
            _log.Warn($"读取续播点失败：{ex.Message}");
            return TimeSpan.Zero;
        }
    }

    private async Task LoadCoverAsync(RadioSummary radio, CancellationToken ct)
    {
        var url = CoverUrlBuilder.Build(radio.Cover, CoverUrlBuilder.PlayerWidth);
        if (url is null)
        {
            Cover = null;
            return;
        }

        var bmp = await _images.GetAsync(url, 160, ct).ConfigureAwait(true);
        if (!ct.IsCancellationRequested) Cover = bmp;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!HasTrack) return;

        if (!_isDragging)
        {
            var pos = _player.Position.TotalSeconds;
            PositionSeconds = pos;
            SeekSeconds = pos;
            PositionText = Fmt(pos);
        }

        var dur = _player.Duration.TotalSeconds;
        if (dur > 1)
        {
            DurationSeconds = dur;
            DurationText = Fmt(dur);
        }

        if (IsPlaying && DateTime.UtcNow - _lastSavedAt >= SaveInterval)
            _ = SaveProgressAsync();
    }

    private void OnPlayerStateChanged(object? sender, PlayerState state)
    {
        void Apply()
        {
            IsPlaying = state == PlayerState.Playing;
            if (state == PlayerState.Buffering) Subtitle = "缓冲中…";
        }

        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.Invoke(Apply);
        else Apply();
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        void Apply()
        {
            _log.Info($"播放结束 radio={CurrentRadioId} radioMode={IsRadioMode}");

            // 结束时把进度标成已完成，下次从头播
            if (CurrentRadioId is { } id)
                _ = _progress.SaveAsync(id, (long)(DurationSeconds * 1000), (long)(DurationSeconds * 1000), true);

            if (IsRadioMode) _ = StartRadioAsync();
            else _ = NextAsync();
        }

        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess()) d.Invoke(Apply);
        else Apply();
    }

    private static string Fmt(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;

        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    partial void OnCurrentRadioIdChanged(string? value) => OnPropertyChanged(nameof(HasTrack));

    public void Dispose()
    {
        _ticker.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _player.Dispose();
    }
}
