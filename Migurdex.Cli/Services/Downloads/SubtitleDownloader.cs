using Migurdex.Shared.Models;
using System.Globalization;
using System.Net.Http;
using System.Text;

namespace Migurdex.Cli.Services.Downloads;

public sealed class SubtitleDownloader : ISubtitleDownloader
{
    public const int MaxSubtitleBytes = 10 * 1024 * 1024;

    private const int BufferSize = 64 * 1024;

    private readonly IDownloadHttpClientFactory? _clientFactory;
    private readonly HttpClient?                  _fixedClient;

    public SubtitleDownloader()
        : this(new DownloadHttpClientFactory())
    {
    }

    public SubtitleDownloader(IDownloadHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public SubtitleDownloader(HttpMessageHandler handler)
    {
        _clientFactory = new HttpMessageHandlerDownloadClientFactory(handler);
    }

    public SubtitleDownloader(HttpClient client)
    {
        _fixedClient = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<SubtitleDownloadResult> DownloadAsync(
        Subtitle                              subtitle,
        string                               mediaPath,
        IReadOnlyDictionary<string, string>? sourceHeaders = null,
        bool                                 overwrite       = false,
        int                                  index           = 0,
        IProgress<DownloadProgress>?         progress        = null,
        CancellationToken                     cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        if (string.IsNullOrWhiteSpace(subtitle.Url))
        {
            throw new SubtitleDownloadException("Altyazı URL'si boş.");
        }

        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            throw new ArgumentException("Medya hedef yolu boş olamaz.", nameof(mediaPath));
        }

        var fullMediaPath = Path.GetFullPath(mediaPath);
        var mediaParent   = Path.GetDirectoryName(fullMediaPath)
                            ?? throw new SubtitleDownloadException("Medya hedef dizini geçersiz.");
        Directory.CreateDirectory(mediaParent);

        HttpClient? client = null;
        var disposeClient  = false;
        string? partPath   = null;

        try
        {
            if (subtitle.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var data          = DecodeDataUri(subtitle.Url, out var dataMediaType);
                var dataExtension = ResolveExtension(subtitle.Format, dataMediaType, uri: null);
                ValidateSubtitleContent(data, dataExtension);
                var dataFinalPath = BuildSidecarPath(fullMediaPath,
                                                     subtitle,
                                                     dataExtension,
                                                     index);
                EnsureTargetAvailable(dataFinalPath, overwrite);
                partPath = dataFinalPath + ".part";
                DeleteIfExists(partPath);

                await WriteDataAsync(data, partPath, progress, cancellationToken)
                    .ConfigureAwait(false);
                MoveCompletedPart(partPath, dataFinalPath, overwrite, cancellationToken);
                Report(progress, DownloadStage.Completed, data.Length, data.Length);
                return new SubtitleDownloadResult(dataFinalPath);
            }

            if (!Uri.TryCreate(subtitle.Url, UriKind.Absolute, out var subtitleUri))
            {
                throw new SubtitleDownloadException("Altyazı URL'si geçersiz.");
            }

            DownloadHttp.ValidateHttpUri(subtitleUri);
            var requestHeaders = subtitle.Headers is { Count: > 0 }
                                     ? DownloadHttp.CopyHeaders(subtitle.Headers)
                                     : DownloadHttp.CopySubtitleFallbackHeaders(sourceHeaders,
                                                                                   sourceUri: null,
                                                                                   subtitleUri);
            var clientToUse = _fixedClient ?? _clientFactory!.CreateClient();
            client          = clientToUse;
            disposeClient   = _fixedClient is null;
            Report(progress, DownloadStage.Requesting, 0, null);

            using var response = await DownloadHttp.SendWithRedirectsAsync(clientToUse,
                                                                           subtitleUri,
                                                                           requestHeaders,
                                                                           cancellationToken)
                                       .ConfigureAwait(false);
            if (DownloadHttp.IsHtml(response))
            {
                throw new SubtitleDownloadException("Altyazı kaynağı HTML içerik döndürdü.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SubtitleDownloadException("Altyazı sunucusu indirilebilir bir yanıt vermedi.");
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > MaxSubtitleBytes)
            {
                throw new SubtitleDownloadException("Altyazı dosyası izin verilen boyut sınırını aşıyor.");
            }

            var extension = ResolveExtension(subtitle.Format,
                                             response.Content.Headers.ContentType?.MediaType,
                                             subtitleUri);
            var finalPath = BuildSidecarPath(fullMediaPath, subtitle, extension, index);
            EnsureTargetAvailable(finalPath, overwrite);
            partPath = finalPath + ".part";
            DeleteIfExists(partPath);

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = new FileStream(partPath,
                                                  FileMode.CreateNew,
                                                  FileAccess.Write,
                                                  FileShare.Read,
                                                  BufferSize,
                                                  FileOptions.Asynchronous | FileOptions.SequentialScan);
            long downloaded = 0;
            Report(progress, DownloadStage.Downloading, downloaded, declaredLength);

            var buffer         = new byte[BufferSize];
            var lastReportTime = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await body.ReadAsync(buffer.AsMemory(), cancellationToken)
                                     .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (downloaded + read > MaxSubtitleBytes)
                {
                    throw new SubtitleDownloadException("Altyazı dosyası izin verilen boyut sınırını aşıyor.");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                          .ConfigureAwait(false);
                downloaded += read;
                if (declaredLength is > 0 && downloaded >= declaredLength.Value
                    || System.Diagnostics.Stopwatch.GetElapsedTime(lastReportTime).TotalMilliseconds >= 150)
                {
                    Report(progress, DownloadStage.Downloading, downloaded, declaredLength);
                    lastReportTime = System.Diagnostics.Stopwatch.GetTimestamp();
                }
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (declaredLength.HasValue && downloaded != declaredLength.Value)
            {
                throw new SubtitleDownloadException("Altyazı gövdesi beklenen boyutta değil.");
            }

            await file.DisposeAsync().ConfigureAwait(false);
            ValidateSubtitleFile(partPath, extension);
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, DownloadStage.Finalizing, downloaded, declaredLength);
            MoveCompletedPart(partPath, finalPath, overwrite, cancellationToken);
            Report(progress, DownloadStage.Completed, downloaded, declaredLength);
            return new SubtitleDownloadResult(finalPath);
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(partPath);
            Report(progress, DownloadStage.Cancelled, 0, null);
            throw;
        }
        catch (SubtitleDownloadException)
        {
            DeleteIfExists(partPath);
            throw;
        }
        catch (DownloadException)
        {
            DeleteIfExists(partPath);
            throw;
        }
        catch (HttpRequestException)
        {
            DeleteIfExists(partPath);
            throw new SubtitleDownloadException("Altyazı HTTP isteği başarısız oldu.");
        }
        catch (IOException)
        {
            DeleteIfExists(partPath);
            throw new SubtitleDownloadException("Altyazı dosyası yazılamadı.");
        }
        catch (Exception)
        {
            DeleteIfExists(partPath);
            throw new SubtitleDownloadException("Altyazı indirilemedi.");
        }
        finally
        {
            if (disposeClient)
            {
                client?.Dispose();
            }
        }
    }

    private static async Task WriteDataAsync(
        byte[]                       data,
        string                       partPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken            cancellationToken)
    {
        await using var file = new FileStream(partPath,
                                              FileMode.CreateNew,
                                              FileAccess.Write,
                                              FileShare.Read,
                                              BufferSize,
                                              FileOptions.Asynchronous | FileOptions.SequentialScan);
        Report(progress, DownloadStage.Downloading, 0, data.Length);
        await file.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        await file.DisposeAsync().ConfigureAwait(false);
        Report(progress, DownloadStage.Downloading, data.Length, data.Length);
    }

    private static byte[] DecodeDataUri(string value, out string? mediaType)
    {
        var commaIndex = value.IndexOf(',');
        if (commaIndex < 0)
        {
            throw new SubtitleDownloadException("Altyazı data URI'si geçersiz.");
        }

        var metadata = value[5..commaIndex];
        var payload  = value[(commaIndex + 1)..];
        if (payload.Length > (long)MaxSubtitleBytes * 4)
        {
            throw new SubtitleDownloadException("Altyazı data URI'si izin verilen boyut sınırını aşıyor.");
        }

        var parts    = metadata.Split(';', StringSplitOptions.TrimEntries);
        mediaType     = parts.Length > 0 && parts[0].Contains('/', StringComparison.Ordinal)
                            ? parts[0]
                            : null;
        var isBase64 = parts.Any(part => part.Equals("base64", StringComparison.OrdinalIgnoreCase));

        try
        {
            byte[] data;
            if (isBase64)
            {
                data = Convert.FromBase64String(Uri.UnescapeDataString(payload));
            }
            else
            {
                data = Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }

            if (data.Length > MaxSubtitleBytes)
            {
                throw new SubtitleDownloadException("Altyazı data URI'si izin verilen boyut sınırını aşıyor.");
            }

            return data;
        }
        catch (FormatException)
        {
            throw new SubtitleDownloadException("Altyazı data URI içeriği geçersiz.");
        }
    }

    private static string ResolveExtension(string? format, string? mediaType, Uri? uri)
    {
        var normalizedMediaType = NormalizeMediaType(mediaType);
        ValidateMediaType(normalizedMediaType);

        var formatExtension = MapSubtitleExtension(format);
        var mediaExtension  = MapSubtitleExtension(normalizedMediaType);
        var uriExtension    = uri is null ? null : MapSubtitleExtension(Path.GetExtension(uri.AbsolutePath));

        if (formatExtension is not null
            && mediaExtension is not null
            && !formatExtension.Equals(mediaExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new SubtitleDownloadException("Altyazı biçemi ile sunucu MIME türü uyuşmuyor.");
        }

        return mediaExtension
               ?? formatExtension
               ?? uriExtension
               ?? throw new SubtitleDownloadException("Altyazı MIME türü veya dosya biçimi tanınmıyor.");
    }

    private static string? NormalizeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        return mediaType.Split(';', StringSplitOptions.TrimEntries)[0]
                        .Trim()
                        .ToLowerInvariant();
    }

    private static void ValidateMediaType(string? mediaType)
    {
        if (mediaType is null
            || mediaType is "text/plain" or "application/octet-stream")
        {
            return;
        }

        if (mediaType is "text/html" or "application/xhtml+xml"
            || mediaType is "application/json" or "text/json"
            || mediaType.EndsWith("+json", StringComparison.Ordinal)
            || mediaType is "application/xml" or "text/xml"
            || mediaType.EndsWith("+xml", StringComparison.Ordinal)
            || mediaType.StartsWith("video/", StringComparison.Ordinal)
            || mediaType.StartsWith("audio/", StringComparison.Ordinal)
            || mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            throw new SubtitleDownloadException("Altyazı kaynağı geçersiz bir MIME türü döndürdü.");
        }

        if (MapSubtitleExtension(mediaType) is null)
        {
            throw new SubtitleDownloadException("Altyazı MIME türü desteklenmiyor.");
        }
    }

    private static string? MapSubtitleExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('.'))
        {
            normalized = normalized[1..];
        }

        return normalized.ToLowerInvariant() switch
        {
            "ass" or "ssa" or "text/ass" or "text/x-ssa" or "application/x-ssa"
                or "application/ass" => ".ass",
            "vtt" or "webvtt" or "text/vtt" => ".vtt",
            "srt" or "subrip" or "application/x-subrip" or "application/subrip"
                or "text/srt" => ".srt",
            _                         => null
        };
    }

