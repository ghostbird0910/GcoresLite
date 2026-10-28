using System.Windows;
using System.Windows.Input;

namespace GcoresLite.Views;

public partial class MiniPlayerWindow : Window
{
    public MiniPlayerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event EventHandler? RestoreRequested;
    public event EventHandler? CloseRequested;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        SnapToCorner();
    }

    /// <summary>贴到主屏右下角，留一点边距。</summary>
    private void SnapToCorner()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - Width - 16;
        Top = work.Bottom - Height - 16;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 1) DragMove();
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
