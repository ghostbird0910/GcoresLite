using System.Text.Json;
using GcoresLite.Infrastructure;
using GcoresLite.Infrastructure.Logging;

namespace GcoresLite.Services.Settings;

public sealed class AppSettings
{
    public bool DarkTheme { get; set; }
}

/// <summary>轻量设置存储。只有一个开关，用 JSON 文件比开一张表更省事。</summary>
public sealed class SettingsService
{
    private static readonly string FilePath = Path.Combine(AppPaths.Root, "settings.json");

    private readonly ILog _log;

    public SettingsService(ILog log) => _log = log;

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Current = new AppSettings();
                return;
            }

            var json = File.ReadAllText(FilePath);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _log.Warn($"读取设置失败，使用默认值：{ex.Message}");
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current));
        }
        catch (Exception ex)
        {
            _log.Warn($"保存设置失败：{ex.Message}");
        }
    }
}
