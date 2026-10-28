namespace GcoresLite.Infrastructure;

/// <summary>应用的数据目录约定，全部落在 %LOCALAPPDATA%\GcoresLite 下。</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GcoresLite");

    public static string CacheRoot => Path.Combine(Root, "cache");
    public static string CoverCacheDir => Path.Combine(CacheRoot, "covers");
    public static string AudioCacheDir => Path.Combine(CacheRoot, "audio");
    public static string LogDir => Path.Combine(Root, "logs");
    public static string BackupDir => Path.Combine(Root, "backup");
    public static string DbPath => Path.Combine(Root, "app.db");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheRoot);
        Directory.CreateDirectory(CoverCacheDir);
        Directory.CreateDirectory(AudioCacheDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(BackupDir);
    }
}