    private static void ValidateSubtitleFile(string path, string extension)
    {
        var preview = new byte[1024];
        using var stream = new FileStream(path,
                                          FileMode.Open,
                                          FileAccess.Read,
                                          FileShare.Read,
                                          preview.Length,
                                          FileOptions.SequentialScan);
        var read = stream.Read(preview, 0, preview.Length);
        ValidateSubtitleContent(preview.AsSpan(0, read), extension);
    }

    private static void ValidateSubtitleContent(ReadOnlySpan<byte> data, string extension)
    {
        var text = Encoding.UTF8.GetString(data);
        if (text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        var first = text.AsSpan().TrimStart();
        if (first.StartsWith("<") || first.StartsWith("{"))
        {
            throw new SubtitleDownloadException("Altyazı gövdesi HTML veya JSON olarak görünüyor.");
        }

        if (first.StartsWith("[", StringComparison.Ordinal))
        {
            var afterBracket = first[1..].TrimStart();
            if (afterBracket.Length > 0
                && afterBracket[0] is '"' or '\'' or '{' or '[' or ']')
            {
                throw new SubtitleDownloadException("Altyazı gövdesi JSON olarak görünüyor.");
            }
        }

        if (extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase)
            && !text.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            throw new SubtitleDownloadException("WebVTT imzası bulunamadı.");
        }

        if (extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("-->", StringComparison.Ordinal))
        {
            throw new SubtitleDownloadException("SRT zamanlama imzası bulunamadı.");
        }

        if (extension.Equals(".ass", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("[V4+ Styles]", StringComparison.OrdinalIgnoreCase))
        {
            throw new SubtitleDownloadException("ASS başlık imzası bulunamadı.");
        }
    }

    private static string BuildSidecarPath(
        string   mediaPath,
        Subtitle subtitle,
        string   extension,
        int      index)
    {
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "subtitle";
        }

        var suffix = !string.IsNullOrWhiteSpace(subtitle.Language)
                         ? subtitle.Language
                         : subtitle.Label;
        if (!string.IsNullOrWhiteSpace(suffix))
        {
            suffix = DownloadPathBuilder.SanitizeComponent(suffix, "track");
        }

        if (index > 0)
        {
            suffix = string.IsNullOrWhiteSpace(suffix)
                         ? index.ToString(CultureInfo.InvariantCulture)
                         : suffix + "." + index.ToString(CultureInfo.InvariantCulture);
        }

        var sidecarStem = string.IsNullOrWhiteSpace(suffix)
                              ? stem
                              : stem + "." + suffix;
        sidecarStem = DownloadPathBuilder.SanitizeComponent(sidecarStem, "subtitle");
        var fileName = sidecarStem + extension;
        var path     = Path.Combine(Path.GetDirectoryName(mediaPath)!, fileName);
        DownloadPathBuilder.EnsureFullPathBudget(path, DownloadPathBuilder.TemporarySuffixUtf8Bytes);
        return path;
    }

    private static void EnsureTargetAvailable(string finalPath, bool overwrite)
    {
        if (!overwrite && File.Exists(finalPath))
        {
            throw new SubtitleDownloadException("Altyazı hedefi zaten var; overwrite kapalı.");
        }
    }

    private static void MoveCompletedPart(
        string            partPath,
        string            finalPath,
        bool              overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Move(partPath, finalPath, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(finalPath))
        {
            throw new SubtitleDownloadException("Altyazı hedefi zaten var; overwrite kapalı.");
        }
        catch (IOException)
        {
            throw new SubtitleDownloadException("Altyazı dosyası taşınamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new SubtitleDownloadException("Altyazı dosyası taşınamadı.");
        }
    }

    private static void DeleteIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void Report(
        IProgress<DownloadProgress>? progress,
        DownloadStage                 stage,
        long                          bytes,
        long?                         total)
    {
        progress?.Report(new DownloadProgress(stage, bytes, total));
    }
}
