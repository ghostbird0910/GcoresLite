using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GcoresLite.Infrastructure.Logging;
using GcoresLite.Models;
using GcoresLite.Storage;

namespace GcoresLite.Services.Playlists;

/// <summary>自建播单：用户可以任意建、任意加、任意排序，数据全在本地。</summary>
public sealed partial class PlaylistService : ObservableObject
{
    private readonly PlaylistRepository _repo;
    private readonly ILog _log;

    public PlaylistService(PlaylistRepository repo, ILog log)
    {
        _repo = repo;
        _log = log;
    }

    public ObservableCollection<Playlist> Playlists { get; } = new();

    [ObservableProperty] private Playlist? _active;

    public async Task RefreshAsync()
    {
        try
        {
            var items = await _repo.GetAllAsync().ConfigureAwait(true);

            Playlists.Clear();
            foreach (var playlist in items) Playlists.Add(playlist);

            if (Active is { } active)
                Active = Playlists.FirstOrDefault(p => p.Id == active.Id);
        }
        catch (Exception ex)
        {
            _log.Error("加载播单失败", ex);
        }
    }

    public async Task<Playlist?> CreateAsync(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        try
        {
            var id = await _repo.CreateAsync(name).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            return Playlists.FirstOrDefault(p => p.Id == id);
        }
        catch (Exception ex)
        {
            _log.Error("新建播单失败", ex);
            return null;
        }
    }

    public async Task RenameAsync(Playlist playlist, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _repo.RenameAsync(playlist.Id, name).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("重命名播单失败", ex);
        }
    }

    public async Task DeleteAsync(Playlist playlist)
    {
        try
        {
            await _repo.DeleteAsync(playlist.Id).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Error("删除播单失败", ex);
        }
    }

    /// <summary>加入播单；已在其中返回 false。</summary>
    public async Task<bool> AddAsync(string playlistId, RadioSummary radio)
    {
        try
        {
            var added = await _repo.AddAsync(playlistId, radio).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            return added;
        }
        catch (Exception ex)
        {
            _log.Error("加入播单失败", ex);
            return false;
        }
    }

    /// <summary>批量加入（整张播单）。返回实际新增条数；-1 表示失败。</summary>
    public async Task<int> AddManyAsync(string playlistId, IReadOnlyList<RadioSummary> radios)
    {
        try
        {
            var added = await _repo.AddManyAsync(playlistId, radios).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            return added;
        }
        catch (Exception ex)
        {
            _log.Error("批量加入播单失败", ex);
            return -1;
        }
    }

    public Task<List<RadioSummary>> GetItemsAsync(string playlistId) => _repo.GetItemsAsync(playlistId);

    public Task RemoveAsync(string playlistId, string radioId) => _repo.RemoveAsync(playlistId, radioId);

    public Task MoveAsync(string playlistId, string radioId, int delta) => _repo.MoveAsync(playlistId, radioId, delta);
}
