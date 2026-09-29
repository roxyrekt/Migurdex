using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Net.Http;

namespace Migurdex.Cli.Services.Downloads;

public sealed class DownloadService : IDownloadService
{
    private readonly IDownloadPathBuilder _pathBuilder;
    private readonly IMp4Downloader        _mp4Downloader;
    private readonly IHlsDownloader        _hlsDownloader;
    private readonly ISubtitleDownloader   _subtitleDownloader;

    public DownloadService(
        IDownloadPathBuilder pathBuilder,
        IMp4Downloader        mp4Downloader,
        IHlsDownloader        hlsDownloader,
        ISubtitleDownloader   subtitleDownloader)
    {
        _pathBuilder      = pathBuilder ?? throw new ArgumentNullException(nameof(pathBuilder));
        _mp4Downloader    = mp4Downloader ?? throw new ArgumentNullException(nameof(mp4Downloader));
        _hlsDownloader    = hlsDownloader ?? throw new ArgumentNullException(nameof(hlsDownloader));
        _subtitleDownloader = subtitleDownloader ?? throw new ArgumentNullException(nameof(subtitleDownloader));
    }

    public DownloadService()
        : this(new DownloadPathBuilder(),
               new Mp4Downloader(),
               new YtDlpHlsDownloader(new ExternalProcessRunner()),
               new SubtitleDownloader())
    {
    }

