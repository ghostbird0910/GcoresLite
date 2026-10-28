using System.Text.Json;
using GcoresLite.Models;
using Microsoft.Data.Sqlite;

namespace GcoresLite.Storage;

public sealed class PlaylistRepository
{
    private readonly Db _db;

    public PlaylistRepository(Db db) => _db = db;

    public Task<List<Playlist>> GetAllAsync() => _db.ReadAsync(conn =>
    {
        var list = new List<Playlist>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, p.created_at, p.sort,
                   (SELECT COUNT(*) FROM playlist_items i WHERE i.playlist_id = p.id) AS cnt
            FROM playlists p
            ORDER BY p.sort, p.created_at
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Playlist
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                CreatedAt = ParseDate(reader.GetString(2)),
                Sort = reader.GetInt32(3),
                ItemCount = reader.GetInt32(4)
            });
        }

        return list;
    });

    public Task<string> CreateAsync(string name) => _db.ReadAsync(conn =>
    {
        var id = Guid.NewGuid().ToString("N")[..12];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO playlists(id,name,created_at,sort) VALUES($id,$n,$c," +
                          "(SELECT IFNULL(MAX(sort),0)+1 FROM playlists))";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.Now.ToString("O"));
        cmd.ExecuteNonQuery();
        return id;
    });

    public Task RenameAsync(string id, string name) => _db.WriteAsync(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE playlists SET name=$n WHERE id=$id";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    public Task DeleteAsync(string id) => _db.WriteAsync(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM playlist_items WHERE playlist_id=$id; DELETE FROM playlists WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    });

    /// <summary>加入播单。已存在返回 false（不报错）。</summary>
    public Task<bool> AddAsync(string playlistId, RadioSummary radio) => _db.ReadAsync(conn =>
    {
        UpsertRadio(conn, radio);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO playlist_items(playlist_id, radio_id, position, added_at)
            VALUES($p, $r, (SELECT IFNULL(MAX(position),0)+1 FROM playlist_items WHERE playlist_id=$p), $a)
            ON CONFLICT(playlist_id, radio_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$p", playlistId);
        cmd.Parameters.AddWithValue("$r", radio.Id);
        cmd.Parameters.AddWithValue("$a", DateTimeOffset.Now.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>
    /// 整批插入（整张播单加入）。返回实际新增条数，已存在的不重复插。
    /// 注意：Db.WriteAsync 内部已经开了事务，这里不能再 BeginTransaction（会抛嵌套事务异常）。
    /// </summary>
    public async Task<int> AddManyAsync(string playlistId, IReadOnlyList<RadioSummary> radios)
    {
        var added = 0;

        await _db.WriteAsync(conn =>
        {
            var stamp = DateTimeOffset.Now.ToString("O");

            foreach (var radio in radios)
            {
                // 必须先把节目元信息落库：GetItemsAsync 是 JOIN radios 的，
                // 只写 playlist_items 会查不出来（批量版本最初就漏了这一步）
                UpsertRadio(conn, radio);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO playlist_items(playlist_id, radio_id, position, added_at)
                    VALUES($p, $r, (SELECT IFNULL(MAX(position),0)+1 FROM playlist_items WHERE playlist_id=$p), $a)
                    ON CONFLICT(playlist_id, radio_id) DO NOTHING
                    """;
                cmd.Parameters.AddWithValue("$p", playlistId);
                cmd.Parameters.AddWithValue("$r", radio.Id);
                cmd.Parameters.AddWithValue("$a", stamp);
                added += cmd.ExecuteNonQuery();
            }
        }).ConfigureAwait(false);

        return added;
    }

    public Task RemoveAsync(string playlistId, string radioId) => _db.WriteAsync(conn =>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM playlist_items WHERE playlist_id=$p AND radio_id=$r";
        cmd.Parameters.AddWithValue("$p", playlistId);
        cmd.Parameters.AddWithValue("$r", radioId);
        cmd.ExecuteNonQuery();
    });

    public Task<List<RadioSummary>> GetItemsAsync(string playlistId) => _db.ReadAsync(conn =>
    {
        var list = new List<RadioSummary>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.id, r.title, r.duration, r.cover, r.published_at, r.owner_type,
                   r.is_privilege, r.category, r.djs, r.album_title
            FROM playlist_items i
            JOIN radios r ON r.id = i.radio_id
            WHERE i.playlist_id = $p
            ORDER BY i.position
            """;
        cmd.Parameters.AddWithValue("$p", playlistId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RadioSummary
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                DurationSeconds = reader.GetInt32(2),
                Cover = reader.IsDBNull(3) ? null : reader.GetString(3),
                PublishedAt = reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4)),
                OwnerType = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                IsPrivilege = reader.GetInt32(6) != 0,
                Category = reader.IsDBNull(7) ? null : reader.GetString(7),
                Djs = reader.IsDBNull(8) ? Array.Empty<string>() : ParseDjs(reader.GetString(8)),
                AlbumTitle = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }

        return list;
    });

    /// <summary>上下移动。idx 为目标位置，越界自动钳制。</summary>
    public Task MoveAsync(string playlistId, string radioId, int delta) => _db.WriteAsync(conn =>
    {
        var items = new List<(string Id, int Pos)>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT radio_id, position FROM playlist_items WHERE playlist_id=$p ORDER BY position";
            cmd.Parameters.AddWithValue("$p", playlistId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) items.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        var index = items.FindIndex(x => x.Id == radioId);
        if (index < 0) return;

        var target = index + delta;
        if (target < 0 || target >= items.Count) return;

        (items[index], items[target]) = (items[target], items[index]);

        using var tx = conn.BeginTransaction();
        for (var i = 0; i < items.Count; i++)
        {
            using var update = conn.CreateCommand();
            update.CommandText = "UPDATE playlist_items SET position=$pos WHERE playlist_id=$p AND radio_id=$r";
            update.Parameters.AddWithValue("$pos", i + 1);
            update.Parameters.AddWithValue("$p", playlistId);
            update.Parameters.AddWithValue("$r", items[i].Id);
            update.ExecuteNonQuery();
        }
        tx.Commit();
    });

    /// <summary>节目快照写入 radios 表，播单只存 id 引用。</summary>
    private static void UpsertRadio(SqliteConnection conn, RadioSummary radio)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO radios(id,title,duration,cover,published_at,owner_type,is_privilege,category,djs,album_title,fetched_at)
            VALUES($id,$t,$d,$c,$p,$o,$pr,$cat,$djs,$al,$f)
            ON CONFLICT(id) DO UPDATE SET
              title=$t, duration=$d, cover=$c, published_at=$p, owner_type=$o,
              is_privilege=$pr, category=$cat, djs=$djs, album_title=$al, fetched_at=$f
            """;
        cmd.Parameters.AddWithValue("$id", radio.Id);
        cmd.Parameters.AddWithValue("$t", radio.Title);
        cmd.Parameters.AddWithValue("$d", radio.DurationSeconds);
        cmd.Parameters.AddWithValue("$c", (object?)radio.Cover ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", radio.PublishedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$o", radio.OwnerType);
        cmd.Parameters.AddWithValue("$pr", radio.IsPrivilege ? 1 : 0);
        cmd.Parameters.AddWithValue("$cat", (object?)radio.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$djs", JsonSerializer.Serialize(radio.Djs));
        cmd.Parameters.AddWithValue("$al", (object?)radio.AlbumTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", DateTimeOffset.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static DateTimeOffset ParseDate(string text) =>
        DateTimeOffset.TryParse(text, out var d) ? d : DateTimeOffset.MinValue;

    private static string[] ParseDjs(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}
