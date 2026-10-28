using System.Windows;
using System.Windows.Input;
using GcoresLite.Services.Media;

namespace GcoresLite.Views;

/// <summary>
/// 极小模式：76×76 只显示封面的悬浮方块。
/// 单击 = 播放/暂停，拖动 = 移动，双击 = 回大模式。
/// </summary>
public partial class TinyPlayerWindow : Window
{
    private Point _pressPoint;
    private bool _dragging;

    public TinyPlayerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event EventHandler? ExpandRequested;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 16;
        Top = work.Bottom - Height - 16;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            ExpandRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _pressPoint = e.GetPosition(this);
        _dragging = false;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragging) return;

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _pressPoint.X) < 4 && Math.Abs(current.Y - _pressPoint.Y) < 4) return;

        _dragging = true;
        try { DragMove(); } catch { /* 鼠标已释放等情况忽略 */ }
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 拖动结束后的那一次抬起不算点击
        if (_dragging)
        {
            _dragging = false;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        if (DataContext is PlaybackService playback)
            playback.TogglePlayCommand.Execute(null);
    }
}
