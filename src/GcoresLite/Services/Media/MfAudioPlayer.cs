using GcoresLite.Infrastructure.Logging;
using GcoresLite.Models;
using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GcoresLite.Services.Media;

/// <summary>
/// NAudio + 系统 Media Foundation 的播放器。
///
/// 两条铁律（踩过的人很多）：
/// 1) 所有 NAudio 调用只在一条专用 MTA 线程上串行执行，UI 只投递命令；
/// 2) PlaybackStopped 回调里绝不调用 Stop()/Dispose()，那里只做标记 + 投递收尾命令。
/// </summary>
public sealed class MfAudioPlayer : IAudioPlayer
{
    private readonly BlockingCollection<Action> _commands = new(new ConcurrentQueue<Action>());
    private readonly ILog _log;
    private readonly Thread _worker;
    private readonly System.Threading.Timer _positionTimer;

    private IWavePlayer? _output;
    private MediaFoundationReader? _reader;
    private VolumeSampleProvider? _volumeProvider;
    private float _volume = 0.8f;

    private volatile PlayerState _state = PlayerState.Idle;
    private long _positionTicks;
    private long _durationTicks;
    private int _generation;
    private volatile bool _suppressEnded;

    static MfAudioPlayer()
    {
        // 进程内只需启动一次，不要配 Shutdown，避免重复初始化导致后续 reader 创建失败
        MediaFoundationApi.Startup();
    }

    public MfAudioPlayer(ILog log)
    {
        _log = log;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "GcoresLite.AudioWorker"
        };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();

