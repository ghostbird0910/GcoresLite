namespace GcoresLite.Models;

/// <summary>
/// 机核的播单（专辑）。只展示免费可听的那部分 —— 253 个播单里有 19 个是
/// display-type=audiobook 的付费有声书，必须整体排除，否则点进去也只能看到空列表。
/// </summary>
public sealed record AlbumSummary
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    public string? Description { get; init; }
    public string? Cover { get; init; }
    public int RadioCount { get; init; }

    /// <summary>"gcores"（机核自制）或 "join_podcast"（入驻播客）。</summary>
    public string OwnerType { get; init; } = string.Empty;

    public bool IsPrivilege { get; init; }
    public bool IsFree { get; init; }

    /// <summary>"album"（普通播单）或 "audiobook"（付费有声书）。</summary>
    public string DisplayType { get; init; } = string.Empty;

    public DateTimeOffset? UpdatedAt { get; init; }

    public bool IsPlayable => IsFree && !IsPrivilege;

    public string OwnerText => OwnerType == "gcores" ? "机核" : "入驻";

    public string MetaText => RadioCount > 0 ? $"{RadioCount} 期 · {OwnerText}" : OwnerText;
}
