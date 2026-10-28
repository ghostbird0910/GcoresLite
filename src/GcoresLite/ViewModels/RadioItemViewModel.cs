using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GcoresLite.Models;
using GcoresLite.Services.Cache;

namespace GcoresLite.ViewModels;

public sealed partial class RadioItemViewModel : ObservableObject
{
    private readonly ImageCache _images;

    public RadioItemViewModel(RadioSummary model, ImageCache images)
    {
        Model = model;
        _images = images;
    }

    public RadioSummary Model { get; }

    [ObservableProperty] private BitmapImage? _coverImage;
    [ObservableProperty] private bool _isPlaying;

    public string Id => Model.Id;
    public string Title => Model.Title;
    public string DurationText => Model.DurationText;
    public bool IsPrivilege => Model.IsPrivilege;
    public bool IsPlayable => Model.IsPlayable;
    public string? Cover => Model.Cover;

    /// <summary>分类 · 时长 · 日期。DJ 太长会挤掉时长，放进 ToolTip 里。</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>(3);

            // 「会员专享」分类已由会员徽标表达，不再重复
            if (!string.IsNullOrWhiteSpace(Model.Category) && Model.Category != "会员专享")
                parts.Add(Model.Category!);

            if (Model.DurationSeconds > 0) parts.Add(Model.DurationText);
            if (!string.IsNullOrWhiteSpace(Model.PublishText)) parts.Add(Model.PublishText);

            return string.Join(" · ", parts);
        }
    }

    public string ToolTipText
    {
        get
        {
            var bits = new List<string>(3) { Model.Title };
            if (!string.IsNullOrWhiteSpace(Model.AlbumTitle)) bits.Add($"专辑：{Model.AlbumTitle}");
            if (!string.IsNullOrWhiteSpace(Model.DjText)) bits.Add($"主播：{Model.DjText}");
            return string.Join(Environment.NewLine, bits);
        }
    }

    public async Task LoadCoverAsync(CancellationToken ct = default)
    {
        var url = CoverUrlBuilder.Build(Model.Cover, CoverUrlBuilder.ListWidth);
        if (url is null) return;

        var bmp = await _images.GetAsync(url, 96, ct).ConfigureAwait(true);
        if (bmp is not null) CoverImage = bmp;
    }
}
