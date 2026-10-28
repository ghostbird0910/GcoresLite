using GcoresLite.Models;

namespace GcoresLite.Storage;

public sealed class ProgressRepository
{
    private readonly Db _db;

    public ProgressRepository(Db db) => _db = db;

    /// <summary>记录播放进度。距上次写入不足 5 秒的重复调用由调用方节流。</summary>
    public Task SaveAsync(string radioId, long positionMs, long durationMs, bool completed = false) =>
        _db.WriteAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO playback_progress(radio_id,position_ms,duration_ms,completed,updated_at)
                VALUES($r,$p,$d,$c,$u)
                ON CONFLICT(radio_id) DO UPDATE SET
                  position_ms=$p, duration_ms=$d, completed=$c, updated_at=$u
                """;
            cmd.Parameters.AddWithValue("$r", radioId);
            cmd.Parameters.AddWithValue("$p", Math.Max(0, positionMs));
            cmd.Parameters.AddWithValue("$d", Math.Max(0, durationMs));
            cmd.Parameters.AddWithValue("$c", completed ? 1 : 0);
            cmd.Parameters.AddWithValue("$u", DateTimeOffset.Now.ToString("O"));
            cmd.ExecuteNonQuery();
        });

    public Task<PlaybackProgress?> GetAsync(string radioId) => _db.ReadAsync(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT radio_id,position_ms,duration_ms,completed,updated_at " +
                          "FROM playback_progress WHERE radio_id=$r";
        cmd.Parameters.AddWithValue("$r", radioId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new PlaybackProgress(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt32(3) != 0,
            DateTimeOffset.TryParse(reader.GetString(4), out var d) ? d : DateTimeOffset.MinValue);
    });

    /// <summary>记录到收听历史，用于电台避免重播。</summary>
    public Task MarkPlayedAsync(string radioId) => _db.WriteAsync(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO radio_history(radio_id, played_at) VALUES($r,$t)
            ON CONFLICT(radio_id) DO UPDATE SET played_at=$t
            """;
        cmd.Parameters.AddWithValue("$r", radioId);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    });

    /// <summary>最近 N 天听过的节目 id，电台取池时排除。</summary>
    public Task<HashSet<string>> GetRecentAsync(int days) => _db.ReadAsync(conn =>
    {
        var since = DateTimeOffset.Now.AddDays(-days).ToString("O");
        var set = new HashSet<string>(StringComparer.Ordinal);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT radio_id FROM radio_history WHERE played_at >= $s";
        cmd.Parameters.AddWithValue("$s", since);

        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetString(0));

        return set;
    });
}
