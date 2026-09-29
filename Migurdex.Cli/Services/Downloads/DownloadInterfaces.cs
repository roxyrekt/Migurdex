using Migurdex.Shared.Models;
using System.Diagnostics;
using System.Net.Http;

namespace Migurdex.Cli.Services.Downloads;

public interface IDownloadService
{
    Task<DownloadResult> DownloadAsync(
        DownloadRequest   request,
        CancellationToken cancellationToken = default);

    Task<DownloadResult> DownloadAsync(
        VideoSource                  source,
        AnimeDetails                 details,
        Episode                      episode,
        string                       outputDirectory,
        bool                         overwrite           = false,
        bool                         resume              = true,
        IProgress<DownloadProgress>? progress           = null,
        CancellationToken             cancellationToken = default);
}

public interface IDownloadPathBuilder
{
    DownloadPath Build(
        string       rootDirectory,
        string       animeTitle,
        string       episodeTitle,
        int          season,
        double       episodeNumber,
        string?      extension = null);

    DownloadPath Build(
        string       rootDirectory,
        string       animeTitle,
        int          season,
        double       episodeNumber,
        string       episodeTitle,
        string?      extension = null);

    DownloadPath Build(
        string       rootDirectory,
        AnimeDetails details,
        Episode      episode,
        string?      extension = null);

    string BuildAnimeDirectory(string rootDirectory, string animeTitle);

    string BuildFileStem(int season, double episodeNumber, string episodeTitle);

    string BuildFilePath(
        string       rootDirectory,
        string       animeTitle,
        string       episodeTitle,
        int          season,
        double       episodeNumber,
        string?      extension = null);
}

public interface IDownloadHttpClientFactory
{
    HttpClient CreateClient();
}

public interface IMp4Downloader
{
    Task<MediaDownloadResult> DownloadAsync(
        VideoSource                  source,
        string                       destinationPath,
        bool                         overwrite           = false,
        bool                         resume              = true,
        IProgress<DownloadProgress>? progress           = null,
        CancellationToken             cancellationToken = default);
}

public interface ISubtitleDownloader
{
    Task<SubtitleDownloadResult> DownloadAsync(
        Subtitle                              subtitle,
        string                               mediaPath,
        IReadOnlyDictionary<string, string>? sourceHeaders = null,
        bool                                 overwrite       = false,
        int                                  index           = 0,
        IProgress<DownloadProgress>?         progress        = null,
        CancellationToken                     cancellationToken = default);
}

public interface IHlsDownloader
{
    Task<MediaDownloadResult> DownloadAsync(
        VideoSource                  source,
        DownloadPath                 destination,
        bool                         overwrite           = false,
        IProgress<DownloadProgress>? progress           = null,
        CancellationToken             cancellationToken = default);
}

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo             startInfo,
        CancellationToken            cancellationToken = default);

    // Yeni isteğe bağlı aşırı yükleme; mevcut IExternalProcessRunner uygulamaları için
    // varsayılan olarak eski RunAsync çağrısına yönlendirilir.
    Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo             startInfo,
        Action<string>?              onStandardOutput,
        CancellationToken            cancellationToken = default)
    {
        return RunAsync(startInfo, cancellationToken);
    }
}

public class DownloadException : Exception
{
    public DownloadException(string message)
        : base(message)
    {
    }

    public DownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ConcurrentDownloadException : DownloadException
{
    public ConcurrentDownloadException(string message, Exception? innerException = null)
        : base(message, innerException ?? new IOException("Hedef kilidi açılamadı."))
    {
    }
}

public sealed class SubtitleDownloadException : DownloadException
{
    public SubtitleDownloadException(string message)
        : base(message)
    {
    }
}

public sealed class HlsDownloadException : DownloadException
{
    public HlsDownloadException(string message)
        : base(message)
    {
    }
}

public sealed class ExternalProcessStartException : DownloadException
{
    public ExternalProcessStartException(string message)
        : base(message)
    {
    }
}
