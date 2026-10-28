namespace GcoresLite.Models;

public sealed record Playlist
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int Sort { get; init; }
    public int ItemCount { get; init; }

    public string ItemCountText => ItemCount == 0 ? "空播单" : $"{ItemCount} 期";
}

public sealed record PlaylistItem
{
    public required string PlaylistId { get; init; }
    public required RadioSummary Radio { get; init; }
    public int Position { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}
