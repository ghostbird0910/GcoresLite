using System.Windows;

namespace GcoresLite.Services.Theming;

/// <summary>
/// 运行时换肤。靠替换 Application.Resources 里的颜色字典实现，
/// 前提是界面全部用 DynamicResource 引用（StaticResource 换完不会更新）。
/// </summary>
public sealed class ThemeService
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string Pipboy = "pipboy";

    private static readonly Uri LightUri =
        new("pack://application:,,,/Views/Themes/Colors.xaml", UriKind.Absolute);

    private static readonly Uri DarkUri =
        new("pack://application:,,,/Views/Themes/Colors.Dark.xaml", UriKind.Absolute);

    // Pip-Boy：纯黑底 + 荧光绿，辐射终端风格
    private static readonly Uri PipboyUri =
        new("pack://application:,,,/Views/Themes/Colors.Pipboy.xaml", UriKind.Absolute);

    /// <summary>当前主题名（light / dark / pipboy）。</summary>
    public string Current { get; private set; } = Light;

    public bool IsDark => Current != Light;

    public static Uri UriFor(string mode) => mode switch
    {
        Dark => DarkUri,
        Pipboy => PipboyUri,
        _ => LightUri
    };

    /// <summary>浅色 → 深色 → Pip-Boy → 浅色。</summary>
    public static string NextOf(string mode) => mode switch
    {
        Light => Dark,
        Dark => Pipboy,
        _ => Light
    };

    public void Apply(string mode)
    {
        var app = Application.Current;
        if (app is null) return;

        var dictionary = new ResourceDictionary { Source = UriFor(mode) };
        var merged = app.Resources.MergedDictionaries;

        var index = -1;
        for (var i = 0; i < merged.Count; i++)
        {
            var source = merged[i].Source?.OriginalString ?? string.Empty;
            if (source.Contains("Colors", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index >= 0) merged[index] = dictionary;
        else merged.Insert(0, dictionary);

        Current = mode;
    }
}
