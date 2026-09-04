using Migurdex.Shared.Models;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public sealed class ApiResult<T>
{
    public required T       Data  { get; init; }
    public          string? Error { get; init; }

    public bool IsSuccess => Error is null;

    public static ApiResult<T> Ok(T data)
    {
        return new ApiResult<T>
        {
            Data = data
        };
    }

    public static ApiResult<T> Fail(T fallback, string error)
    {
        return new ApiResult<T>
        {
            Data  = fallback,
            Error = error
        };
    }
}

public interface IApiClientService
{
    Task<bool>                                   IsApiOnlineAsync(CancellationToken       cancellationToken = default);
    Task<bool>                                   TryStartApiDaemonAsync(CancellationToken cancellationToken = default);
    Task<ApiResult<IReadOnlyList<ProviderInfo>>> GetProvidersAsync(CancellationToken      cancellationToken = default);

    Task<ApiResult<IReadOnlyList<SearchResult>>> SearchAnimeAsync(string query,
        string?                                                          provider          = null,
        CancellationToken                                                cancellationToken = default);

    IAsyncEnumerable<StreamedSearchResult> SearchAnimeStreamAsync(string query,
        string?                                                          provider          = null,
        CancellationToken                                                cancellationToken = default,
        StreamScanStats?                                                 stats             = null);

    Task<ApiResult<AnimeDetails?>> GetAnimeDetailsAsync(string provider,
        string                                                 animeId,
        CancellationToken                                      cancellationToken = default);

    Task<ApiResult<IReadOnlyList<string>>> GetEpisodeGroupsAsync(string provider,
        string                                                          episodeId,
        CancellationToken                                               cancellationToken = default);

    Task<ApiResult<IReadOnlyList<VideoSource>>> GetVideoSourcesAsync(string provider,
        string                                                              episodeId,
        string?                                                             group             = null,
        CancellationToken                                                   cancellationToken = default);

    IAsyncEnumerable<VideoSource> GetVideoSourcesStreamAsync(string provider,
        string                                                      episodeId,
        string?                                                     group             = null,
        CancellationToken                                           cancellationToken = default,
        StreamScanStats?                                            stats             = null);

    Task<ApiResult<IReadOnlyList<string>>> GetExtractorsAsync(CancellationToken cancellationToken = default);
}

public class ProviderInfo
{
    public string      Name         { get; set; } = string.Empty;
    public JsonElement Type         { get; set; }
    public string      BaseUrl      { get; set; } = string.Empty;
    public JsonElement Capabilities { get; set; }
}

public class StreamedSearchResult
{
    public string        Provider { get; set; } = string.Empty;
    public string        Status   { get; set; } = string.Empty;
    public SearchResult? Data     { get; set; }
    public string?       Error    { get; set; }
}

public sealed class StreamScanStats
{
    public int Errors;
    public int Received;
}
