using GcoresLite.Infrastructure;
using GcoresLite.Infrastructure.Logging;
using Microsoft.Data.Sqlite;

namespace GcoresLite.Storage;

/// <summary>
/// 单连接 + 串行门控。数据量很小（几千条节目 + 播单 + 进度），
/// 单连接比连接池更省心，也彻底避免 SQLite 并发写锁问题。
/// </summary>
public sealed class Db : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILog _log;

    public Db(ILog log)
    {
        _log = log;
        AppPaths.EnsureCreated();

        _conn = new SqliteConnection($"Data Source={AppPaths.DbPath}");
        _conn.Open();

        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA busy_timeout=3000;");
        Exec("PRAGMA temp_store=MEMORY;");

        CreateSchema();
    }

    public async Task<T> ReadAsync<T>(Func<SqliteConnection, T> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => work(_conn)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task WriteAsync(Action<SqliteConnection> work) =>
        ReadAsync(conn =>
        {
            using var tx = conn.BeginTransaction();
            work(conn);
            tx.Commit();
            return true;
        });

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema()
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS radios(
              id TEXT PRIMARY KEY,
              title TEXT NOT NULL,
              duration INTEGER NOT NULL DEFAULT 0,
              cover TEXT,
              published_at TEXT,
              owner_type TEXT,
              is_privilege INTEGER NOT NULL DEFAULT 0,
              category TEXT,
              djs TEXT,
              album_title TEXT,
              fetched_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS playlists(
              id TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              created_at TEXT NOT NULL,
              sort INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS playlist_items(
              playlist_id TEXT NOT NULL,
              radio_id TEXT NOT NULL,
              position INTEGER NOT NULL DEFAULT 0,
              added_at TEXT NOT NULL,
              PRIMARY KEY(playlist_id, radio_id)
            );

            CREATE INDEX IF NOT EXISTS ix_items_pl ON playlist_items(playlist_id, position);

            CREATE TABLE IF NOT EXISTS playback_progress(
              radio_id TEXT PRIMARY KEY,
              position_ms INTEGER NOT NULL DEFAULT 0,
              duration_ms INTEGER NOT NULL DEFAULT 0,
              completed INTEGER NOT NULL DEFAULT 0,
              updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS radio_history(
              radio_id TEXT PRIMARY KEY,
              played_at TEXT NOT NULL
            );
            """;

        Exec(ddl);
        _log.Info($"db ready at {AppPaths.DbPath}");
    }

    public void Dispose()
    {
        _conn.Dispose();
        _gate.Dispose();
    }
}
