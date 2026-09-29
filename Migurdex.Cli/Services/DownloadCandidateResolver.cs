using Migurdex.Cli.Configuration;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

public sealed class DownloadCandidateResult
{
    public required IReadOnlyList<VideoSource> Sources    { get; init; }
    public required List<VideoSource>         Candidates { get; init; }
    public required bool                      TimedOut   { get; init; }
    public          string?                   Error      { get; init; }
}

public static class DownloadCandidateResolver
{
    public static async Task<DownloadCandidateResult> ResolveAsync(
        IApiClientService   api,
        string              provider,
        string              episodeId,
        string?             group,
        DownloadSourceFormat format,
        CliConfig           config,
        double              timeoutSeconds,
        CancellationToken   cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(config);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (double.IsFinite(timeoutSeconds) && timeoutSeconds > 0)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }

        ApiResult<IReadOnlyList<VideoSource>> sourcesResult;
        try
        {
            sourcesResult = await api.GetVideoSourcesAsync(provider, episodeId, group, timeout.Token)
                                     .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            return new DownloadCandidateResult
            {
                Sources    = [],
                Candidates = [],
                TimedOut   = true
            };
        }

        if (!sourcesResult.IsSuccess)
        {
            return new DownloadCandidateResult
            {
                Sources    = sourcesResult.Data,
                Candidates = [],
                TimedOut   = false,
                Error      = sourcesResult.Error
            };
        }

        return new DownloadCandidateResult
        {
            Sources    = sourcesResult.Data,
            Candidates = DownloadSourceResolver.SelectCandidates(sourcesResult.Data, format, config),
            TimedOut   = false
        };
    }
}
