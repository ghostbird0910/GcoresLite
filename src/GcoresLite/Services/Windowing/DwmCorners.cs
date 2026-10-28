using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GcoresLite.Services.Windowing;

/// <summary>
/// 让无边框窗口使用 Windows 11 的系统圆角。
///
/// 之前窗口是 AllowsTransparency=False 的不透明窗口，矩形四角由 Window.Background 填满，
/// 外层 Border 的 CornerRadius 只画了一条圆角边框线，四角本身并没有被裁掉 ——
/// 浅色时四角颜色和内部一致看不出来，一旦背后是深色就露出直角方块。
///
/// DWM 裁剪是系统级的，四角真正透明，且不影响 ClearType 文字渲染。
/// Win10 及更早版本没有该属性，调用无效，保持系统原有的直角风格。
/// </summary>
internal static class DwmCorners
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var preference = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // 老系统没有这个属性，忽略
        }
    }
}
