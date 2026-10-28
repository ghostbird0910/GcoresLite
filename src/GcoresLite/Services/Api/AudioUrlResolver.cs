using GcoresLite.Models;

namespace GcoresLite.Services.Api;

/// <summary>
/// 音频地址规则（实测确认）：
/// 1) 机核自制 + 非会员：audio 是裸文件名，拼 https://alioss.gcores.com/uploads/audio/
/// 2) 入驻播客：audio 本身即完整外链（小宇宙 CDN，会 302）
/// 3) 会员专享：本工具不解析，直接拒绝
/// </summary>
public sealed class AudioUrlResolver
{
    public AudioSource Resolve(RadioDetail detail)
    {
        var summary = detail.Summary;

        if (summary.IsPrivilege)
            throw new NotPlayableException("这是会员专享节目，本工具不提供播放");

        if (!string.IsNullOrWhiteSpace(detail.MediaType) &&
            !string.Equals(detail.MediaType, "audio", StringComparison.OrdinalIgnoreCase))
            throw new NotPlayableException("这条内容不是音频节目");

        var raw = detail.AudioRaw?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            throw new NotPlayableException("这条节目没有可播放的音频");

        string url;
        AudioKind kind;

        if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = raw;
            kind = AudioKind.PodcastExternal;
        }
        else if (string.Equals(summary.OwnerType, "gcores", StringComparison.OrdinalIgnoreCase))
        {
            url = ApiEndpoints.AudioOssPrefix + Uri.EscapeDataString(raw);
            kind = AudioKind.GcoresOss;
        }
        else
        {
            url = raw;
            kind = AudioKind.PodcastExternal;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new NotPlayableException("音频地址无法解析");

        var duration = TimeSpan.FromSeconds(Math.Max(0, summary.DurationSeconds));
        return new AudioSource(uri.ToString(), kind, duration, uri.Host);
    }
}
