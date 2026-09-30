using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Text.Json;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadCandidateResolverTests
{
    [Fact]
    public async Task Resolve_FiltersDownloadableCandidates()
    {
        var api = new StubApiClient((_, _, _, _) => Task.FromResult(ApiResult<IReadOnlyList<VideoSource>>.Ok(
        [
            new VideoSource { Url = "https://origin.example/a.mp4", Type = VideoType.Mp4, Quality = "1080p" },
            new VideoSource { Url = "https://origin.example/embed", Type = VideoType.Embed, Quality = "2160p" }
        ])));

        var result = await DownloadCandidateResolver.ResolveAsync(api,
                                                                  "P",
                                                                  "ep-1",
                                                                  null,
                                                                  DownloadSourceFormat.Auto,
                                                                  new CliConfig(),
                                                                  5,
                                                                  TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Sources.Count);
        var single = Assert.Single(result.Candidates);
        Assert.Equal("https://origin.example/a.mp4", single.Url);
    }

    [Fact]
    public async Task Resolve_PassesGroupThrough()
    {
        string? seenGroup = "unset";
        var api = new StubApiClient((provider, episodeId, group, _) =>
        {
            seenGroup = group;
            return Task.FromResult(ApiResult<IReadOnlyList<VideoSource>>.Ok([]));
        });

        await DownloadCandidateResolver.ResolveAsync(api,
                                                     "P",
                                                     "ep-1",
                                                     "Fansub",
                                                     DownloadSourceFormat.Auto,
                                                     new CliConfig(),
                                                     5,
                                                     TestContext.Current.CancellationToken);

        Assert.Equal("Fansub", seenGroup);
    }

    [Fact]
    public async Task Resolve_ApiErrorSurfacesMessage()
    {
        var api = new StubApiClient((_, _, _, _) => Task.FromResult(
            ApiResult<IReadOnlyList<VideoSource>>.Fail([], "Sağlayıcı patladı.")));

        var result = await DownloadCandidateResolver.ResolveAsync(api,
                                                                  "P",
                                                                  "ep-1",
                                                                  null,
                                                                  DownloadSourceFormat.Auto,
                                                                  new CliConfig(),
                                                                  5,
                                                                  TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.Equal("Sağlayıcı patladı.", result.Error);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Resolve_TimeoutReturnsEmptyWithoutThrowing()
    {
        var api = new StubApiClient(async (_, _, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ApiResult<IReadOnlyList<VideoSource>>.Ok([]);
        });

        var result = await DownloadCandidateResolver.ResolveAsync(api,
                                                                  "P",
                                                                  "ep-1",
                                                                  null,
                                                                  DownloadSourceFormat.Auto,
                                                                  new CliConfig(),
                                                                  0.05,
                                                                  TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task Resolve_UserCancelPropagates()
    {
        var api = new StubApiClient(async (_, _, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ApiResult<IReadOnlyList<VideoSource>>.Ok([]);
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DownloadCandidateResolver.ResolveAsync(api,
                                                   "P",
                                                   "ep-1",
                                                   null,
                                                   DownloadSourceFormat.Auto,
                                                   new CliConfig(),
                                                   30,
                                                   cts.Token));
    }

    private sealed class StubApiClient(
        Func<string, string, string?, CancellationToken, Task<ApiResult<IReadOnlyList<VideoSource>>>> sources)
        : IApiClientService
    {
        public Task<bool> IsApiOnlineAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> TryStartApiDaemonAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<ApiHealthInfo?> GetApiHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ApiHealthInfo?>(new()
            {
                Status  = "OK",
                Version = "0.0.0"
            });

        public Task<ApiResult<IReadOnlyList<ProviderInfo>>> GetProvidersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult<IReadOnlyList<ProviderInfo>>.Ok([]));

        public Task<ApiResult<IReadOnlyList<SearchResult>>> SearchAnimeAsync(
            string          query,
            string?         provider          = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult<IReadOnlyList<SearchResult>>.Ok([]));

        public IAsyncEnumerable<StreamedSearchResult> SearchAnimeStreamAsync(
            string          query,
            string?         provider          = null,
            CancellationToken cancellationToken = default,
            StreamScanStats?  stats             = null)
            => AsyncEnumerable.Empty<StreamedSearchResult>();

        public Task<ApiResult<AnimeDetails?>> GetAnimeDetailsAsync(
            string            provider,
            string            animeId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult<AnimeDetails?>.Ok(null));

        public Task<ApiResult<IReadOnlyList<string>>> GetEpisodeGroupsAsync(
            string            provider,
            string            episodeId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult<IReadOnlyList<string>>.Ok([]));

        public Task<ApiResult<IReadOnlyList<VideoSource>>> GetVideoSourcesAsync(
            string            provider,
            string            episodeId,
            string?           group             = null,
            CancellationToken cancellationToken = default)
            => sources(provider, episodeId, group, cancellationToken);

        public IAsyncEnumerable<VideoSource> GetVideoSourcesStreamAsync(
            string            provider,
            string            episodeId,
            string?           group             = null,
            CancellationToken cancellationToken = default,
            StreamScanStats?  stats             = null)
            => AsyncEnumerable.Empty<VideoSource>();

        public Task<ApiResult<IReadOnlyList<string>>> GetExtractorsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult<IReadOnlyList<string>>.Ok([]));

        public Task<ApiResult<TrackerResolveResult?>> ResolveTrackerIdAsync(
            string                 provider,
            string                 providerId,
            IReadOnlyList<string>  titles,
            int?                   year   = null,
            ContentFormat?         format = null,
            CancellationToken      cancellationToken = default)
            => Task.FromResult(ApiResult<TrackerResolveResult?>.Ok(null));

        public Task<ApiResult<MediaMetadata?>> LookupTrackerAsync(
            string? anilistId,
            string? malId             = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult<MediaMetadata?>.Ok(null));

        public Task<ApiResult<TrackerEpisodeMapping?>> MapTrackerEpisodeAsync(
            string            provider,
            string            providerId,
            int               season,
            double            episode,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ApiResult<TrackerEpisodeMapping?>.Ok(null));

        public Task<bool> SaveTrackerMappingAsync(
            string            provider,
            string            providerId,
            string            anilistId,
            string?           malId             = null,
            string?           matchedTitle      = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
