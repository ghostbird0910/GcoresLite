using System.Text.Json;
using GcoresLite.Infrastructure;
using GcoresLite.Infrastructure.Logging;

namespace GcoresLite.Services.Settings;

public sealed class AppSettings
{
    /// <summary>light / dark / pipboy。空字符串表示旧版本设置，需要迁移。</summary>
    public string ThemeMode { get; set; } = string.Empty;

    /// <summary>旧字段，只用于迁移老设置文件，新版本不再依此判断。</summary>
    public bool DarkTheme { get; set; }

    /// <summary>把老版本的布尔开关迁移成主题名。</summary>
    public void MigrateTheme()
    {
        if (!string.IsNullOrEmpty(ThemeMode)) return;
        ThemeMode = DarkTheme ? "dark" : "light";
    }
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
                Current.MigrateTheme();
                return;
            }

            var json = File.ReadAllText(FilePath);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            Current.MigrateTheme();
        }
        catch (Exception ex)
        {
            _log.Warn($"读取设置失败，使用默认值：{ex.Message}");
            Current = new AppSettings();
            Current.MigrateTheme();
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
