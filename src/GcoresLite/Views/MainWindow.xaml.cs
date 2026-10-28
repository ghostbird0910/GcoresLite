using System.Windows;
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

    private void RadioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (RadioList.SelectedItem is not RadioItemViewModel item) return;

        if (vm.PlayItemCommand.CanExecute(item))
            vm.PlayItemCommand.Execute(item);
    }

    private void PlaylistItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox list || list.SelectedItem is not RadioItemViewModel item) return;

        if (vm.PlayItemCommand.CanExecute(item))
            vm.PlayItemCommand.Execute(item);
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox list) return;
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
        if (e.AddedItems.Count == 0) return;
        if (list.SelectedItem is not AlbumItemViewModel album) return;

        if (vm.OpenAlbumCommand.CanExecute(album))
            vm.OpenAlbumCommand.Execute(album);
    }

    private void AlbumRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not ListBox list) return;
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
