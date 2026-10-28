namespace GcoresLite.Models;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Offset)
{
    public static PagedResult<T> Empty(int offset) => new(Array.Empty<T>(), 0, offset);
}

public enum PlayerState
{
    Idle,
    Opening,
    Buffering,
    Playing,
    Paused,
    Ended,
    Failed
}

public enum WindowMode
{
    /// <summary>大模式：380×620 主界面。</summary>
    Full,

    /// <summary>小模式：380×88 置顶迷你条。</summary>
    Mini,

    /// <summary>极小模式：76×76 只显示封面的悬浮方块。</summary>
    Tiny
}

/// <summary>播放进度的持久化记录。</summary>
public sealed record PlaybackProgress(
    string RadioId,
    long PositionMs,
    long DurationMs,
    bool Completed,
    DateTimeOffset UpdatedAt);
