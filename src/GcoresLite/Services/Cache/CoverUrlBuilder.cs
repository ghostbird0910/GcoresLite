using GcoresLite.Services.Api;

namespace GcoresLite.Services.Cache;

/// <summary>
/// 封面 URL 构造。
/// 关键：接口原始地址带 format,webp，WPF 解不了；换成 format,jpg 后实测对 png/jpg 源都返回 image/jpeg，
/// 因此不需要引入 WebP 解码库。
/// </summary>
public static class CoverUrlBuilder
{
    public const int ListWidth = 120;
    public const int PlayerWidth = 313;

    public static string? Build(string? cover, int width = ListWidth)
    {
        if (string.IsNullOrWhiteSpace(cover)) return null;

        if (cover.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return cover;

        return $"{ApiEndpoints.ImageBase}{cover}" +
               $"?x-oss-process=image/resize,limit_1,m_lfit,w_{width}/quality,q_90/format,jpg";
    }

    /// <summary>降级链的第二级：不要 format 参数，取原始格式。</summary>
    public static string? BuildOriginal(string? cover)
    {
        if (string.IsNullOrWhiteSpace(cover)) return null;
        if (cover.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return cover;
        return $"{ApiEndpoints.ImageBase}{cover}?x-oss-process=image/resize,limit_1,m_lfit,w_313";
    }
}
