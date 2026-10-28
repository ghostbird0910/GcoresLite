using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GcoresLite.Models;
using GcoresLite.Services.Cache;

namespace GcoresLite.ViewModels;

public sealed partial class AlbumItemViewModel : ObservableObject
{
    private readonly ImageCache _images;

    public AlbumItemViewModel(AlbumSummary model, ImageCache images)
    {
        Model = model;
        _images = images;
    }

    public AlbumSummary Model { get; }

    public string Id => Model.Id;
    public string Title => Model.Title;
    public string MetaText => Model.MetaText;
    public string? Cover => Model.Cover;

    [ObservableProperty] private BitmapImage? _coverImage;

    public async Task LoadCoverAsync(CancellationToken ct = default)
    {
        var url = CoverUrlBuilder.Build(Model.Cover, CoverUrlBuilder.ListWidth);
        if (url is null) return;

        var bmp = await _images.GetAsync(url, 96, ct).ConfigureAwait(true);
        if (bmp is not null) CoverImage = bmp;
    }
}
