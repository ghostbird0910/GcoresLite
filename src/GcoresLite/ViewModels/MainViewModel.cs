using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GcoresLite.Infrastructure.Logging;
using GcoresLite.Models;
using GcoresLite.Services.Api;
using GcoresLite.Services.Cache;
using GcoresLite.Services.Media;
using GcoresLite.Services.Playlists;
using GcoresLite.Services.Settings;
using GcoresLite.Services.Theming;

namespace GcoresLite.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int PageSize = 20;

    private readonly GcoresApiClient _api;
    private readonly ImageCache _images;
    private readonly ILog _log;
    private readonly ThemeService _theme;
    private readonly SettingsService _settings;

    private int _offset;
    private bool _hasMore = true;
    private int _hiddenCount;

    private int _albumOffset;
    private bool _albumHasMore = true;
    private int _albumHiddenCount;

    private int _albumRadioOffset;
    private bool _albumRadioHasMore = true;
    private int _albumRadioHidden;

    public MainViewModel(
        GcoresApiClient api,
        ImageCache images,
        ILog log,
        PlaybackService playback,
        PlaylistService playlists,
        ThemeService theme,
        SettingsService settings)
    {
        _api = api;
        _images = images;
        _log = log;
        Playback = playback;
        Playlists = playlists;
        _theme = theme;
        _settings = settings;

        // 以 ThemeService 的实际状态为准（App 在构造 VM 之前已经 Apply 过主题）
        _isDarkTheme = theme.IsDark;
    }

    public PlaybackService Playback { get; }
    public PlaylistService Playlists { get; }

    public ObservableCollection<RadioItemViewModel> Items { get; } = new();
    public ObservableCollection<RadioItemViewModel> PlaylistItems { get; } = new();
    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();
    public ObservableCollection<RadioItemViewModel> AlbumRadios { get; } = new();

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _activeTab = "latest";

    // list / playlists / playlist / radio / albums / albumdetail
    [ObservableProperty] private string _viewMode = "list";

    [ObservableProperty] private Playlist? _activePlaylist;
    [ObservableProperty] private AlbumSummary? _activeAlbum;
    [ObservableProperty] private bool _showPlaylistPicker;
    [ObservableProperty] private RadioItemViewModel? _pendingAdd;

    /// <summary>非空表示「整张播单加入」模式，选择播单时走批量。</summary>
    [ObservableProperty] private List<RadioSummary>? _pendingAlbumBatch;

    /// <summary>浮层副标题：单条显示节目名，批量显示播单名 + 期数。</summary>
    public string PickerSubtitle => PendingAlbumBatch is { Count: > 0 } batch
        ? $"{ActiveAlbum?.Title ?? "整张播单"} · {batch.Count} 期"
        : PendingAdd?.Title ?? string.Empty;

    partial void OnPendingAddChanged(RadioItemViewModel? value) =>
        OnPropertyChanged(nameof(PickerSubtitle));

    partial void OnPendingAlbumBatchChanged(List<RadioSummary>? value) =>
        OnPropertyChanged(nameof(PickerSubtitle));
    [ObservableProperty] private string _newPlaylistName = string.Empty;
    [ObservableProperty] private bool _isDarkTheme;

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        _theme.Apply(IsDarkTheme);

        _settings.Current.DarkTheme = IsDarkTheme;
        _settings.Save();

        _log.Info($"theme -> {(IsDarkTheme ? "dark" : "light")}");
    }

    public bool ShowRadioList => ViewMode == "list";
    public bool ShowPlaylists => ViewMode == "playlists";
    public bool ShowPlaylistDetail => ViewMode == "playlist";
    public bool ShowRadioView => ViewMode == "radio";
    public bool ShowAlbumList => ViewMode == "albums";
    public bool ShowAlbumDetail => ViewMode == "albumdetail";

    partial void OnViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowRadioList));
        OnPropertyChanged(nameof(ShowPlaylists));
        OnPropertyChanged(nameof(ShowPlaylistDetail));
        OnPropertyChanged(nameof(ShowRadioView));
        OnPropertyChanged(nameof(ShowAlbumList));
        OnPropertyChanged(nameof(ShowAlbumDetail));
    }

    // ---------- 分类列表 ----------

    [RelayCommand]
    private async Task SelectTabAsync(string? tab)
    {
        if (string.IsNullOrEmpty(tab)) return;

        if (tab == ActiveTab)
        {
            // 「播单」下面有二级页，重复点击时回到「我的播单」
            if (tab == "playlists" && ViewMode != "playlists")
            {
                ViewMode = "playlists";
                await Playlists.RefreshAsync().ConfigureAwait(true);
            }
            return;
        }

        ActiveTab = tab;

        switch (tab)
        {
            case "playlists":
                ViewMode = "playlists";
                await Playlists.RefreshAsync().ConfigureAwait(true);
                return;

            case "radio":
                ViewMode = "radio";
                Playback.IsRadioMode = true;
                if (!Playback.HasTrack || Playback.CurrentRadioId is null)
                    await Playback.StartRadioAsync().ConfigureAwait(true);
                return;

            case "albums":
                ViewMode = "albums";
                Playback.IsRadioMode = false;
                Albums.Clear();
                _albumOffset = 0;
                _albumHasMore = true;
                _albumHiddenCount = 0;
                StatusText = string.Empty;
                await LoadMoreAlbumsAsync().ConfigureAwait(true);
                return;
        }

        ViewMode = "list";
        Playback.IsRadioMode = false;

        Items.Clear();
        _offset = 0;
        _hasMore = true;
        _hiddenCount = 0;
        StatusText = string.Empty;

        await LoadMoreAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (IsLoading || !_hasMore || ViewMode != "list") return;

        IsLoading = true;
        StatusText = "加载中…";

        try
        {
            var addedAny = false;

            // 会员专享节目不展示，于是某一页可能整页都被过滤掉，
            // 这里最多连续补 3 页，避免用户滚到底看到空白。
            for (var attempt = 0; attempt < 3 && !addedAny; attempt++)
            {
                if (attempt > 0 && !_hasMore) break;

                var page = await FetchPageAsync().ConfigureAwait(true);
                _offset += PageSize;

                _hasMore = page.Items.Count >= PageSize && (page.Total <= 0 || _offset < page.Total);

                foreach (var radio in page.Items.Where(r => r.IsPlayable))
                {
                    var vm = new RadioItemViewModel(radio, _images);
                    Items.Add(vm);
                    _ = vm.LoadCoverAsync();
                    addedAny = true;
                }

                _hiddenCount += page.Items.Count(r => !r.IsPlayable);
            }

            var hidden = _hiddenCount > 0 ? $"，已隐藏 {_hiddenCount} 期会员专享" : string.Empty;

            StatusText = _hasMore
                ? $"已加载 {Items.Count} 期{hidden}"
                : Items.Count == 0 ? "这里暂时没有可播放的内容" : $"共 {Items.Count} 期 · 到底了{hidden}";

            _log.Info($"list loaded tab={ActiveTab} count={Items.Count} hidden={_hiddenCount} hasMore={_hasMore}");
        }
        catch (Exception ex)
        {
            _log.Error($"加载列表失败 tab={ActiveTab}", ex);
            StatusText = "加载失败，检查网络后重试";
            _hasMore = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Task<PagedResult<RadioSummary>> FetchPageAsync() => ActiveTab switch
    {
        "gcores" => _api.GetLatestAsync(_offset, PageSize, gcoresOnly: true),
        "podcast" => _api.GetJoinPodcastsAsync(_offset, PageSize),
        _ => _api.GetLatestAsync(_offset, PageSize, gcoresOnly: false)
    };

    [RelayCommand]
    private async Task PlayItemAsync(RadioItemViewModel? item)
    {
        if (item is null) return;

        try
        {
            var source = ViewMode switch
            {
                "playlist" => PlaylistItems,
                "albumdetail" => AlbumRadios,
                _ => Items
            };
            var queue = source.Select(x => x.Model).ToList();
            var index = source.IndexOf(item);

            // 从列表点播就退出电台模式，避免播完自动跳走
            Playback.IsRadioMode = ViewMode == "radio";

            await Playback.PlayAsync(item.Model, queue, index).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("播放命令失败", ex);
        }
    }

    // ---------- 机核播单 ----------

    [RelayCommand]
    private async Task ShowMyPlaylistsAsync()
    {
        if (ViewMode == "playlists") return;

        ViewMode = "playlists";
        ActiveTab = "playlists";
        await Playlists.RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ShowGcoresAlbumsAsync()
    {
        if (ViewMode == "albums") return;

        ActiveTab = "playlists";
        ViewMode = "albums";
        Playback.IsRadioMode = false;

        // 已经拉过就复用，不用每次重来
        if (Albums.Count > 0) return;

        _albumOffset = 0;
        _albumHasMore = true;
        _albumHiddenCount = 0;
        StatusText = string.Empty;

        await LoadMoreAlbumsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task LoadMoreAlbumsAsync()
    {
        if (IsLoading || !_albumHasMore || ViewMode != "albums") return;

        IsLoading = true;
        StatusText = "加载中…";

        try
        {
            var addedAny = false;

            // 付费有声书是成片出现的，某一页可能全是它们，需要连续补页
            for (var attempt = 0; attempt < 3 && !addedAny; attempt++)
            {
                if (attempt > 0 && !_albumHasMore) break;

                var page = await _api.GetAlbumsAsync(_albumOffset, PageSize).ConfigureAwait(true);
                _albumOffset += PageSize;

                _albumHasMore = page.Items.Count >= PageSize && (page.Total <= 0 || _albumOffset < page.Total);

                foreach (var album in page.Items.Where(a => a.IsPlayable))
                {
                    var vm = new AlbumItemViewModel(album, _images);
                    Albums.Add(vm);
                    _ = vm.LoadCoverAsync();
                    addedAny = true;
                }

                _albumHiddenCount += page.Items.Count(a => !a.IsPlayable);
            }

            var hidden = _albumHiddenCount > 0 ? $"，已隐藏 {_albumHiddenCount} 个付费播单" : string.Empty;

            StatusText = _albumHasMore
                ? $"已加载 {Albums.Count} 个播单{hidden}"
                : Albums.Count == 0 ? "这里暂时没有可播放的播单" : $"共 {Albums.Count} 个播单{hidden}";

            _log.Info($"albums loaded count={Albums.Count} hidden={_albumHiddenCount} hasMore={_albumHasMore}");
        }
        catch (Exception ex)
        {
            _log.Error("加载播单列表失败", ex);
            StatusText = "加载失败，检查网络后重试";
            _albumHasMore = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task OpenAlbumAsync(AlbumItemViewModel? item)
    {
        if (item is null) return;

        ActiveAlbum = item.Model;
        AlbumRadios.Clear();

        _albumRadioOffset = 0;
        _albumRadioHasMore = true;
        _albumRadioHidden = 0;

        ViewMode = "albumdetail";
        StatusText = "加载中…";

        await LoadMoreAlbumRadiosAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task LoadMoreAlbumRadiosAsync()
    {
        if (IsLoading || !_albumRadioHasMore || ActiveAlbum is null) return;

        IsLoading = true;

        try
        {
            const int pageSize = 50;
            var page = await _api.GetAlbumRadiosAsync(ActiveAlbum.Id, _albumRadioOffset, pageSize).ConfigureAwait(true);

            _albumRadioOffset += pageSize;
            _albumRadioHasMore = page.Items.Count >= pageSize && (page.Total <= 0 || _albumRadioOffset < page.Total);

            foreach (var radio in page.Items.Where(r => r.IsPlayable))
            {
                var vm = new RadioItemViewModel(radio, _images);
                AlbumRadios.Add(vm);
                _ = vm.LoadCoverAsync();
            }

            _albumRadioHidden += page.Items.Count(r => !r.IsPlayable);

            var hidden = _albumRadioHidden > 0 ? $"，已隐藏 {_albumRadioHidden} 期会员专享" : string.Empty;
            StatusText = AlbumRadios.Count == 0 && !_albumRadioHasMore
                ? "这个播单暂时没有可播放的节目"
                : $"共 {AlbumRadios.Count} 期{hidden}";

            _log.Info($"album {ActiveAlbum.Id} radios={AlbumRadios.Count} hidden={_albumRadioHidden} hasMore={_albumRadioHasMore}");
        }
        catch (Exception ex)
        {
            _log.Error($"加载播单节目失败 {ActiveAlbum?.Id}", ex);
            StatusText = "加载失败，检查网络后重试";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task PlayAlbumAsync()
    {
        if (AlbumRadios.Count == 0) return;

        Playback.IsRadioMode = false;
        await Playback.PlayAsync(AlbumRadios[0].Model,
            AlbumRadios.Select(x => x.Model).ToList(), 0).ConfigureAwait(true);
    }

    /// <summary>整张播单加入我的播单。会先把没翻完的期数补齐，避免只加一半。</summary>
    [RelayCommand]
    private async Task AddAlbumToPlaylistAsync()
    {
        if (ActiveAlbum is null) return;

        var guard = 0;
        while (_albumRadioHasMore && guard++ < 10)
            await LoadMoreAlbumRadiosAsync().ConfigureAwait(true);

        if (AlbumRadios.Count == 0)
        {
            StatusText = "这个播单没有可播放的节目";
            return;
        }

        PendingAdd = null;
        PendingAlbumBatch = AlbumRadios.Select(x => x.Model).ToList();

        ShowPlaylistPicker = true;
        _ = Playlists.RefreshAsync();
    }

    [RelayCommand]
    private void CloseAlbum()
    {
        ViewMode = "albums";
        ActiveAlbum = null;
        AlbumRadios.Clear();
        StatusText = string.Empty;
    }

    // ---------- 播单 ----------

    [RelayCommand]
    private void AddToPlaylist(RadioItemViewModel? item)
    {
        if (item is null) return;

        PendingAdd = item;
        ShowPlaylistPicker = true;
        _ = Playlists.RefreshAsync();
    }

    [RelayCommand]
    private void ClosePicker()
    {
        ShowPlaylistPicker = false;
        PendingAdd = null;
        PendingAlbumBatch = null;
    }

    [RelayCommand]
    private async Task PickPlaylistAsync(Playlist? playlist)
    {
        if (playlist is null) return;

        // 整张播单加入
        if (PendingAlbumBatch is { Count: > 0 } batch)
        {
            var added = await Playlists.AddManyAsync(playlist.Id, batch).ConfigureAwait(true);

            StatusText = added switch
            {
                < 0 => "加入失败，详见日志",
                0 => "这些节目已经在播单里了",
                _ when added == batch.Count => $"已把整张 {added} 期加入「{playlist.Name}」",
                _ => $"已加入 {added} 期（其余 {batch.Count - added} 期原本就在）"
            };

            PendingAlbumBatch = null;
            ShowPlaylistPicker = false;
            return;
        }

        if (PendingAdd is null) return;

        var ok = await Playlists.AddAsync(playlist.Id, PendingAdd.Model).ConfigureAwait(true);
        StatusText = ok ? $"已加入「{playlist.Name}」" : "这期已经在播单里了";

        ShowPlaylistPicker = false;
        PendingAdd = null;
    }

    /// <summary>新建播单；如果是从「加入播单」浮层进来的，建完顺手加进去。</summary>
    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        var name = NewPlaylistName.Trim();
        if (name.Length == 0) return;

        var created = await Playlists.CreateAsync(name).ConfigureAwait(true);
        if (created is null) return;

        NewPlaylistName = string.Empty;

        if (PendingAlbumBatch is { Count: > 0 } albumBatch)
        {
            var count = await Playlists.AddManyAsync(created.Id, albumBatch).ConfigureAwait(true);
            StatusText = $"已新建「{created.Name}」并加入 {count} 期";
            PendingAlbumBatch = null;
            ShowPlaylistPicker = false;
        }
        else if (PendingAdd is not null)
        {
            await Playlists.AddAsync(created.Id, PendingAdd.Model).ConfigureAwait(true);
            StatusText = $"已新建「{created.Name}」并加入";
            PendingAdd = null;
            ShowPlaylistPicker = false;
        }
        else
        {
            StatusText = $"已新建「{created.Name}」";
        }
    }

    [RelayCommand]
    private async Task OpenPlaylistAsync(Playlist? playlist)
    {
        if (playlist is null) return;

        ActivePlaylist = playlist;
        PlaylistItems.Clear();

        var items = await Playlists.GetItemsAsync(playlist.Id).ConfigureAwait(true);
        foreach (var radio in items)
        {
            var vm = new RadioItemViewModel(radio, _images);
            PlaylistItems.Add(vm);
            _ = vm.LoadCoverAsync();
        }

        ViewMode = "playlist";
        StatusText = items.Count == 0 ? "这个播单还是空的" : $"共 {items.Count} 期";
    }

    [RelayCommand]
    private void ClosePlaylist()
    {
        ViewMode = "playlists";
        ActivePlaylist = null;
        PlaylistItems.Clear();
        StatusText = string.Empty;
    }

    [RelayCommand]
    private async Task PlayPlaylistAsync()
    {
        if (PlaylistItems.Count == 0) return;

        Playback.IsRadioMode = false;
        await Playback.PlayAsync(PlaylistItems[0].Model,
            PlaylistItems.Select(x => x.Model).ToList(), 0).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync(Playlist? playlist)
    {
        if (playlist is null) return;

        await Playlists.DeleteAsync(playlist).ConfigureAwait(true);
        StatusText = $"已删除「{playlist.Name}」";
    }

    [RelayCommand]
    private async Task RemoveFromPlaylistAsync(RadioItemViewModel? item)
    {
        if (item is null || ActivePlaylist is null) return;

        await Playlists.RemoveAsync(ActivePlaylist.Id, item.Id).ConfigureAwait(true);
        PlaylistItems.Remove(item);
        StatusText = $"已从播单移除，剩 {PlaylistItems.Count} 期";
    }
}
