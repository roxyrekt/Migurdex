using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Migurdex.Cli.Services.Downloads;

public sealed class Mp4Downloader : IMp4Downloader
{
    private const int BufferSize = 128 * 1024;
    private const long ProgressIntervalMilliseconds = 150;
    private const int MaxResumeAttempts = 2;

    private static readonly Regex ContentRangeRegex = new(
        @"^bytes\s+(\d+)-(\d+)/(\d+|\*)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UnsatisfiedRangeRegex = new(
        @"^bytes\s+\*/(\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IDownloadHttpClientFactory? _clientFactory;
    private readonly HttpClient?                  _fixedClient;

    public Mp4Downloader()
        : this(new DownloadHttpClientFactory())
    {
    }

    public Mp4Downloader(IDownloadHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public Mp4Downloader(HttpMessageHandler handler)
        : this(new HttpMessageHandlerDownloadClientFactory(handler))
    {
    }

    public Mp4Downloader(HttpClient client)
    {
        _fixedClient = client ?? throw new ArgumentNullException(nameof(client));
    }

    public static string GetResumePartPath(string destinationPath, VideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Video hedef yolu boş olamaz.", nameof(destinationPath));
        }

        var headers = DownloadHttp.CopyHeaders(source.Headers);
        headers.Remove("Range");
        var fingerprint = DownloadHttp.CreateSourceFingerprint(source.Url, headers);
        return Path.GetFullPath(destinationPath) + "." + fingerprint + ".part";
    }

    public async Task<MediaDownloadResult> DownloadAsync(
        VideoSource                  source,
        string                       destinationPath,
        bool                         overwrite           = false,
        bool                         resume              = true,
        IProgress<DownloadProgress>? progress           = null,
        CancellationToken             cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Type == VideoType.Embed)
        {
            throw new DownloadException("Embed kaynakları doğrudan indirilemez.");
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Video hedef yolu boş olamaz.", nameof(destinationPath));
        }

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var sourceUri))
        {
            throw new DownloadException("Video kaynak URL'si geçersiz.");
        }

        DownloadHttp.ValidateHttpUri(sourceUri);

        var finalPath = Path.GetFullPath(destinationPath);
        DownloadPathBuilder.EnsureFullPathBudget(finalPath, DownloadPathBuilder.TemporarySuffixUtf8Bytes);
        var parent = Path.GetDirectoryName(finalPath)
                     ?? throw new DownloadException("Video hedef dizini geçersiz.");
        Directory.CreateDirectory(parent);

        var lockIdentity = Path.Combine(parent, Path.GetFileNameWithoutExtension(finalPath));
        using var targetLock = await DownloadTargetLock.AcquireAsync(lockIdentity, cancellationToken)
                                             .ConfigureAwait(false);
        DeleteLegacyPart(finalPath);
        if (File.Exists(finalPath) && !overwrite)
        {
            throw new DownloadException("Video hedefi zaten var; overwrite kapalı.");
        }

        var requestHeaders = DownloadHttp.CopyHeaders(source.Headers);
        requestHeaders.Remove("Range");
        var sourceFingerprint = DownloadHttp.CreateSourceFingerprint(source.Url,
                                                                        requestHeaders);
        // Eski sürümün kaynak kimliği taşımayan final.part dosyası bilinçli olarak
        // resume edilmez; yalnız fingerprint + metadata eşleşen parça kullanılır.
        var partPath = finalPath + "." + sourceFingerprint + ".part";
        var metadataPath = partPath + ".meta";
        DownloadPathBuilder.EnsureFullPathBudget(metadataPath, reservedSuffixBytes: 0);

        Report(progress, DownloadStage.Preparing, 0, null);

        var client           = _fixedClient ?? _clientFactory!.CreateClient();
        var disposeClient    = _fixedClient is null;
        var lastKnownBytes   = 0L;
        long? lastKnownTotal = null;

        try
        {
            for (var requestAttempt = 0; requestAttempt < MaxResumeAttempts; requestAttempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resumeState = PrepareResumeState(resume,
                                                     partPath,
                                                     metadataPath,
                                                     sourceFingerprint);
                if (resumeState is null)
                {
                    requestHeaders.Remove("Range");
                }
                else
                {
                    requestHeaders["Range"] = "bytes="
                                             + resumeState.ExistingBytes.ToString(CultureInfo.InvariantCulture)
                                             + "-";
                }

                Report(progress,
                       DownloadStage.Requesting,
                       resumeState?.ExistingBytes ?? 0,
                       resumeState?.Metadata.TotalBytes);

                using var response = await DownloadHttp.SendWithRedirectsAsync(client,
                                                                               sourceUri,
                                                                               requestHeaders,
                                                                               cancellationToken)
                                         .ConfigureAwait(false);
                if (DownloadHttp.IsHtml(response))
                {
                    throw new DownloadException("Video kaynağı HTML içerik döndürdü.");
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    if (resumeState is not null
                        && TryGetUnsatisfiedTotal(GetContentRange(response), out var completeTotal)
                        && completeTotal > 0
                        && completeTotal == resumeState.ExistingBytes
                        && File.Exists(partPath)
                        && new FileInfo(partPath).Length == resumeState.ExistingBytes
                        && resumeState.Metadata.MatchesResponse(GetETag(response),
                                                                 GetLastModified(response)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lastKnownBytes = resumeState.ExistingBytes;
                        lastKnownTotal = completeTotal;
                        Report(progress,
                               DownloadStage.Finalizing,
                               resumeState.ExistingBytes,
                               completeTotal);
                        MoveCompletedPart(partPath, finalPath, overwrite, cancellationToken);
                        Mp4ResumeMetadata.Delete(metadataPath);
                        Report(progress,
                               DownloadStage.Completed,
                               resumeState.ExistingBytes,
                               completeTotal);
                        return new MediaDownloadResult(finalPath,
                                                       resumeState.ExistingBytes,
                                                       completeTotal,
                                                       true);
                    }

                    if (requestAttempt + 1 < MaxResumeAttempts)
                    {
                        ResetPartial(partPath, metadataPath);
                        continue;
                    }

                    throw new DownloadException("Video aralık isteği tamamlanamadı.");
                }

                if (response.StatusCode != HttpStatusCode.OK
                    && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    throw new DownloadException("Video sunucusu indirilebilir bir yanıt vermedi.");
                }

                if (DownloadHttp.IsNonMediaContent(response))
                {
                    // 200 + text/plain|json|xml|görsel gövde sunucu hata çıktısıdır;
                    // medya dosyası olarak yazılmamalıdır.
                    throw new DownloadException("Video kaynağı medya dışı bir içerik döndürdü.");
                }

                var append           = response.StatusCode == HttpStatusCode.PartialContent;
                var existingBytes    = resumeState?.ExistingBytes ?? 0;
                long? total          = null;
                long  expectedLength = 0;
                long  completionEnd  = 0;

                if (append)
                {
                    if (!TryParseContentRange(GetContentRange(response),
                                               out var rangeStart,
                                               out var rangeEnd,
                                               out total)
                        || resumeState is null
                        || rangeStart != existingBytes
                        || rangeEnd < rangeStart
                        || rangeEnd == long.MaxValue
                        || (total.HasValue && total.Value <= rangeEnd)
                        || !TryCheckedRangeLength(rangeStart, rangeEnd, out expectedLength)
                        || !resumeState.Metadata.MatchesResponse(GetETag(response),
                                                                 GetLastModified(response)))
                    {
                        if (requestAttempt + 1 < MaxResumeAttempts)
                        {
                            ResetPartial(partPath, metadataPath);
                            continue;
                        }

                        throw new DownloadException("Video sunucusu geçersiz bir aralık yanıtı verdi.");
                    }

                    var contentLength = response.Content.Headers.ContentLength;
                    if (contentLength.HasValue && contentLength.Value != expectedLength)
                    {
                        if (requestAttempt + 1 < MaxResumeAttempts)
                        {
                            ResetPartial(partPath, metadataPath);
                            continue;
                        }

                        throw new DownloadException("Video sunucusu aralık uzunluğu tutarsız.");
                    }

                    completionEnd = total ?? checked(rangeEnd + 1);
                }
                else
                {
                    existingBytes = 0;
                    total         = response.Content.Headers.ContentLength;
                    if (total is <= 0)
                    {
                        throw new DownloadException("Video sunucusu boş bir gövde döndürdü.");
                    }
                }

                if (!append)
                {
                    // 200, Range isteğini yok sayarak tam yeniden başlatma sinyalidir.
                    ResetPartial(partPath, metadataPath);
                }

                var metadata = Mp4ResumeMetadata.Create(sourceFingerprint,
                                                         GetETag(response),
                                                         GetLastModified(response),
                                                         total);
                await Mp4ResumeMetadata.WriteAsync(metadataPath,
                                                    metadata,
                                                    cancellationToken)
                                            .ConfigureAwait(false);

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = new FileStream(partPath,
                                                      append ? FileMode.Append : FileMode.CreateNew,
                                                      FileAccess.Write,
                                                      FileShare.Read,
                                                      BufferSize,
                                                      FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (append && file.Length != existingBytes)
                {
                    throw new DownloadException("Video parçası indirme sırasında değişti.");
                }

                var downloaded   = existingBytes;
                lastKnownBytes   = downloaded;
                lastKnownTotal   = total;
                Report(progress, DownloadStage.Downloading, downloaded, total);

                var buffer         = new byte[BufferSize];
                var lastReportTime = Stopwatch.GetTimestamp();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = append ? expectedLength - (downloaded - existingBytes) : buffer.Length;
                    if (append && remaining == 0)
                    {
                        var extraProbe = new byte[1];
                        var extra      = await body.ReadAsync(extraProbe.AsMemory(), cancellationToken)
                                             .ConfigureAwait(false);
                        if (extra != 0)
                        {
                            throw new DownloadException("Video sunucusu aralık uzunluğu tutarsız.");
                        }

                        break;
                    }

                    var readLength = append
                                         ? (int)Math.Min(buffer.Length, remaining)
                                         : buffer.Length;
                    var read = await body.ReadAsync(buffer.AsMemory(0, readLength), cancellationToken)
                                     .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                              .ConfigureAwait(false);
                    downloaded += read;
                    lastKnownBytes = downloaded;

                    if (ShouldReport(lastReportTime, downloaded, total))
                    {
                        Report(progress, DownloadStage.Downloading, downloaded, total);
                        lastReportTime = Stopwatch.GetTimestamp();
                    }
                }

                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                var receivedRangeBytes = downloaded - existingBytes;
                if (downloaded == 0
                    || (append && receivedRangeBytes != expectedLength)
                    || (total.HasValue && downloaded != total.Value)
                    || (append && total.HasValue && downloaded != completionEnd))
                {
                    throw new DownloadException("Video indirilemedi; gövde beklenen aralıkta değil.");
                }

                await file.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, DownloadStage.Finalizing, downloaded, total);
                MoveCompletedPart(partPath, finalPath, overwrite, cancellationToken);
                Mp4ResumeMetadata.Delete(metadataPath);
                Report(progress, DownloadStage.Completed, downloaded, total);

                return new MediaDownloadResult(finalPath, downloaded, total, append);
            }

            throw new DownloadException("Video kaynağı güvenli biçimde yeniden başlatılamadı.");
        }
        catch (OperationCanceledException)
        {
            Report(progress, DownloadStage.Cancelled, lastKnownBytes, lastKnownTotal);
            throw;
        }
        catch (DownloadException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new DownloadException("Video HTTP isteği başarısız oldu.");
        }
        catch (IOException)
        {
            throw new DownloadException("Video dosyası yazılamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new DownloadException("Video dosyasına erişilemedi.");
        }
        catch (FormatException)
        {
            throw new DownloadException("Video isteği oluşturulamadı.");
        }
        catch (InvalidOperationException)
        {
            throw new DownloadException("Video isteği oluşturulamadı.");
        }
        catch (ArgumentException)
        {
            throw new DownloadException("Video isteği oluşturulamadı.");
        }
        finally
        {
            if (disposeClient)
            {
                client.Dispose();
            }
        }
    }

    private static ResumeState? PrepareResumeState(
        bool   resume,
        string partPath,
        string metadataPath,
        string sourceFingerprint)
    {
        if (!resume)
        {
            ResetPartial(partPath, metadataPath);
            return null;
        }

        try
        {
            if (!File.Exists(partPath))
            {
                Mp4ResumeMetadata.Delete(metadataPath);
                return null;
            }

            var existingBytes = new FileInfo(partPath).Length;
            if (existingBytes <= 0
                || !Mp4ResumeMetadata.TryRead(metadataPath,
                                              sourceFingerprint,
                                              out var metadata)
                || metadata is null
                || (metadata.TotalBytes is { } total && existingBytes > total))
            {
                ResetPartial(partPath, metadataPath);
                return null;
            }

            return new ResumeState(existingBytes, metadata);
        }
        catch (IOException)
        {
            ResetPartial(partPath, metadataPath);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            throw new DownloadException("Video parçasına erişilemedi.");
        }
    }

    private static void DeleteLegacyPart(string finalPath)
    {
        var legacyPart = finalPath + ".part";
        DeleteIfExists(legacyPart);
        Mp4ResumeMetadata.Delete(legacyPart + ".meta");
    }

    private static void ResetPartial(string partPath, string metadataPath)
    {
        DeleteIfExists(partPath);
        Mp4ResumeMetadata.Delete(metadataPath);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            throw new DownloadException("Eski video parçası temizlenemedi.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new DownloadException("Eski video parçasına erişilemedi.");
        }
    }

    private static string? GetContentRange(HttpResponseMessage response)
    {
        var contentRange = response.Content.Headers.ContentRange?.ToString();
        if (!string.IsNullOrWhiteSpace(contentRange))
        {
            return contentRange;
        }

        return response.Headers.TryGetValues("Content-Range", out var values)
                   ? string.Join(",", values)
                   : null;
    }

    private static string? GetETag(HttpResponseMessage response)
    {
        return response.Headers.ETag?.ToString();
    }

    private static string? GetLastModified(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Last-Modified", out var values))
        {
            return values.FirstOrDefault();
        }

        return response.Content.Headers.LastModified?.ToString();
    }

    private static bool TryParseContentRange(
        string? value,
        out long   start,
        out long   end,
        out long?  total)
    {
        start = 0;
        end   = 0;
        total = null;

        var match = ContentRangeRegex.Match(value ?? string.Empty);
        if (!match.Success
            || !long.TryParse(match.Groups[1].Value,
                              NumberStyles.None,
                              CultureInfo.InvariantCulture,
                              out start)
            || !long.TryParse(match.Groups[2].Value,
                              NumberStyles.None,
                              CultureInfo.InvariantCulture,
                              out end))
        {
            return false;
        }

        var totalText = match.Groups[3].Value;
        if (totalText == "*")
        {
            return true;
        }

        return long.TryParse(totalText,
                             NumberStyles.None,
                             CultureInfo.InvariantCulture,
                             out var parsedTotal)
               && (total = parsedTotal) > 0;
    }

    private static bool TryGetUnsatisfiedTotal(string? value, out long total)
    {
        total = 0;
        var match = UnsatisfiedRangeRegex.Match(value ?? string.Empty);
        return match.Success
               && long.TryParse(match.Groups[1].Value,
                                NumberStyles.None,
                                CultureInfo.InvariantCulture,
                                out total)
               && total > 0;
    }

    private static bool TryCheckedRangeLength(long start, long end, out long length)
    {
        try
        {
            length = checked(end - start + 1);
            return length > 0;
        }
        catch (OverflowException)
        {
            length = 0;
            return false;
        }
    }

    private static bool ShouldReport(long lastReportTime, long downloaded, long? total)
    {
        if (total.HasValue && downloaded >= total.Value)
        {
            return true;
        }

        return Stopwatch.GetElapsedTime(lastReportTime).TotalMilliseconds
               >= ProgressIntervalMilliseconds;
    }

    private static void Report(
        IProgress<DownloadProgress>? progress,
        DownloadStage                 stage,
        long                          bytes,
        long?                         total)
    {
        progress?.Report(new DownloadProgress(stage, bytes, total));
    }

    private static void MoveCompletedPart(
        string          partPath,
        string          finalPath,
        bool            overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            File.Move(partPath, finalPath, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(finalPath))
        {
            throw new DownloadException("Video hedefi zaten var; overwrite kapalı.");
        }
        catch (IOException)
        {
            throw new DownloadException("Video dosyası taşınamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new DownloadException("Video dosyası taşınamadı.");
        }
    }

    private sealed record ResumeState(long ExistingBytes, Mp4ResumeMetadata Metadata);
}
