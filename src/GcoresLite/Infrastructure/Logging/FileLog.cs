using System.Collections.Concurrent;

namespace GcoresLite.Infrastructure.Logging;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>
/// 极简异步文件日志：写日志本身绝不能拖慢 UI，也不能因为磁盘问题抛异常。
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 8192);
    private readonly Task _worker;
    private readonly string _dir;

    public FileLog(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        PruneOldLogs(keepDays: 7);
        _worker = Task.Factory.StartNew(Pump, TaskCreationOptions.LongRunning);
    }

    /// <summary>日志按天写文件，只保留最近 N 天，避免长年累积。</summary>
    private void PruneOldLogs(int keepDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var file in new DirectoryInfo(_dir).EnumerateFiles("app-*.log"))
            {
                if (file.LastWriteTime < cutoff) file.Delete();
            }
        }
        catch
        {
            // 清理失败不影响启动
        }
    }

    private static string Stamp => DateTime.Now.ToString("HH:mm:ss.fff");

    public void Info(string message) => Enqueue($"[{Stamp}] INFO  {message}");
    public void Warn(string message) => Enqueue($"[{Stamp}] WARN  {message}");

    public void Error(string message, Exception? ex = null) =>
        Enqueue($"[{Stamp}] ERROR {message}{(ex is null ? string.Empty : Environment.NewLine + ex)}");

    private void Enqueue(string line)
    {
        if (_queue.IsAddingCompleted) return;
        _queue.TryAdd(line);
    }

    private void Pump()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // 日志写失败就丢弃，不能反过来影响主流程
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _queue.Dispose();
    }
}
