using System.Windows;
using System.Windows.Controls;
using GcoresLite.Infrastructure.Logging;
using GcoresLite.Models;
using GcoresLite.Services.Media;
using GcoresLite.Views;
using H.NotifyIcon;

namespace GcoresLite.Services.Windowing;

/// <summary>
/// 三档窗口编排 + 托盘。
/// 三个窗口共用同一个 PlaybackService，切换模式只做 Show/Hide，绝不重建 —— 播放不会中断。
/// </summary>
public sealed class WindowModeService : IDisposable
{
    private readonly PlaybackService _playback;
    private readonly ILog _log;

    private MainWindow? _main;
    private MiniPlayerWindow? _mini;
    private TinyPlayerWindow? _tiny;
    private TaskbarIcon? _tray;

    private MenuItem? _fullItem;
    private MenuItem? _miniItem;
    private MenuItem? _tinyItem;
    private MenuItem? _playItem;

    public WindowModeService(PlaybackService playback, ILog log)
    {
        _playback = playback;
        _log = log;
        _playback.PropertyChanged += OnPlaybackPropertyChanged;
    }

    public WindowMode Mode { get; private set; } = WindowMode.Full;

    /// <summary>当前可见的窗口，供截图/调试用。</summary>
    public Window? CurrentWindow => Mode switch
    {
        WindowMode.Full => _main,
        WindowMode.Mini => _mini,
        WindowMode.Tiny => _tiny,
        _ => _main
    };

    public void Initialize(MainWindow main)
    {
        _main = main;
        main.WindowModes = this;

        CreateTray();
        UpdateMenuState();
    }

    public void SwitchTo(WindowMode mode)
    {
        if (_main is null) return;

        if (Mode == mode && IsCurrentlyVisible(mode))
        {
            BringToFront(mode);
            return;
        }

        _main.Hide();
        _mini?.Hide();
        _tiny?.Hide();

        switch (mode)
        {
            case WindowMode.Full:
                if (_main.WindowState == WindowState.Minimized)
                    _main.WindowState = WindowState.Normal;
                _main.Show();
                _main.Activate();
                break;

            case WindowMode.Mini:
                _mini ??= CreateMini();
                _mini.Show();
                _mini.Activate();
                break;

            case WindowMode.Tiny:
                _tiny ??= CreateTiny();
                _tiny.Show();
                _tiny.Activate();
                break;
        }

        Mode = mode;
        UpdateMenuState();
        _log.Info($"window mode -> {mode}");
    }

    /// <summary>托盘左键：大模式与小模式之间来回切；主窗被最小化时直接还原。</summary>
    public void ToggleMain()
    {
        var mainIsUsable = Mode == WindowMode.Full &&
                           _main is { IsVisible: true } &&
                           _main.WindowState != WindowState.Minimized;

        SwitchTo(mainIsUsable ? WindowMode.Mini : WindowMode.Full);
    }

    public void Quit()
    {
        Dispose();
        Application.Current?.Shutdown();
    }

    private MiniPlayerWindow CreateMini()
    {
        var window = new MiniPlayerWindow { DataContext = _playback };
        window.RestoreRequested += (_, _) => SwitchTo(WindowMode.Full);
        window.CloseRequested += (_, _) => Quit();
        return window;
    }

    private TinyPlayerWindow CreateTiny()
    {
        var window = new TinyPlayerWindow { DataContext = _playback };
        window.ExpandRequested += (_, _) => SwitchTo(WindowMode.Full);
        return window;
    }

    private bool IsCurrentlyVisible(WindowMode mode) => mode switch
    {
        // 最小化的窗口虽然 IsVisible=true，但对用户来说等于看不见，要走还原流程
        WindowMode.Full => _main is { IsVisible: true } && _main.WindowState != WindowState.Minimized,
        WindowMode.Mini => _mini?.IsVisible == true,
        WindowMode.Tiny => _tiny?.IsVisible == true,
        _ => false
    };

    private void BringToFront(WindowMode mode)
    {
        switch (mode)
        {
            case WindowMode.Full: _main?.Activate(); break;
            case WindowMode.Mini: _mini?.Activate(); break;
            case WindowMode.Tiny: _tiny?.Activate(); break;
        }
    }

    // ---------- 托盘 ----------

    private void CreateTray()
    {
        try
        {
            _tray = new TaskbarIcon
            {
                ToolTipText = "机核播客",
                Icon = TrayIconFactory.CreateIcon(),
                ContextMenu = CreateMenu()
            };

            _tray.TrayLeftMouseUp += (_, _) => ToggleMain();
            _tray.ForceCreate();

            _log.Info("tray icon created");
        }
        catch (Exception ex)
        {
            _log.Error("创建托盘图标失败（托盘功能不可用，其他功能不受影响）", ex);
        }
    }

    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();

        _fullItem = CreateModeItem("大模式", WindowMode.Full);
        _miniItem = CreateModeItem("小模式", WindowMode.Mini);
        _tinyItem = CreateModeItem("极小模式", WindowMode.Tiny);

        _playItem = new MenuItem { Header = "播放" };
        _playItem.Click += (_, _) => _playback.TogglePlayCommand.Execute(null);

        var nextItem = new MenuItem { Header = "下一首" };
        nextItem.Click += (_, _) => _playback.NextCommand.Execute(null);

        var quitItem = new MenuItem { Header = "退出" };
        quitItem.Click += (_, _) => Quit();

        var openDataItem = new MenuItem { Header = "打开数据目录" };
        openDataItem.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Infrastructure.AppPaths.Root,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _log.Warn($"打开数据目录失败：{ex.Message}");
            }
        };

        menu.Items.Add(_fullItem);
        menu.Items.Add(_miniItem);
        menu.Items.Add(_tinyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_playItem);
        menu.Items.Add(nextItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(openDataItem);
        menu.Items.Add(quitItem);

        // 每次弹出前刷新勾选与播放状态文案
        menu.Opened += (_, _) => UpdateMenuState();

        return menu;
    }

    private MenuItem CreateModeItem(string header, WindowMode mode)
    {
        var item = new MenuItem { Header = header, IsCheckable = true };
        item.Click += (_, _) =>
        {
            SwitchTo(mode);
            UpdateMenuState();
        };
        return item;
    }

    private void UpdateMenuState()
    {
        if (_fullItem is not null) _fullItem.IsChecked = Mode == WindowMode.Full;
        if (_miniItem is not null) _miniItem.IsChecked = Mode == WindowMode.Mini;
        if (_tinyItem is not null) _tinyItem.IsChecked = Mode == WindowMode.Tiny;
        if (_playItem is not null) _playItem.Header = _playback.IsPlaying ? "暂停" : "播放";
    }

    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.IsPlaying))
            UpdateMenuState();
    }

    public void Dispose()
    {
        _playback.PropertyChanged -= OnPlaybackPropertyChanged;

        if (_tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }
    }
}
