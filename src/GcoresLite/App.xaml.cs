using System.Windows;
using GcoresLite.Infrastructure;
using GcoresLite.Infrastructure.Bootstrap;
using GcoresLite.Infrastructure.Logging;
using GcoresLite.Models;
using GcoresLite.Services.Cache;
using GcoresLite.Services.Settings;
using GcoresLite.Services.Theming;
using GcoresLite.Services.Windowing;
using GcoresLite.ViewModels;
using GcoresLite.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GcoresLite;

public partial class App : Application
{
    private const string MutexName = "GcoresLite.SingleInstance.v1";

    private Mutex? _mutex;
    private ServiceProvider? _services;
    private WindowModeService? _windowModes;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    protected override void OnStartup(StartupEventArgs e)
    {
        // 构建期工具：从图片生成多尺寸 ico，完成后直接退出
        if (TryMakeIcon(e.Args))
        {
            Shutdown(0);
            return;
        }

        // 构建期工具：--roundicon <源图> <输出目录>  生成圆角方形图标 + 预览图
        if (TryMakeRoundIcon(e.Args))
        {
            Shutdown(0);
            return;
        }

        // 构建期工具：--iconsizes <源图> <输出png>  生成真实尺寸对照图
        if (TryMakeSizeComparison(e.Args))
        {
            Shutdown(0);
            return;
        }

        // 构建期工具：--appico <源图> <输出.ico>  生成最终应用图标（含 16/24 简化版）
        if (TryMakeAppIcon(e.Args))
        {
            Shutdown(0);
            return;
        }

        _mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();

        var services = ServiceRegistration.Build();
        _services = services;

        var log = services.GetRequiredService<ILog>();
        DispatcherUnhandledException += (_, args) =>
        {
            // 这里大多来自图片/网络之类的异步回调，弹窗反而更打扰；
            // 统一吞掉并写日志，避免一个孤立异常打断收听。
            log.Error("UI 线程未处理异常", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error("后台线程未处理异常", args.ExceptionObject as Exception);

        log.Info("---- GcoresLite 启动 ----");

        // 主题要在建窗口之前应用，否则首帧会闪一下浅色
        var settings = services.GetRequiredService<SettingsService>();
        settings.Load();

        var theme = services.GetRequiredService<ThemeService>();

        // 自检：--darktest 只影响本次运行，不改用户设置
        var forceDark = e.Args.Any(a => a.Equals("--darktest", StringComparison.OrdinalIgnoreCase));
        theme.Apply(forceDark || settings.Current.DarkTheme);

        var window = new MainWindow { DataContext = services.GetRequiredService<MainViewModel>() };
        MainWindow = window;
        window.Show();

        _windowModes = services.GetRequiredService<WindowModeService>();
        _windowModes.Initialize(window);

        // 开发自检：--playtest 自动播放第一条，用于验证播放内核（正常启动不带此参数）
        if (e.Args.Any(a => a.Equals("--playtest", StringComparison.OrdinalIgnoreCase)))
        {
            var vm = services.GetRequiredService<MainViewModel>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(async () =>
                {
                    await vm.LoadMoreAsync();

                    var first = vm.Items.FirstOrDefault(i => i.IsPlayable);
                    if (first is null)
                    {
                        log.Warn("playtest: 没有可播放的节目");
                        return;
                    }

                    log.Info($"playtest: 开始播放「{first.Title}」");
                    await vm.Playback.PlayAsync(first.Model,
                        vm.Items.Select(i => i.Model).ToList(), vm.Items.IndexOf(first));
                });

                // 等播放状态与封面稳定后，把窗口渲染成 PNG，方便核对界面
                for (var i = 0; i < 3; i++)
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    await Dispatcher.InvokeAsync(() =>
                        log.Info($"playtest t+{(i + 1) * 3}s 位置={vm.Playback.PositionText} " +
                                 $"总长={vm.Playback.DurationText} playing={vm.Playback.IsPlaying}"));
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var path = Path.Combine(AppPaths.Root, "shot.png");
                        CaptureWindow(window, path);
                        log.Info($"playtest: 截图={path}");
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"截图失败：{ex.Message}");
                    }
                });
            });
        }

        // 开发自检：--modetest 依次切三种模式并各截一张图，用于核对布局
        if (e.Args.Any(a => a.Equals("--modetest", StringComparison.OrdinalIgnoreCase)) && _windowModes is { } modes)
        {
            var vm = services.GetRequiredService<MainViewModel>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(async () =>
                {
                    await vm.LoadMoreAsync();
                    var first = vm.Items.FirstOrDefault(i => i.IsPlayable);
                    if (first is not null)
                        await vm.Playback.PlayAsync(first.Model,
                            vm.Items.Select(i => i.Model).ToList(), vm.Items.IndexOf(first));
                });

                await Task.Delay(4500).ConfigureAwait(false);

                foreach (var mode in new[] { WindowMode.Full, WindowMode.Mini, WindowMode.Tiny })
                {
                    await Dispatcher.InvokeAsync(() => modes.SwitchTo(mode));
                    await Task.Delay(900).ConfigureAwait(false);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            if (modes.CurrentWindow is not { IsVisible: true } w) return;
                            var suffix = theme.IsDark ? "-dark" : string.Empty;
                            var path = Path.Combine(AppPaths.Root, $"mode-{mode}{suffix}.png");
                            CaptureWindow(w, path);
                            log.Info($"modetest {mode} -> {path}");
                        }
                        catch (Exception ex)
                        {
                            log.Warn($"modetest 截图失败 {mode}：{ex.Message}");
                        }
                    });
                }
            });
        }

        // 开发自检：--modetest 末尾顺便出一张 4 倍放大的「硬裁剪 vs alpha 蒙版」对比图
        if (e.Args.Any(a => a.Equals("--modetest", StringComparison.OrdinalIgnoreCase)))
        {
            var vm2 = services.GetRequiredService<MainViewModel>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(14000).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (vm2.Playback.Cover is not { } cover) return;
                    try
                    {
                        var path = Path.Combine(AppPaths.Root, "alpha-comparison.png");
                        RenderAlphaComparison(cover, path);
                    }
                    catch (Exception ex)
                    {
                        // 诊断图失败不影响主流程
                    }
                });
            });
        }

        // 自检：--themetest 先截浅色，切换后截深色，验证运行时换肤是否真的生效
        if (e.Args.Any(a => a.Equals("--themetest", StringComparison.OrdinalIgnoreCase)))
        {
            var vmTheme = services.GetRequiredService<MainViewModel>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(3500).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    try { CaptureWindow(window, Path.Combine(AppPaths.Root, "theme-before.png")); }
                    catch (Exception ex) { log.Warn($"浅色截图失败：{ex.Message}"); }
                });

                await Dispatcher.InvokeAsync(() => vmTheme.ToggleThemeCommand.Execute(null));
                await Task.Delay(1200).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        CaptureWindow(window, Path.Combine(AppPaths.Root, "theme-after.png"));
                        log.Info("themetest 完成");
                    }
                    catch (Exception ex) { log.Warn($"深色截图失败：{ex.Message}"); }
                });
            });
        }

        // 自检：--shot 截真实桌面（含窗口四角），用来确认圆角在系统层面是否被裁掉
        if (e.Args.Any(a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var suffix = theme.IsDark ? "-dark" : "-light";
                        var path = Path.Combine(AppPaths.Root, $"desktop{suffix}.png");
                        CaptureDesktop(path);
                        log.Info($"桌面截图 -> {path}");
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"桌面截图失败：{ex.Message}");
                    }
                });
            });
        }

        // 自检：--tinyshot 切到极小模式，把鼠标移到窗口中心，截悬停态
        if (e.Args.Any(a => a.Equals("--tinyshot", StringComparison.OrdinalIgnoreCase)) && _windowModes is { } wm)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => wm.SwitchTo(WindowMode.Tiny));
                await Task.Delay(900).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (wm.CurrentWindow is not { } w) return;
                    var center = w.PointToScreen(new Point(w.ActualWidth / 2, w.ActualHeight / 2));
                    SetCursorPos((int)center.X, (int)center.Y);
                });

                await Task.Delay(900).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (wm.CurrentWindow is not { } w) return;
                        var path = Path.Combine(AppPaths.Root, "tiny-hover.png");
                        CaptureWindowAround(w, path);
                        log.Info($"tinyshot -> {path}");
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"tinyshot 截图失败：{ex.Message}");
                    }
                });
            });
        }

        // 自检：--showcase <输出png> 生成一张带圆角与背景的展示图（给 README 用）
        if (e.Args.Any(a => a.Equals("--showcase", StringComparison.OrdinalIgnoreCase)))
        {
            var outIndex = Array.FindIndex(e.Args, a => a.Equals("--showcase", StringComparison.OrdinalIgnoreCase)) + 1;
            var outPath = e.Args.Length > outIndex ? e.Args[outIndex] : Path.Combine(AppPaths.Root, "showcase.png");

            var vmShow = services.GetRequiredService<MainViewModel>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(async () =>
                {
                    await vmShow.LoadMoreAsync();
                    var first = vmShow.Items.FirstOrDefault(i => i.IsPlayable);
                    if (first is not null)
                        await vmShow.Playback.PlayAsync(first.Model,
                            vmShow.Items.Select(i => i.Model).ToList(), vmShow.Items.IndexOf(first));
                });

                await Task.Delay(5000).ConfigureAwait(false);

                // 把鼠标挪开，避免列表项残留 hover 底色
                await Dispatcher.InvokeAsync(() => SetCursorPos(0, 0));
                await Task.Delay(400).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        RenderShowcase(window, outPath);
                        log.Info($"showcase -> {outPath}");
                    }
                    catch (Exception ex)
                    {
                        log.Warn($"showcase 生成失败：{ex.Message}");
                    }
                });
            });
        }

        // 自检：--albumtest 进机核播单，截列表和详情
        if (e.Args.Any(a => a.Equals("--albumtest", StringComparison.OrdinalIgnoreCase)))
        {
            var vmA = services.GetRequiredService<MainViewModel>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(async () =>
                    await vmA.ShowGcoresAlbumsCommand.ExecuteAsync(null).ConfigureAwait(true));

                await Task.Delay(3500).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        CaptureWindow(window, Path.Combine(AppPaths.Root, "album-list.png"));
                        log.Info($"albumtest 列表 共 {vmA.Albums.Count} 个播单");
                    }
                    catch (Exception ex) { log.Warn($"album 列表截图失败：{ex.Message}"); }
                });

                await Dispatcher.InvokeAsync(() =>
                {
                    if (vmA.Albums.FirstOrDefault() is { } first)
                        vmA.OpenAlbumCommand.Execute(first);
                });

                await Task.Delay(3500).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        CaptureWindow(window, Path.Combine(AppPaths.Root, "album-detail.png"));
                        log.Info($"albumtest 详情 {vmA.ActiveAlbum?.Title} 共 {vmA.AlbumRadios.Count} 期");
                    }
                    catch (Exception ex) { log.Warn($"album 详情截图失败：{ex.Message}"); }
                });
            });
        }

        // 自检：--albumaddtest 整张播单加入的端到端验证
        if (e.Args.Any(a => a.Equals("--albumaddtest", StringComparison.OrdinalIgnoreCase)))
        {
            var vmB = services.GetRequiredService<MainViewModel>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() => vmB.ShowGcoresAlbumsCommand.Execute(null));
                await Task.Delay(3500).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (vmB.Albums.FirstOrDefault() is { } first) vmB.OpenAlbumCommand.Execute(first);
                });
                await Task.Delay(3500).ConfigureAwait(false);

                string? playlistId = null;

                // 先把已有播单刷出来，否则每次都会新建一个，测不到「同一播单去重」
                await Dispatcher.InvokeAsync(() => { _ = vmB.Playlists.RefreshAsync(); });
                await Task.Delay(900).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (vmB.Playlists.Playlists.FirstOrDefault() is { } existing)
                        playlistId = existing.Id;
                });

                if (playlistId is null)
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        var created = await vmB.Playlists.CreateAsync("整张加入测试").ConfigureAwait(true);
                        playlistId = created?.Id;
                    });
                    await Task.Delay(600).ConfigureAwait(false);
                }

                var albumCount = 0;
                await Dispatcher.InvokeAsync(() =>
                {
                    albumCount = vmB.AlbumRadios.Count;
                    vmB.AddAlbumToPlaylistCommand.Execute(null);
                });
                await Task.Delay(900).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    var target = vmB.Playlists.Playlists.FirstOrDefault(p => p.Id == playlistId);
                    if (target is not null) vmB.PickPlaylistCommand.Execute(target);
                });
                await Task.Delay(1800).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(async () =>
                {
                    var items = playlistId is null
                        ? new List<RadioSummary>()
                        : await vmB.Playlists.GetItemsAsync(playlistId).ConfigureAwait(true);

                    log.Info($"albumaddtest 专辑期数={albumCount} 播单现有={items.Count} 提示={vmB.StatusText}");
                });
            });
        }

        // 启动后异步跑一次磁盘回收，不阻塞界面
        var gc = services.GetRequiredService<CacheGcService>();
        _ = Task.Run(async () =>
        {
            try { await gc.RunAsync(force: true).ConfigureAwait(false); }
            catch (Exception ex) { log.Warn($"启动缓存回收失败：{ex.Message}"); }
        });

        base.OnStartup(e);
    }

    /// <summary>
    /// 把窗口渲染到一张带背景与圆角的画布上。
    /// 因为 RenderTargetBitmap 不包含 DWM 的窗口圆角，这里自己裁一次，得到和实际观感一致的图。
    /// </summary>
    private static void RenderShowcase(Window window, string path)
    {
        window.UpdateLayout();

        const double scale = 2.0;
        const int pad = 56;
        const double radius = 10 * scale;

        var width = (int)Math.Ceiling(window.ActualWidth * scale);
        var height = (int)Math.Ceiling(window.ActualHeight * scale);

        var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        shot.Render(window);

        var totalWidth = width + pad * 2;
        var totalHeight = height + pad * 2;

        var visual = new System.Windows.Media.DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var background = new System.Windows.Media.LinearGradientBrush(
                System.Windows.Media.Color.FromRgb(0xF7, 0xF3, 0xEF),
                System.Windows.Media.Color.FromRgb(0xEA, 0xE4, 0xDD),
                90);
            dc.DrawRectangle(background, null, new Rect(0, 0, totalWidth, totalHeight));

            // 柔和的落地感：几层递减透明度的偏移圆角矩形
            for (var i = 4; i >= 1; i--)
            {
                var offset = 2 + i * 3;
                var alpha = 0.035;
                dc.DrawRoundedRectangle(
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb((byte)(alpha * 255), 0x3A, 0x2A, 0x1E)),
                    null,
                    new Rect(pad - offset / 2.0, pad + offset, width + offset, height + offset),
                    radius + offset / 2.0, radius + offset / 2.0);
            }

            var clip = new System.Windows.Media.RectangleGeometry(
                new Rect(pad, pad, width, height), radius, radius);

            dc.PushClip(clip);
            dc.DrawImage(shot, new Rect(pad, pad, width, height));
            dc.Pop();

            dc.DrawRoundedRectangle(null,
                new System.Windows.Media.Pen(
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(0x28, 0, 0, 0)), 1),
                new Rect(pad, pad, width, height), radius, radius);
        }

        var result = new System.Windows.Media.Imaging.RenderTargetBitmap(
            totalWidth, totalHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        result.Render(visual);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(result));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>截窗口周边区域（含四角与背景），用于验证系统层面的圆角/透明行为。</summary>
    private static void CaptureDesktop(string path) =>
        CaptureWindowAround(Application.Current.MainWindow ?? throw new InvalidOperationException("no window"), path);

    private static void CaptureWindowAround(Window window, string path)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);

        // PointToScreen 直接给设备像素，比用 Left/Top（DIP）换算可靠
        var topLeft = window.PointToScreen(new Point(0, 0));
        var bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));

        var pad = (int)(28 * dpi.DpiScaleX);
        var x = (int)topLeft.X - pad;
        var y = (int)topLeft.Y - pad;
        var width = (int)(bottomRight.X - topLeft.X) + pad * 2;
        var height = (int)(bottomRight.Y - topLeft.Y) + pad * 2;

        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
        }

        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>
    /// 诊断用：左边用几何裁剪（硬边、锯齿），右边用 alpha 蒙版（平滑），
    /// 4 倍像素渲染放大边缘细节。
    /// </summary>
    private static void RenderAlphaComparison(System.Windows.Media.Imaging.BitmapSource source, string path)
    {
        const int cell = 72;
        const int gap = 24;
        const double radius = 16;
        const int scale = 4;

        var totalWidth = cell * 2 + gap * 3;
        var totalHeight = cell + gap * 2;

        var visual = new System.Windows.Media.DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null,
                new Rect(0, 0, totalWidth, totalHeight));

            // 左：几何裁剪
            dc.PushClip(new System.Windows.Media.RectangleGeometry(
                new Rect(gap, gap, cell, cell), radius, radius));
            dc.DrawImage(source, new Rect(gap, gap, cell, cell));
            dc.Pop();

            // 右：alpha 蒙版
            var maskGeometry = new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, cell, cell), radius, radius);

            var maskBrush = new System.Windows.Media.DrawingBrush(
                new System.Windows.Media.GeometryDrawing(
                    System.Windows.Media.Brushes.Black, null, maskGeometry))
            {
                Viewbox = new Rect(0, 0, cell, cell),
                ViewboxUnits = System.Windows.Media.BrushMappingMode.Absolute,
                Stretch = System.Windows.Media.Stretch.Fill
            };

            dc.PushOpacityMask(maskBrush);
            dc.DrawImage(source, new Rect(gap * 2 + cell, gap, cell, cell));
            dc.Pop();
        }

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            totalWidth * scale, totalHeight * scale, 96 * scale, 96 * scale,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// 原图（826×831）中字母 G 的像素边界，由连通域分析测得（位置 98,297 尺寸 144×200）。
    /// 16/24px 只保留这一块，否则 "G.FM" 缩下去会糊成一团。
    /// </summary>
    private static readonly Int32Rect GlyphCrop = new(98, 297, 144, 200);

    private const double IconRadiusRatio = 0.225;    // 圆角半径 / 边长（iOS 比例）
    private const double IconGreenStrokeRatio = 0.04573; // 38px / 831px，与 "G.FM" 中间的句点等宽
    private const double IconBlackStrokeRatio = 0.00481; // ≈4px，压住绿色的外扩

    private static readonly System.Windows.Media.Color IconGreen =
        System.Windows.Media.Color.FromRgb(0x26, 0xE4, 0x76);

    /// <summary>构建期工具：--appico &lt;源图&gt; &lt;输出.ico&gt;</summary>
    private static bool TryMakeAppIcon(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--appico", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || args.Length < index + 3) return false;

        try
        {
            IconBuilder.BuildIco(
                args[index + 1], args[index + 2],
                IconRadiusRatio, IconGreenStrokeRatio, IconGreen,
                IconBlackStrokeRatio, System.Windows.Media.Colors.Black,
                GlyphCrop);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"生成应用图标失败：{ex.Message}", "机核播客");
        }

        return true;
    }

    /// <summary>构建期工具：--iconsizes &lt;源图&gt; &lt;输出png&gt;</summary>
    private static bool TryMakeSizeComparison(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--iconsizes", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || args.Length < index + 3) return false;

        try
        {
            IconBuilder.BuildSizeComparison(
                args[index + 1],
                args[index + 2],
                radiusRatio: IconRadiusRatio,
                strokeRatio: IconGreenStrokeRatio,
                strokeColor: IconGreen,
                outerStrokeRatio: IconBlackStrokeRatio,
                outerStrokeColor: System.Windows.Media.Colors.Black,
                glyphCrop: GlyphCrop);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"生成尺寸对照失败：{ex.Message}", "机核播客");
        }

        return true;
    }

    /// <summary>构建期工具：--roundicon &lt;源图&gt; &lt;输出目录&gt;</summary>
    private static bool TryMakeRoundIcon(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--roundicon", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || args.Length < index + 2) return false;

        try
        {
            var source = args[index + 1];
            var outDir = args.Length > index + 2 ? args[index + 2] : AppPaths.Root;
            Directory.CreateDirectory(outDir);

            // 可选第三个参数：圆角半径占边长的比例，默认 0.225（接近 iOS 图标比例）
            var ratio = 0.225;
            if (args.Length > index + 3 &&
                double.TryParse(args[index + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                parsed is > 0.02 and < 0.5)
            {
                ratio = parsed;
            }

            // 可选第四个参数：描边宽度占边长的比例（0 表示不描边）
            var strokeRatio = 0.0;
            if (args.Length > index + 4 &&
                double.TryParse(args[index + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out var sr) &&
                sr is > 0 and < 0.2)
            {
                strokeRatio = sr;
            }

            // 可选第五个参数：最外层黑色细边的宽度比例
            var outerStrokeRatio = 0.0;
            if (args.Length > index + 5 &&
                double.TryParse(args[index + 5], NumberStyles.Float, CultureInfo.InvariantCulture, out var osr) &&
                osr is > 0 and < 0.05)
            {
                outerStrokeRatio = osr;
            }

            var suffix = $"{(int)Math.Round(ratio * 100)}{(strokeRatio > 0 ? "-stroke" : string.Empty)}" +
                         (outerStrokeRatio > 0 ? $"-outline{(int)Math.Round(outerStrokeRatio * 1000)}" : string.Empty);
            var rounded = Path.Combine(outDir, $"icon-rounded-{suffix}.png");
            var preview = Path.Combine(outDir, $"icon-preview-{suffix}.png");

            var strokeColor = System.Windows.Media.Color.FromRgb(0x26, 0xE4, 0x76);
            var outlineColor = System.Windows.Media.Colors.Black;
            IconBuilder.BuildRoundedIcon(source, rounded, ratio, strokeRatio, strokeColor,
                outerStrokeRatio, outlineColor);
            IconBuilder.BuildPreview(rounded, preview);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"生成图标失败：{ex.Message}", "机核播客");
        }

        return true;
    }

    /// <summary>构建期工具：--makeicon &lt;源图片&gt; &lt;输出.ico&gt;</summary>
    private static bool TryMakeIcon(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--makeicon", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || args.Length < index + 3) return false;

        try
        {
            IconBuilder.Build(args[index + 1], args[index + 2]);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"生成图标失败：{ex.Message}", "机核播客");
        }

        return true;
    }

    /// <summary>把窗口的视觉树渲染成 PNG，用于核对界面（不依赖桌面截屏）。</summary>
    private static void CaptureWindow(Window window, string path)
    {
        window.UpdateLayout();

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));

        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            System.Windows.Media.PixelFormats.Pbgra32);

        target.Render(window);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 先收托盘，再拆容器，避免残留幽灵图标
        try { _windowModes?.Dispose(); } catch { /* 退出阶段不再抛 */ }

        try
        {
            if (_services is { } sp)
            {
                sp.GetService<ILog>()?.Info("---- GcoresLite 退出 ----");
                sp.Dispose();
            }
        }
        catch { /* 退出阶段不再抛 */ }

        try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
