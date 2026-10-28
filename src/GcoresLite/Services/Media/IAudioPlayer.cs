using GcoresLite.Models;

namespace GcoresLite.Services.Media;

public interface IAudioPlayer : IDisposable
{
    PlayerState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    float Volume { get; set; }

    event EventHandler<PlayerState>? StateChanged;
    event EventHandler? PlaybackEnded;

    Task<bool> OpenAsync(AudioSource source, TimeSpan startAt, CancellationToken ct = default);
    void Play();
    void Pause();
    Task SeekAsync(TimeSpan target);
    void Stop();
}