    public DownloadService(HttpMessageHandler handler, IExternalProcessRunner? processRunner = null)
        : this(new DownloadPathBuilder(),
               new Mp4Downloader(handler),
               new YtDlpHlsDownloader(processRunner ?? new ExternalProcessRunner()),
               new SubtitleDownloader(handler))
    {
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest   request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.Source;
        if (source is null || source.Type == VideoType.Embed)
        {
            return DownloadResult.Failed("Embed kaynakları doğrudan indirilemez.");
        }

        if (source.Type is not (VideoType.Mp4 or VideoType.M3U8))
        {
            return DownloadResult.Failed("Bu video kaynağı indirilebilir değil.");
        }

        Uri? sourceUri = null;
        if (Uri.TryCreate(source.Url, UriKind.Absolute, out var parsedSourceUri))
        {
            sourceUri = parsedSourceUri;
        }

        try
        {
            var extension = source.Type == VideoType.Mp4 ? ".mp4" : null;
            var destination = _pathBuilder.Build(
                request.OutputDirectory,
                request.AnimeTitle,
                request.EpisodeTitle,
                request.Season,
                request.EpisodeNumber,
                extension);
            Directory.CreateDirectory(destination.AnimeDirectory);
            request.Progress?.Report(new DownloadProgress(DownloadStage.Preparing,
                                                          0,
                                                          null));

            var media = source.Type == VideoType.Mp4
                            ? await _mp4Downloader.DownloadAsync(source,
                                                                  destination.MediaPath,
                                                                  request.Overwrite,
                                                                  request.Resume,
                                                                  request.Progress,
                                                                  cancellationToken)
                            : await _hlsDownloader.DownloadAsync(source,
                                                                  destination,
                                                                  request.Overwrite,
                                                                  request.Progress,
                                                                  cancellationToken);

            if (string.IsNullOrWhiteSpace(media.OutputPath) || !File.Exists(media.OutputPath))
            {
                request.Progress?.Report(new DownloadProgress(DownloadStage.Failed, 0, null));
                return DownloadResult.Failed("Video medya dosyası oluşturulamadı.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                request.Progress?.Report(new DownloadProgress(DownloadStage.Cancelled, 0, null));
                return new DownloadResult
                {
                    Success     = true,
                    IsCancelled = true,
                    MediaPath   = media.OutputPath,
                    Error       = "İndirme iptal edildi."
                };
            }

            var subtitlePaths = new List<string>();
            var warnings       = new List<string>();
            warnings.AddRange(media.Warnings);

            using var subtitleLock = await TryAcquireSubtitleLockAsync(destination,
                                                                        cancellationToken)
                                             .ConfigureAwait(false);
            if (subtitleLock is null)
            {
                warnings.Add("Altyazılar için hedef kilidi alınamadı; altyazılar atlandı.");
                return DownloadResult.Successful(media.OutputPath, subtitlePaths, warnings);
            }

            try
            {
                await DownloadSubtitlesAsync(request,
                                             media.OutputPath,
                                             subtitlePaths,
                                             warnings,
                                             sourceUri,
                                             cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
                                                     && File.Exists(media.OutputPath))
            {
                warnings.Add("Video tamamlandı; altyazı indirme iptal edildi.");
                request.Progress?.Report(new DownloadProgress(DownloadStage.Cancelled, 0, null));
                return new DownloadResult
                {
                    Success       = true,
                    IsCancelled   = true,
                    MediaPath     = media.OutputPath,
                    SubtitlePaths = subtitlePaths.ToArray(),
                    Warnings      = warnings.ToArray(),
                    Error         = "Altyazı indirme iptal edildi."
                };
            }

            return DownloadResult.Successful(media.OutputPath, subtitlePaths, warnings);
        }
        catch (OperationCanceledException)
        {
            request.Progress?.Report(new DownloadProgress(DownloadStage.Cancelled, 0, null));
            throw;
        }
        catch (DownloadException exception)
        {
            request.Progress?.Report(new DownloadProgress(DownloadStage.Failed, 0, null));
            return DownloadResult.Failed(SafeFailureMessage(source, exception));
        }
        catch
        {
            request.Progress?.Report(new DownloadProgress(DownloadStage.Failed, 0, null));
            return DownloadResult.Failed("Video indirilemedi.");
        }
    }

    public Task<DownloadResult> DownloadAsync(
        VideoSource                  source,
        AnimeDetails                 details,
        Episode                      episode,
        string                       outputDirectory,
        bool                         overwrite           = false,
        bool                         resume              = true,
        IProgress<DownloadProgress>? progress           = null,
        CancellationToken             cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(episode);

        var request = new DownloadRequest
        {
            Source          = source,
            OutputDirectory = outputDirectory,
            AnimeTitle      = details.Title,
            EpisodeTitle    = episode.Title,
            Season          = episode.Season ?? 1,
            EpisodeNumber   = episode.Number,
            Overwrite       = overwrite,
            Resume          = resume,
            Progress        = progress
        };

        return DownloadAsync(request, cancellationToken);
    }

    public Task<DownloadResult> DownloadAsync(
        VideoSource                  source,
        string                       outputDirectory,
        string                       animeTitle,
        string                       episodeTitle,
        int                          season,
        double                       episodeNumber,
        bool                         overwrite           = false,
        bool                         resume              = true,
        IProgress<DownloadProgress>? progress           = null,
        CancellationToken             cancellationToken = default)
    {
        var request = new DownloadRequest
        {
            Source          = source,
            OutputDirectory = outputDirectory,
            AnimeTitle      = animeTitle,
            EpisodeTitle    = episodeTitle,
            Season          = season,
            EpisodeNumber   = episodeNumber,
            Overwrite       = overwrite,
            Resume          = resume,
            Progress        = progress
        };

        return DownloadAsync(request, cancellationToken);
    }

    private static string SafeFailureMessage(VideoSource source, DownloadException exception)
    {
        var message = exception.Message;
        if ((!string.IsNullOrWhiteSpace(source.Url)
             && message.Contains(source.Url, StringComparison.Ordinal))
            || (source.Headers?.Values.Any(value => !string.IsNullOrEmpty(value)
                                                   && message.Contains(value, StringComparison.Ordinal)) == true))
        {
            return "Video indirilemedi.";
        }

        return string.IsNullOrWhiteSpace(message) ? "Video indirilemedi." : message;
    }

    private static async Task<IDisposable?> TryAcquireSubtitleLockAsync(
        DownloadPath      destination,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadTargetLock.AcquireAsync(
                                 Path.Combine(destination.AnimeDirectory, destination.FileStem),
                                 cancellationToken)
                                 .ConfigureAwait(false);
        }
        catch (ConcurrentDownloadException)
        {
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException
                                               or IOException
                                               or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task DownloadSubtitlesAsync(
        DownloadRequest  request,
        string           mediaPath,
        ICollection<string> subtitlePaths,
        ICollection<string> warnings,
        Uri?                 sourceUri,
        CancellationToken     cancellationToken)
    {
        if (!request.DownloadSubtitles || request.Source.Subtitles is not { Count: > 0 })
        {
            return;
        }

        request.Progress?.Report(new DownloadProgress(DownloadStage.Subtitle, 0, null));
        var usedSuffixes  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var anonymousIndex = 0;
        foreach (var subtitle in request.Source.Subtitles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtitle is null || string.IsNullOrWhiteSpace(subtitle.Url))
            {
                warnings.Add("Altyazı atlandı: URL yok.");
                continue;
            }

            var suffix = !string.IsNullOrWhiteSpace(subtitle.Language)
                             ? subtitle.Language
                             : subtitle.Label ?? string.Empty;
            var ordinal = 0;
            if (!string.IsNullOrWhiteSpace(suffix))
            {
                usedSuffixes.TryGetValue(suffix, out ordinal);
                usedSuffixes[suffix] = ordinal + 1;
            }
            else
            {
                ordinal = anonymousIndex++;
            }

            try
            {
                Uri? subtitleUri = null;
                if (Uri.TryCreate(subtitle.Url, UriKind.Absolute, out var parsedSubtitleUri)
                    && (parsedSubtitleUri.Scheme.Equals(Uri.UriSchemeHttp,
                                                         StringComparison.OrdinalIgnoreCase)
                        || parsedSubtitleUri.Scheme.Equals(Uri.UriSchemeHttps,
                                                           StringComparison.OrdinalIgnoreCase)))
                {
                    subtitleUri = parsedSubtitleUri;
                }

                var fallbackHeaders = subtitleUri is null
                                          ? null
                                          : DownloadHttp.CopySubtitleFallbackHeaders(request.Source.Headers,
                                                                                    sourceUri,
                                                                                    subtitleUri);
                var subtitleResult = await _subtitleDownloader.DownloadAsync(
                    subtitle,
                    mediaPath,
                    fallbackHeaders,
                    request.Overwrite,
                    ordinal,
                    request.Progress,
                    cancellationToken)
                                      .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(subtitleResult.OutputPath)
                    && File.Exists(subtitleResult.OutputPath))
                {
                    subtitlePaths.Add(subtitleResult.OutputPath);
                }
                else
                {
                    warnings.Add("Altyazı indirilemedi.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                warnings.Add("Altyazı indirilemedi.");
            }

        }
    }
}
