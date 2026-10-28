namespace GcoresLite.Models;

public enum AudioKind
{
    /// <summary>机核自有 CDN，audio 字段是裸文件名，需拼 alioss 前缀。</summary>
    GcoresOss = 0,

    /// <summary>入驻播客，audio 字段已是完整外链（多为小宇宙 CDN，会 302）。</summary>
    PodcastExternal = 1
}

/// <summary>列表用的节目摘要，字段与接口 attributes 对齐。</summary>
public sealed record RadioSummary
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    public int DurationSeconds { get; init; }
    public string? Cover { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>"gcores"（机核自制）或 "join_podcast"（入驻播客）。</summary>
    public string OwnerType { get; init; } = string.Empty;

    /// <summary>会员专享标记。判据只能是这个字段，不能用 is-free（实测两者会打架）。</summary>
    public bool IsPrivilege { get; init; }

    public string? Category { get; init; }
    public IReadOnlyList<string> Djs { get; init; } = Array.Empty<string>();
    public string? AlbumTitle { get; init; }

    public bool IsPlayable => !IsPrivilege;

    public string DurationText => DurationSeconds <= 0
        ? "--:--"
        : DurationSeconds >= 3600
            ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"h\:mm\:ss")
            : TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss");

    public string DjText => Djs.Count == 0 ? string.Empty : string.Join("、", Djs);

    public string PublishText => PublishedAt is null
        ? string.Empty
        : PublishedAt.Value.LocalDateTime.ToString("MM-dd");
}

/// <summary>单集详情，比摘要多出简介与媒体信息。</summary>
public sealed record RadioDetail
{
    public required RadioSummary Summary { get; init; }
    public string? Excerpt { get; init; }
    public string? SummaryText { get; init; }

    /// <summary>接口返回的原始 audio 字段：机核自制是文件名，入驻播客是完整 URL。</summary>
    public string? AudioRaw { get; init; }

    public string? MediaType { get; init; }

    /// <summary>专辑/播单 id，用于「播放整个专辑」。</summary>
    public string? AlbumId { get; init; }
}

/// <summary>解析后的可播放音频地址。</summary>
public sealed record AudioSource(string Url, AudioKind Kind, TimeSpan Duration, string Host)
{
    public string FileExtension
    {
        get
        {
            var path = Url.Split('?')[0];
            var ext = Path.GetExtension(path);
            return string.IsNullOrWhiteSpace(ext) ? ".bin" : ext.ToLowerInvariant();
        }
    }
}
