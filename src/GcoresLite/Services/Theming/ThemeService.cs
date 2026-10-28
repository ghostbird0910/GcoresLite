using System.Windows;

namespace GcoresLite.Services.Theming;

/// <summary>
/// 运行时换肤。靠替换 Application.Resources 里的颜色字典实现，
/// 前提是界面全部用 DynamicResource 引用（StaticResource 换完不会更新）。
/// </summary>
public sealed class ThemeService
{
    private static readonly Uri LightUri =
        new("pack://application:,,,/Views/Themes/Colors.xaml", UriKind.Absolute);

    private static readonly Uri DarkUri =
        new("pack://application:,,,/Views/Themes/Colors.Dark.xaml", UriKind.Absolute);

    public bool IsDark { get; private set; }

    public void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;

        var dictionary = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
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

        IsDark = dark;
    }
}