        _positionTimer = new System.Threading.Timer(_ => Post(RefreshPosition), null, 250, 250);
    }

    public PlayerState State => _state;

    public TimeSpan Position => TimeSpan.FromTicks(Interlocked.Read(ref _positionTicks));

    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));

    /// <summary>
    /// 应用内音量，作用在音频管道上（软件增益）。
    /// 注意：绝不能赋给 WasapiOut.Volume —— 那是 Windows 主音量设备，会改掉系统音量。
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            var target = _volume;
            Post(() =>
            {
                if (_volumeProvider is not null) _volumeProvider.Volume = target;
            });
        }
    }

    public event EventHandler<PlayerState>? StateChanged;
    public event EventHandler? PlaybackEnded;

    public Task<bool> OpenAsync(AudioSource source, TimeSpan startAt, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = Interlocked.Increment(ref _generation);

        Post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                tcs.TrySetResult(false);
                return;
            }

            CloseCurrent();
            SetState(PlayerState.Opening);

            var sysVolBefore = ReadSystemVolume();

            try
            {
                var reader = new MediaFoundationReader(source.Url);
                var total = reader.TotalTime;

                // 有些流拿不到时长，用列表里的 duration 兜底，保证进度条可用
                if (total <= TimeSpan.Zero && source.Duration > TimeSpan.Zero)
                    total = source.Duration;

                if (startAt > TimeSpan.Zero && total > TimeSpan.Zero &&
                    startAt < total - TimeSpan.FromSeconds(1))
                {
                    try { reader.CurrentTime = startAt; }
                    catch (Exception ex) { _log.Warn($"定位续播点失败，从头播放：{ex.Message}"); }
                }

                var output = new WasapiOut(AudioClientShareMode.Shared, 150);
                output.PlaybackStopped += OnPlaybackStopped;

                // 软件音量：只影响本应用，不碰系统主音量
                var volumeProvider = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = _volume };
                output.Init(volumeProvider);

                _reader = reader;
                _volumeProvider = volumeProvider;
                _output = output;

                Interlocked.Exchange(ref _durationTicks, total.Ticks);
                Interlocked.Exchange(ref _positionTicks, reader.CurrentTime.Ticks);

                var sysVolAfter = ReadSystemVolume();
                _log.Info($"opened {source.Host} dur={total:mm\\:ss} start={startAt:mm\\:ss} appvol={_volume:0.00} " +
                          $"sysvol {FmtVol(sysVolBefore)} -> {FmtVol(sysVolAfter)}");
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _log.Error($"打开音频失败 {source.Url}", ex);
                CloseCurrent();
                SetState(PlayerState.Failed);
                tcs.TrySetResult(false);
            }
        });

        return tcs.Task;
    }

    public void Play()
    {
        _suppressEnded = false;
        Post(() =>
        {
            try
            {
                if (_output is null) return;
                _output.Play();
                SetState(PlayerState.Playing);
            }
            catch (Exception ex)
            {
                _log.Error("播放失败", ex);
                SetState(PlayerState.Failed);
            }
        });
    }

    public void Pause()
    {
        _suppressEnded = true;
        Post(() =>
        {
            try
            {
                if (_output is null) return;
                _output.Pause();
                SetState(PlayerState.Paused);
            }
            catch (Exception ex)
            {
                _log.Error("暂停失败", ex);
            }
        });
    }

    public Task SeekAsync(TimeSpan target)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Post(() =>
        {
            try
            {
                if (_reader is not null)
                {
                    var total = _reader.TotalTime > TimeSpan.Zero
                        ? _reader.TotalTime
                        : TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));

                    if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                    if (total > TimeSpan.FromSeconds(2))
                    {
                        var max = total - TimeSpan.FromSeconds(1);
                        if (target > max) target = max;
                    }

                    _reader.CurrentTime = target;
                    Interlocked.Exchange(ref _positionTicks, _reader.CurrentTime.Ticks);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"定位失败（该音频可能不支持拖动）：{ex.Message}");
            }
            finally
            {
                tcs.TrySetResult();
            }
        });

        return tcs.Task;
    }

    public void Stop()
    {
        _suppressEnded = true;
        Post(() =>
        {
            try
            {
                _output?.Stop();
            }
            catch (Exception ex)
            {
                _log.Warn($"停止失败：{ex.Message}");
            }

            SetState(PlayerState.Idle);
        });
    }

    /// <summary>只被播放线程调用。</summary>
    private void CloseCurrent()
    {
        try
        {
            if (_output is not null)
            {
                _output.PlaybackStopped -= OnPlaybackStopped;
                try { _output.Stop(); } catch { /* 已在关闭流程中 */ }
                _output.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"释放输出设备异常：{ex.Message}");
        }
        finally
        {
            _output = null;
        }

        _volumeProvider = null;

        try
        {
            _reader?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warn($"释放音频读取器异常：{ex.Message}");
        }
        finally
        {
            _reader = null;
        }

        Interlocked.Exchange(ref _positionTicks, 0);
    }

    /// <summary>回调线程：这里只能做标记和投递命令，绝不能碰 NAudio 对象。</summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _log.Error("播放中断", e.Exception);
            Post(() =>
            {
                SetState(PlayerState.Failed);
                if (!_suppressEnded) PlaybackEnded?.Invoke(this, EventArgs.Empty);
            });
            return;
        }

        if (_suppressEnded) return;

        Post(() =>
        {
            if (_state is PlayerState.Playing or PlayerState.Buffering)
            {
                SetState(PlayerState.Ended);
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    /// <summary>只在播放线程执行：读 CurrentTime 也必须在同一线程，否则偶发 COM 异常。</summary>
    private void RefreshPosition()
    {
        if (_reader is null) return;
        if (_state is not (PlayerState.Playing or PlayerState.Paused)) return;

        try
        {
            var current = _reader.CurrentTime;
            if (current < TimeSpan.Zero) current = TimeSpan.Zero;
            Interlocked.Exchange(ref _positionTicks, current.Ticks);
        }
        catch
        {
            // 偶发 COM 异常，下一轮再读
        }
    }

    private void SetState(PlayerState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void Post(Action action)
    {
        if (_commands.IsAddingCompleted) return;
        try { _commands.Add(action); } catch { /* 已关闭 */ }
    }

    private void WorkerLoop()
    {
        foreach (var command in _commands.GetConsumingEnumerable())
        {
            try { command(); }
            catch (Exception ex) { _log.Error("音频线程命令执行失败", ex); }
        }
    }

    private static string FmtVol(float? v) => v is null ? "n/a" : $"{v:0.00}";

    /// <summary>读系统主音量（只读，用于验证我们没动过它）。</summary>
    private static float? ReadSystemVolume()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        _positionTimer.Dispose();

        // 关闭命令必须在播放线程上执行
        var done = new ManualResetEventSlim(false);
        Post(() => { CloseCurrent(); done.Set(); });
        _commands.CompleteAdding();
        done.Wait(TimeSpan.FromSeconds(3));
    }
}
