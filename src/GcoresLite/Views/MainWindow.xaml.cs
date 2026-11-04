using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GcoresLite.Models;
using GcoresLite.Services.Windowing;
using GcoresLite.ViewModels;

namespace GcoresLite.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        // 窗口句柄就绪后交给 DWM 做圆角裁剪（Win11），四角才是真的透明
        SourceInitialized += (_, _) => DwmCorners.Apply(this);
    }

    /// <summary>由 App 在启动时注入，用于三档窗口切换。</summary>
    public WindowModeService? WindowModes { get; set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // Slider 内部是 Thumb，冒泡的拖拽事件比挂 MouseUp 可靠
        SeekBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnSeekStarted));
        SeekBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSeekCompleted));

        _ = LoadFirstPageAsync();
    }

    private async Task LoadFirstPageAsync()
    {
        if (DataContext is MainViewModel vm)
            await vm.LoadMoreAsync();
    }

    // ---------- 窗口控制 ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 竖窄条最大化没有意义，双击只做拖动
        if (e.ClickCount == 1) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Mini_Click(object sender, RoutedEventArgs e) => WindowModes?.SwitchTo(WindowMode.Mini);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 列表交互 ----------

    /// <summary>滚动到距底部 320px 就预取下一页。</summary>
    private void RadioList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 320) return;

        if (DataContext is MainViewModel vm && vm.LoadMoreCommand.CanExecute(null))
            vm.LoadMoreCommand.Execute(null);
    }

    /// <summary>
    /// 右键按下的标记。ListBox 右键也会把项选中，而播放/打开是挂在 SelectionChanged 上的，
    /// 不拦一下就会出现"右键弹出菜单的同时节目也开播了"。
    /// </summary>
    private bool _rightClickGuard;

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        _rightClickGuard = true;

        // 用 Background 优先级解除，保证排在本次输入引发的 SelectionChanged 之后
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _rightClickGuard = false));

        base.OnPreviewMouseRightButtonDown(e);
    }

    /// <summary>
    /// 右键引起的选中要撤销：既不该播放，也不该留高亮。
    /// 留着高亮的话，用户接着左键点这一项时不会再有 SelectionChanged，点了没反应。
    /// </summary>
    private bool ShouldSkipForRightClick(ListBox list)
    {
        if (!_rightClickGuard) return false;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (list.SelectedItem is not null) list.SelectedItem = null;
        }));

        return true;
    }

    private void RadioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox list) return;
        if (ShouldSkipForRightClick(list)) return;
        if (list.SelectedItem is not RadioItemViewModel item) return;

        if (vm.PlayItemCommand.CanExecute(item))
            vm.PlayItemCommand.Execute(item);
    }

    private void PlaylistItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox list) return;
        if (ShouldSkipForRightClick(list)) return;
        if (list.SelectedItem is not RadioItemViewModel item) return;

        if (vm.PlayItemCommand.CanExecute(item))
            vm.PlayItemCommand.Execute(item);
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox list) return;
        if (ShouldSkipForRightClick(list)) return;
        if (e.AddedItems.Count == 0) return;
        if (list.SelectedItem is not Models.Playlist playlist) return;

        if (vm.OpenPlaylistCommand.CanExecute(playlist))
            vm.OpenPlaylistCommand.Execute(playlist);
    }

    // ---------- 机核播单 ----------

    private void AlbumList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox list) return;
        if (ShouldSkipForRightClick(list)) return;
        if (e.AddedItems.Count == 0) return;
        if (list.SelectedItem is not AlbumItemViewModel album) return;

        if (vm.OpenAlbumCommand.CanExecute(album))
            vm.OpenAlbumCommand.Execute(album);
    }

    private void AlbumRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox list) return;
        if (ShouldSkipForRightClick(list)) return;
        if (e.AddedItems.Count == 0) return;
        if (list.SelectedItem is not RadioItemViewModel item) return;

        if (vm.PlayItemCommand.CanExecute(item))
            vm.PlayItemCommand.Execute(item);
    }

    /// <summary>播单列表滚动到底部前 320px 预取下一页。</summary>
    private void AlbumList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 320) return;

        if (DataContext is MainViewModel vm && vm.LoadMoreAlbumsCommand.CanExecute(null))
            vm.LoadMoreAlbumsCommand.Execute(null);
    }

    /// <summary>播单详情里的节目列表同样要能续翻，否则百期播单只能看到第一页。</summary>
    private void AlbumRadios_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight < e.ExtentHeight - 320) return;

        if (DataContext is MainViewModel vm && vm.LoadMoreAlbumRadiosCommand.CanExecute(null))
            vm.LoadMoreAlbumRadiosCommand.Execute(null);
    }

    private void NewPlaylist_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is not MainViewModel vm) return;

        if (vm.CreatePlaylistCommand.CanExecute(null))
            vm.CreatePlaylistCommand.Execute(null);

        e.Handled = true;
    }

    private void Picker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点遮罩空白处关闭
        if (e.OriginalSource is not System.Windows.Media.Visual) return;
        if (DataContext is MainViewModel vm && vm.ShowPlaylistPicker)
            vm.ClosePickerCommand.Execute(null);
    }

    // ---------- 进度条 ----------

    private void OnSeekStarted(object sender, DragStartedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Playback.BeginSeek();
    }

    private async void OnSeekCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.Playback.EndSeekAsync();
    }
}
