namespace GcoresLite.Services.Api;

/// <summary>
/// 所有接口地址与 include/fields 参数集中在这里。
/// 机核接口非官方，一旦变形只需要改这一个文件。
/// </summary>
public static class ApiEndpoints
{
    public const string BaseUrl = "https://www.gcores.com/";
    public const string AudioOssPrefix = "https://alioss.gcores.com/uploads/audio/";
    public const string ImageBase = "https://image.gcores.com/";
    public const string Referer = "https://www.gcores.com/";

    public const string FieldsRadios =
        "title,duration,cover,thumb,published-at,is-published,is-verified,likes-count," +
        "comments-count,category,albums,djs,plays,owner-type,is-require-privilege,is-free";

    public const string FieldsCategories = "name,logo,subscriptions-count";
    public const string FieldsAlbums = "title,cover,is-require-privilege,content-type,is-free,owner-type";

    /// <summary>播单列表：不带 description（动辄几百字，列表用不上）。</summary>
    public const string FieldsAlbumsList =
        "title,cover,radios-count,owner-type,is-free,is-require-privilege,display-type,updated-at";

    public const string FieldsAlbumDetail = FieldsAlbumsList + ",description";

    public const string FieldsUsers = "nickname,thumb";

    public const string PathLatestRadios = "gapi/v1/latest-radios";
    public const string PathRadios = "gapi/v1/radios";
    public const string PathAlbums = "gapi/v1/albums";

    public static string Radio(string id) => $"gapi/v1/radios/{id}";
    public static string Album(string id) => $"gapi/v1/albums/{id}";
    public static string AlbumRadios(string albumId) => $"gapi/v1/albums/{albumId}/published-radios";
}
