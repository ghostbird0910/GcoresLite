using System.Drawing;
using System.IO;
using System.Windows;

namespace GcoresLite.Services.Windowing;

/// <summary>
/// 托盘图标直接给 System.Drawing.Icon。
///
/// 不能用 H.NotifyIcon 的 ImageSource 通道：它内部会强行读 BitmapImage.UriSource，
/// 而内存流构造的 BitmapImage 那个字段是 null，会抛 ArgumentNullException。
/// </summary>
public static class TrayIconFactory
{
    private static readonly Uri ResourceUri = new("pack://application:,,,/Assets/app.ico", UriKind.Absolute);

    public static Icon CreateIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(ResourceUri);
            if (resource is null) return SystemIcons.Application;

            using var stream = resource.Stream;
            return new Icon(stream);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }
}
