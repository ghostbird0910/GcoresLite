using GcoresLite.Infrastructure.Logging;
using GcoresLite.Services.Api;
using GcoresLite.Services.Cache;
using GcoresLite.Services.Media;
using GcoresLite.Services.Playlists;
using GcoresLite.Services.Radio;
using GcoresLite.Services.Settings;
using GcoresLite.Services.Theming;
using GcoresLite.Services.Windowing;
using GcoresLite.Storage;
using GcoresLite.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace GcoresLite.Infrastructure.Bootstrap;

public static class ServiceRegistration
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddSingleton<ILog>(_ => new FileLog(AppPaths.LogDir));

        // 存储
        services.AddSingleton<Db>();
        services.AddSingleton<PlaylistRepository>();
        services.AddSingleton<ProgressRepository>();

        // 数据
        services.AddSingleton<RadioAssembler>();
        services.AddSingleton<AudioUrlResolver>();
        services.AddSingleton<GcoresApiClient>(sp => new GcoresApiClient(
            GcoresHttp.CreateApiClient(),
            sp.GetRequiredService<RadioAssembler>(),
            sp.GetRequiredService<AudioUrlResolver>(),
            sp.GetRequiredService<ILog>()));

        // 缓存
        services.AddSingleton<ImageCache>(sp => new ImageCache(
            GcoresHttp.CreateMediaClient(),
            sp.GetRequiredService<ILog>()));
        services.AddSingleton<CacheGcService>();

        // 业务
        services.AddSingleton<PlaylistService>();
        services.AddSingleton<RadioService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IAudioPlayer>(sp => new MfAudioPlayer(sp.GetRequiredService<ILog>()));
        services.AddSingleton<PlaybackService>();
        services.AddSingleton<WindowModeService>();

        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}
