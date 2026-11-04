using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GcoresLite.Infrastructure;

/// <summary>
/// 让列表的滚轮滚动变成平滑动画。
///
/// WPF 默认每次滚一格就瞬间跳 48 像素，眼睛看到的是"一卡一卡"。
/// 这里把滚轮输入累积成一个目标偏移，再在每一帧里朝目标逼近一小步，
/// 视觉上就是连续滑动了。逼近系数越小越"软"，越大越跟手。
/// </summary>
public static class SmoothScroll
{
    /// <summary>滚轮一格的位移（WPF 默认一格约 3 行 = 48px，稍放大一点更跟手）。</summary>
    private const double StepPerNotch = 56;

    /// <summary>
    /// 一次滚动走完的时长。按真实时间算进度，不受帧率影响
    /// （之前用"每帧乘系数"的写法，帧越密跑得越快，实测 3 帧就到位了）。
    /// </summary>
    private const double DurationMs = 200;

    /// <summary>小于这个距离就直接吸附到目标，避免永远差一点点。</summary>
    private const double SnapThreshold = 0.4;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
            element.Unloaded += OnUnloaded;
        }
        else
        {
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
            element.Unloaded -= OnUnloaded;
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        var viewer = FindScrollViewer(element);
        if (viewer is not null) ReleaseState(viewer);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject element) return;

        var viewer = FindScrollViewer(element);
        if (viewer is null || viewer.ScrollableHeight <= 0) return;

        // 触控板会给出非常小的 delta，交给原生的像素滚动更自然
        if (Math.Abs(e.Delta) < 40) return;

        var smoother = GetOrCreateState(viewer);
        if (smoother is null) return;

        e.Handled = true;
        smoother.Nudge(-e.Delta / 120.0 * StepPerNotch);
    }

    // ---------- 每个 ScrollViewer 一个平滑器 ----------

    private static readonly DependencyProperty SmootherProperty =
        DependencyProperty.RegisterAttached(
            "Smoother", typeof(ScrollSmoother), typeof(SmoothScroll), new PropertyMetadata(null));

    private static ScrollSmoother? GetOrCreateState(ScrollViewer viewer)
    {
        if (viewer.GetValue(SmootherProperty) is ScrollSmoother existing) return existing;

        var smoother = new ScrollSmoother(viewer);
        viewer.SetValue(SmootherProperty, smoother);
        return smoother;
    }

    private static void ReleaseState(ScrollViewer viewer)
    {
        if (viewer.GetValue(SmootherProperty) is ScrollSmoother smoother)
        {
            smoother.Dispose();
            viewer.ClearValue(SmootherProperty);
        }
    }

    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer direct) return direct;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }

        return null;
    }

    /// <summary>把离散的滚轮目标，按时间缓出成连续位移。</summary>
    private sealed class ScrollSmoother : IDisposable
    {
        private readonly ScrollViewer _viewer;
        private double _target;
        private double _current;
        private double _startOffset;
        private double _elapsedMs;
        private DateTime _lastFrame;
        private bool _running;
        private bool _disposed;

        public ScrollSmoother(ScrollViewer viewer)
        {
            _viewer = viewer;
            _current = viewer.VerticalOffset;
            _target = _current;
            _startOffset = _current;
            _viewer.ScrollChanged += OnScrollChanged;
        }

        /// <summary>外部滚动（拖滚动条、代码跳转）时同步状态，避免动画抢控制权。</summary>
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_running) return;

            _current = _viewer.VerticalOffset;
            _target = _current;
            _startOffset = _current;
        }

        public void Nudge(double delta)
        {
            if (_disposed) return;

            // 连滚时从当前位置接续，不要在旧起点上重算（会突然跳一下）
            _current = _viewer.VerticalOffset;
            _startOffset = _current;
            _elapsedMs = 0;

            _target = Math.Clamp(_target + delta, 0, _viewer.ScrollableHeight);

            if (Math.Abs(_target - _current) < SnapThreshold) return;

            if (!_running)
            {
                _running = true;
                _lastFrame = DateTime.UtcNow;
                CompositionTarget.Rendering += OnFrame;
            }
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            if (_disposed) return;

            // 用户按住滚动条拖动时，动画让位
            if (_viewer.IsMouseCaptureWithin)
            {
                Stop();
                return;
            }

            var now = DateTime.UtcNow;
            _elapsedMs += (now - _lastFrame).TotalMilliseconds;
            _lastFrame = now;

            var t = Math.Min(1.0, _elapsedMs / DurationMs);

            // easeOutCubic：起步快、收尾稳，接近触屏惯性
            var eased = 1 - Math.Pow(1 - t, 3);

            _current = _startOffset + (_target - _startOffset) * eased;
            _viewer.ScrollToVerticalOffset(_current);

            if (t >= 1.0) Stop();
        }

        private void Stop()
        {
            if (!_running) return;

            _running = false;
            CompositionTarget.Rendering -= OnFrame;
            _current = _viewer.VerticalOffset;
            _target = _current;
            _startOffset = _current;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            Stop();
            _viewer.ScrollChanged -= OnScrollChanged;
        }
    }
}
