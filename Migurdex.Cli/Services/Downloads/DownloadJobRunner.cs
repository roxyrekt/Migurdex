using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services.Downloads;

public sealed record DownloadFilterSpec(
    List<string> Groups,
    List<string> Hosters,
    List<string> Qualities,
    List<string> Formats)
{
    public static readonly DownloadFilterSpec None = new([], [], [], []);
}

public static class DownloadJobRunner
{
    public static async Task RunEpisodeAsync(
        IApiClientService         api,
        IDownloadService          downloadService,
        BackgroundDownloadTracker tracker,
        CliConfig                 config,
        string                    provider,
        string                    animeTitle,
        Episode                   episode,
        int                       season,
        DownloadFilterSpec        filter,
        string                    outputDirectory,
        bool                      downloadSubtitles,
        bool                      overwrite,
        bool                      resume,
        int                       connections,
        JobControl                control)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(downloadService);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(control);

        var job = control.Job;

        try
        {
            var (onDisk, _) = ResumeProgressProbe.Probe(
                outputDirectory,
                animeTitle,
                episode.Title,
                episode.Season ?? season,
                episode.Number);
            job.ResumeBaselineBytes = onDisk;
        }
        catch
        {
            // ignored
        }

        string? group = null;
        if (filter.Groups.Count == 1)
        {
            try
            {
                var groups = await api.GetEpisodeGroupsAsync(provider, episode.Id, control.Token)
                                      .ConfigureAwait(false);
                if (groups.IsSuccess
                    && MediaSelection.TryResolveGroup(groups.Data, filter.Groups[0], out var resolvedGroup))
                {
                    group = resolvedGroup;
                }
            }
            catch
            {
                // ignored
            }
        }

        var maxRetries = Math.Clamp(config.DownloadMaxRetries, 1, 10);
        var baseDelay  = TimeSpan.FromSeconds(Math.Clamp(config.DownloadRetryBaseDelaySeconds, 0, 60));
        for (var attempt = 1;; attempt++)
        {
            try
            {
                var resolved = await DownloadCandidateResolver.ResolveAsync(
                                                                  api,
                                                                  provider,
                                                                  episode.Id,
                                                                  group,
                                                                  DownloadSourceFormat.Auto,
                                                                  config,
                                                                  config.DownloadAutoSelectTimeoutSeconds,
                                                                  control.Token)
                                                              .ConfigureAwait(false);
                if (resolved.TimedOut)
                {
                    job.Error =
                        $"Kaynak taraması zaman aşımına uğradı ({config.DownloadAutoSelectTimeoutSeconds:F1} sn).";
                    throw new DownloadException(job.Error);
                }

                if (resolved.Error is not null)
                {
                    job.Error = resolved.Error;
                    throw new DownloadException(job.Error);
                }

                var candidates = resolved.Candidates
                                         .Where(s => MatchesAny(s.Group, filter.Groups)
                                                     && MatchesAny(s.Hoster, filter.Hosters)
                                                     && MatchesAny(s.Quality, filter.Qualities)
                                                     && (filter.Formats.Count == 0
                                                         || filter.Formats.Any(f => s.Type.ToString()
                                                                                   .Equals(f,
                                                                                       StringComparison
                                                                                           .OrdinalIgnoreCase))))
                                         .ToList();
                if (candidates.Count == 0)
                {
                    job.Error = "Uygun kaynak bulunamadı.";
                    throw new DownloadException(job.Error);
                }

                var hosterKey = candidates[0].Hoster ?? "unknown";
                await tracker.HosterGate(hosterKey).WaitAsync(control.Token).ConfigureAwait(false);
                try
                {
                    string? lastError = null;
                    foreach (var source in candidates)
                    {
                        control.Token.ThrowIfCancellationRequested();
                        var result = await downloadService.DownloadAsync(new DownloadRequest
                                                                         {
                                                                             Source          = source,
                                                                             OutputDirectory = outputDirectory,
                                                                             AnimeTitle      = animeTitle,
                                                                             EpisodeTitle    = episode.Title,
                                                                             Season =
                                                                                 episode.Season ?? season,
                                                                             EpisodeNumber     = episode.Number,
                                                                             Overwrite         = overwrite,
                                                                             Resume            = resume,
                                                                             DownloadSubtitles = downloadSubtitles,
                                                                             ConnectionsPerFile =
                                                                                 Math.Clamp(connections, 1, 8),
                                                                             Progress = control.Progress
                                                                         },
                                                                         control.Token)
                                                          .ConfigureAwait(false);
                        if (result.Success)
                        {
                            job.MediaPath = result.MediaPath;
                            job.Percent   = 100;
                            job.Detail    = "tamamlandı";
                            return;
                        }

                        lastError = result.Error;
                    }

                    job.Error = lastError ?? "Video indirilemedi.";
                    throw new DownloadException(job.Error);
                }
                finally
                {
                    tracker.HosterGate(hosterKey).Release();
                }
            }
            catch (DownloadException ex) when (attempt < maxRetries && DownloadRetryPolicy.IsTransient(ex.Message))
            {
                job.Error = ex.Message;
                await Task.Delay(DownloadRetryPolicy.BackoffDelay(attempt, baseDelay), control.Token)
                          .ConfigureAwait(false);
            }
        }
    }

    private static bool MatchesAny(string? value, List<string> filters)
    {
        if (filters.Count == 0 || string.IsNullOrWhiteSpace(value))
        {
            return filters.Count == 0;
        }

        return filters.Any(f => value.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}
