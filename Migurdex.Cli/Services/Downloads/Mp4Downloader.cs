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
    private const long ParallelThresholdBytes = 8L * 1024 * 1024;
    private const long ParallelBytesPerConnection = 16L * 1024 * 1024;
    private const int ParallelMinSegments = 2;
    private const int ParallelMaxSegments = 4;

    /// <summary>
    /// Güncel parçaya ek olarak, hedef dosya için tutulacak en fazla eski aday parçası.
    /// Aday izolasyonu korunur (yakın adaylar resume için saklanır), disk kullanımı ise
    /// sınırlanır.
    /// </summary>
    private const int MaxRetainedResumeParts = 2;

    private static readonly Regex ContentRangeRegex = new(
        @"^bytes\s+(\d+)-(\d+)/(\d+|\*)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UnsatisfiedRangeRegex = new(
        @"^bytes\s+\*/(\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IDownloadHttpClientFactory? _clientFactory;
    private readonly HttpClient?                  _fixedClient;
    private readonly DownloadStallOptions         _stallOptions;

    public Mp4Downloader()
        : this(new DownloadHttpClientFactory())
    {
    }

    public Mp4Downloader(IDownloadHttpClientFactory clientFactory,
                         DownloadStallOptions?     stallOptions = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _stallOptions  = stallOptions ?? DownloadStallOptions.Default;
    }

    public Mp4Downloader(HttpMessageHandler handler,
                         DownloadStallOptions? stallOptions = null)
        : this(new HttpMessageHandlerDownloadClientFactory(handler), stallOptions)
    {
    }

    public Mp4Downloader(HttpClient client)
    {
        _fixedClient  = client ?? throw new ArgumentNullException(nameof(client));
        _stallOptions = DownloadStallOptions.Default;
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
        var fingerprint = DownloadHttp.CreateSourceFingerprint(source.Url,
                                                                headers,
                                                                BuildSourceDescriptor(source));
        return Path.GetFullPath(destinationPath) + "." + fingerprint + ".part";
    }

    /// <summary>
    /// Adayı ayırt etmek için fingerprint'e eklenen, URL'de bulunmayan nitelikler.
    /// Normalizasyon yalnızca URL'nin nesne tanımlayan kısmını korur; aynı yoldaki iki
    /// farklı kalite (ör. Google Drive <c>itag=37</c> ve <c>itag=22</c>) sorgu parametresiyle
    /// ayrılır ve bu parametreler beyaz listede tutulur. Yine de sağlayıcı kaliteyi
    /// yolda bildiriyorsa ayrım kaybolabilir; nitelikler bu boşluğu kapatır.
    /// </summary>
    private static string BuildSourceDescriptor(VideoSource source)
    {
        return string.Join('',
                           source.Type.ToString(),
                           (source.Quality ?? string.Empty).Trim(),
                           (source.Hoster ?? string.Empty).Trim(),
                           (source.Group ?? string.Empty).Trim(),
                           (source.Language ?? string.Empty).Trim());
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

        await DownloadHttp.ValidateHttpUriAsync(sourceUri, cancellationToken).ConfigureAwait(false);

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
                                                                        requestHeaders,
                                                                        BuildSourceDescriptor(source));
        // Eski sürümün kaynak kimliği taşımayan final.part dosyası bilinçli olarak
        // resume edilmez; yalnız fingerprint + metadata eşleşen parça kullanılır.
        var partPath = finalPath + "." + sourceFingerprint + ".part";
        var metadataPath = partPath + ".meta";
        DownloadPathBuilder.EnsureFullPathBudget(metadataPath, reservedSuffixBytes: 0);
        DeleteStaleResumeParts(finalPath, sourceFingerprint);

        Report(progress, DownloadStage.Preparing, 0, null);

        var client           = _fixedClient ?? _clientFactory!.CreateClient();
        var disposeClient    = _fixedClient is null;
        var lastKnownBytes   = 0L;
        long? lastKnownTotal = null;
        var parallelAttempted = false;

        try
        {
            if (resume)
            {
                var resumedParallel = await TryResumeParallelAsync(client,
                                                                   sourceUri,
                                                                   requestHeaders,
                                                                   partPath,
                                                                   metadataPath,
                                                                   sourceFingerprint,
                                                                   finalPath,
                                                                   overwrite,
                                                                   progress,
                                                                   cancellationToken)
                                            .ConfigureAwait(false);
                if (resumedParallel is not null)
                {
                    return resumedParallel;
                }
            }

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
                                                                               cancellationToken,
                                                                               _stallOptions)
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

                if (!parallelAttempted
                    && resumeState is null
                    && requestAttempt == 0
                    && response.StatusCode == HttpStatusCode.OK
                    && TryGetParallelTotal(response, out var parallelTotal))
                {
                    parallelAttempted = true;
                    response.Dispose();
                    var parallelResult = await TryParallelFreshAsync(client,
                                                                     sourceUri,
                                                                     requestHeaders,
                                                                     partPath,
                                                                     metadataPath,
                                                                     sourceFingerprint,
                                                                     finalPath,
                                                                     overwrite,
                                                                     parallelTotal,
                                                                     GetETag(response),
                                                                     GetLastModified(response),
                                                                     progress,
                                                                     cancellationToken)
                                               .ConfigureAwait(false);
                    if (parallelResult is not null)
                    {
                        return parallelResult;
                    }

                    continue;
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

                // Bekçi gövde akışından ÖNCE bildirilir: using bildirimleri ters
                // sırada yok edildiği için, asılı kalmış okumanın iptalinden sonra
                // bekçinin kendisi temizlenir.
                using var stall = new DownloadStallGuard(_stallOptions, cancellationToken);
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
                        var extra      = await stall.ReadAsync(body, extraProbe.AsMemory())
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
                    var read = await stall.ReadAsync(body, buffer.AsMemory(0, readLength))
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

    /// <summary>
    /// Hedef dosyaya ait olup güncel fingerprint ile eşleşmeyen `.part`/`.meta`
    /// kalıntılarını <b>eskiden yeniye</b> sırayla döndürür; ilk
    /// <see cref="MaxRetainedResumeParts"/> eşleşme korunur, fazlası atılır.
    ///
    /// Gerekçe: kaynak URL'i imzalı olduğunda eski sürümün ham-URL hash'i her
    /// çalıştırmada yeni fingerprint üretiyordu; her deneme bölüm boyutunda yeni bir
    /// `.part` bırakıyor ve hiçbiri silinmiyordu. Fingerprint artık kararlı olduğundan
    /// aynı aday artık aynı parçayı kullanır, ama sürümden önce biriken kalıntılar ve
    /// farklı adayların bıraktığı parçalar hâlâ diski sınırsız büyütebilir. Bu yüzden
    /// parçalar tümüyle silinmez — aday izolasyonu sözleşmesi korunur, yalnız sayı
    /// sınırlanır.
    /// </summary>
    public static IReadOnlyList<string> GetStaleResumePartPaths(
        string destinationPath,
        string                     currentFingerprint)
    {
        var stale = new List<string>();
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return stale;
        }

        var finalPath = Path.GetFullPath(destinationPath);
        var parent    = Path.GetDirectoryName(finalPath);
        var fileName  = Path.GetFileName(finalPath);
        if (string.IsNullOrEmpty(parent)
            || string.IsNullOrEmpty(fileName)
            || !Directory.Exists(parent))
        {
            return stale;
        }

        var currentPrefix = fileName + "." + currentFingerprint + ".part";
        var legacyPart    = fileName + ".part";
        var prefix        = fileName + ".";
        const string partMarker = ".part";

        // Glob deseni kullanılmıyor: dosya adı `[`/`]` içerebilir ve bunlar desen
        // meta karakteridir. Dizin listelenip filtre kod içinde yapılır.
        var groups = new Dictionary<string, ResumeGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in Directory.EnumerateFiles(parent))
        {
            if (!string.Equals(Path.GetDirectoryName(candidate),
                               parent,
                               StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(candidate);
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string? metaPath = null;
            string groupKey;
            if (name.EndsWith(partMarker + ".meta", StringComparison.Ordinal))
            {
                groupKey = name[..^".meta".Length];
                metaPath = candidate;
            }
            else if (TryGetResumeGroupKey(name, prefix, partMarker, out var key))
            {
                groupKey = key;
            }
            else
            {
                continue;
            }

            // Eski sürümün fingerprint'siz `final.part` biçimi de ayrı bir gruptur.
            if (string.Equals(groupKey, legacyPart, StringComparison.Ordinal))
            {
                groupKey = legacyPart;
            }

            if (string.Equals(groupKey, currentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!groups.TryGetValue(groupKey, out var group))
            {
                group = new ResumeGroup { Key = groupKey };
            }

            if (metaPath is not null)
            {
                group.MetadataPath = metaPath;
            }
            else
            {
                group.PartPaths.Add(candidate);
            }

            var modified = SafeLastWriteTimeUtc(candidate);
            if (modified > group.ModifiedUtc)
            {
                group.ModifiedUtc = modified;
            }

            groups[groupKey] = group;
        }

        // Yeni parçaya en yakın adaylar resume için değerlidir; eskiler atılır.
        //
        // `ModifiedUtc` bir türev alandır (gruptaki en yeni dosyanın zamanı) ve
        // dosya sistemi çözünürlüğü saniyeden kaba olduğu için farklı adaylar
        // aynı değeri alabilir. Bağlayıcısız (tiebreaker'sız) bir sıralamada
        // `Skip` bu durumda **hangi** 2 grubun korunacağını keyfî seçer; yani
        // "en yeni 2" sözleşmesi sessizce aday izolasyonunu bozabilir. Grup
        // anahtarı ikincil ölçüt olarak kullanılır: seçim artık dosya sistemi
        // sırasına değil, kararlı bir kıyaslamaya bağlıdır ve tekrar çalıştırmada
        // birebir aynı sonucu verir.
        foreach (var group in groups.Values.OrderByDescending(entry => entry.ModifiedUtc)
                                         .ThenByDescending(entry => entry.Key, StringComparer.Ordinal)
                                         .Skip(MaxRetainedResumeParts))
        {
            if (group.MetadataPath is not null)
            {
                stale.Add(group.MetadataPath);
            }

            stale.AddRange(group.PartPaths);
        }

        return stale;
    }

    private sealed class ResumeGroup
    {
        /// <summary>Sıralamada bağlayıcı olarak kullanılan kararlı grup anahtarı.</summary>
        public string              Key          { get; init; } = string.Empty;
        public string?             MetadataPath { get; set; }
        public List<string>        PartPaths    { get; } = [];
        public DateTime            ModifiedUtc  { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// `<ad>.&lt;fingerprint&gt;.part` ile `<ad>.&lt;fingerprint&gt;.part.segN`
    /// adlarını aynı gruba indirger. Düğüm yalnız fingerprint uzunluğunu (24 onaltılık
    /// karakter) doğrular; eski sürümün fingerprint'siz `final.part` biçimi buraya girmez.
    /// </summary>
    private static bool TryGetResumeGroupKey(
        string name,
        string prefix,
        string partMarker,
        out    string groupKey)
    {
        groupKey = string.Empty;
        var markerIndex = name.IndexOf(partMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var fingerprint = name[prefix.Length..markerIndex];
        if (fingerprint.Length != DownloadHttp.SourceFingerprintLength)
        {
            return false;
        }

        groupKey = prefix + fingerprint + partMarker;
        return true;
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }

    private static void DeleteStaleResumeParts(string finalPath, string currentFingerprint)
    {
        foreach (var stale in GetStaleResumePartPaths(finalPath, currentFingerprint))
        {
            DeleteIfExists(stale);
        }
    }

    private static void ResetPartial(string partPath, string metadataPath)
    {
        DeleteIfExists(partPath);
        DeleteParallelSegments(partPath);
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

    private static string GetSegmentPath(string partPath, int index)
    {
        return partPath + ".seg" + index.ToString(CultureInfo.InvariantCulture);
    }

    private static void DeleteParallelSegments(string partPath)
    {
        for (var i = 0; i < ParallelMaxSegments; i++)
        {
            try
            {
                var segment = GetSegmentPath(partPath, i);
                if (File.Exists(segment))
                {
                    File.Delete(segment);
                }
            }
            catch
            {
                // Temizlik en iyi eforladır; indirme sonucunu maskeleme.
            }
        }
    }

    private static void DeleteParallelArtifacts(string partPath, string metadataPath)
    {
        DeleteParallelSegments(partPath);
        Mp4ResumeMetadata.Delete(metadataPath);
    }

    private static int ComputeSegmentCount(long total)
    {
        if (total < ParallelThresholdBytes)
        {
            return 0;
        }

        var count = (int)(total / ParallelBytesPerConnection);
        return (int)Math.Clamp(count, ParallelMinSegments, ParallelMaxSegments);
    }

    private static bool TryGetParallelTotal(HttpResponseMessage response, out long total)
    {
        total = 0;
        var contentLength = response.Content.Headers.ContentLength;
        if (!contentLength.HasValue || contentLength.Value < ParallelThresholdBytes)
        {
            return false;
        }

        foreach (var value in response.Headers.AcceptRanges)
        {
            if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        total = contentLength.Value;
        return total > 0;
    }

    private async Task<MediaDownloadResult?> TryResumeParallelAsync(
        HttpClient                     client,
        Uri                            sourceUri,
        Dictionary<string, string>     baseHeaders,
        string                         partPath,
        string                         metadataPath,
        string                         sourceFingerprint,
        string                         finalPath,
        bool                           overwrite,
        IProgress<DownloadProgress>?   progress,
        CancellationToken              cancellationToken)
    {
        Mp4ResumeMetadata? manifest = null;
        try
        {
            if (!Mp4ResumeMetadata.TryRead(metadataPath, sourceFingerprint, out manifest)
                || manifest is null
                || manifest.SegmentCount is not >= ParallelMinSegments
                || manifest.SegmentCount > ParallelMaxSegments
                || manifest.TotalBytes is not { } manifestTotal
                || manifestTotal < ParallelThresholdBytes)
            {
                return null;
            }

            if (File.Exists(partPath))
            {
                try
                {
                    if (new FileInfo(partPath).Length > 0)
                    {
                        DeleteParallelSegments(partPath);
                        return null;
                    }
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    throw new DownloadException("Video parçasına erişilemedi.");
                }
            }

            var segmentCount = manifest.SegmentCount.Value;
            var hasAnySegment = false;
            for (var i = 0; i < segmentCount; i++)
            {
                if (File.Exists(GetSegmentPath(partPath, i)))
                {
                    hasAnySegment = true;
                    break;
                }
            }

            if (!hasAnySegment)
            {
                return null;
            }

            return await ExecuteParallelAsync(client,
                                              sourceUri,
                                              baseHeaders,
                                              partPath,
                                              metadataPath,
                                              sourceFingerprint,
                                              finalPath,
                                              overwrite,
                                              manifestTotal,
                                              null,
                                              null,
                                              manifest,
                                              progress,
                                              cancellationToken)
                             .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new DownloadException("Video dosyası yazılamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new DownloadException("Video dosyasına erişilemedi.");
        }
        catch
        {
            return null;
        }
    }

    private async Task<MediaDownloadResult?> TryParallelFreshAsync(
        HttpClient                     client,
        Uri                            sourceUri,
        Dictionary<string, string>     baseHeaders,
        string                         partPath,
        string                         metadataPath,
        string                         sourceFingerprint,
        string                         finalPath,
        bool                           overwrite,
        long                           total,
        string?                        etag,
        string?                        lastModified,
        IProgress<DownloadProgress>?   progress,
        CancellationToken              cancellationToken)
    {
        try
        {
            return await ExecuteParallelAsync(client,
                                              sourceUri,
                                              baseHeaders,
                                              partPath,
                                              metadataPath,
                                              sourceFingerprint,
                                              finalPath,
                                              overwrite,
                                              total,
                                              etag,
                                              lastModified,
                                              null,
                                              progress,
                                              cancellationToken)
                             .ConfigureAwait(false);
        }
        catch (ParallelFallbackException)
        {
            DeleteParallelArtifacts(partPath, metadataPath);
            return null;
        }
        catch (OperationCanceledException)
        {
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
        catch
        {
            DeleteParallelArtifacts(partPath, metadataPath);
            return null;
        }
    }

    private async Task<MediaDownloadResult?> ExecuteParallelAsync(
        HttpClient                     client,
        Uri                            sourceUri,
        Dictionary<string, string>     baseHeaders,
        string                         partPath,
        string                         metadataPath,
        string                         sourceFingerprint,
        string                         finalPath,
        bool                           overwrite,
        long                           total,
        string?                        probeEtag,
        string?                        probeLastModified,
        Mp4ResumeMetadata?             resumeManifest,
        IProgress<DownloadProgress>?   progress,
        CancellationToken              cancellationToken)
    {
        var segmentCount = ComputeSegmentCount(total);
        if (segmentCount < ParallelMinSegments)
        {
            return null;
        }

        Mp4ResumeMetadata manifest;
        if (resumeManifest is not null
            && resumeManifest.SegmentCount == segmentCount
            && resumeManifest.TotalBytes == total)
        {
            manifest = resumeManifest;
        }
        else if (resumeManifest is not null)
        {
            DeleteParallelArtifacts(partPath, metadataPath);
            manifest = Mp4ResumeMetadata.Create(sourceFingerprint, probeEtag, probeLastModified, total, segmentCount);
            await Mp4ResumeMetadata.WriteAsync(metadataPath, manifest, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            manifest = Mp4ResumeMetadata.Create(sourceFingerprint, probeEtag, probeLastModified, total, segmentCount);
            await Mp4ResumeMetadata.WriteAsync(metadataPath, manifest, cancellationToken).ConfigureAwait(false);
        }

        var segmentLengthBase = total / segmentCount;
        var ranges = new (long Start, long End)[segmentCount];
        for (var i = 0; i < segmentCount; i++)
        {
            var start = i * segmentLengthBase;
            var end = i == segmentCount - 1 ? total - 1 : (i + 1) * segmentLengthBase - 1;
            ranges[i] = (start, end);
        }

        if (resumeManifest is null)
        {
            DeleteParallelSegments(partPath);
        }

        var existingLengths = new long[segmentCount];
        for (var i = 0; i < segmentCount; i++)
        {
            try
            {
                var segmentPath = GetSegmentPath(partPath, i);
                if (File.Exists(segmentPath))
                {
                    var length = new FileInfo(segmentPath).Length;
                    var expectedLength = ranges[i].End - ranges[i].Start + 1;
                    if (length < 0 || length > expectedLength)
                    {
                        try { File.Delete(segmentPath); } catch { }
                        length = 0;
                    }

                    existingLengths[i] = length;
                }
            }
            catch (IOException)
            {
                throw new DownloadException("Video parçasına erişilemedi.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new DownloadException("Video parçasına erişilemedi.");
            }
        }

        long initialDownloaded = 0;
        for (var i = 0; i < segmentCount; i++)
        {
            initialDownloaded += existingLengths[i];
        }

        var sharedTotal = new long[1];
        sharedTotal[0] = initialDownloaded;

        Report(progress, DownloadStage.Requesting, sharedTotal[0], total);
        var progressLock = new Lock();
        var lastReportTime = Stopwatch.GetTimestamp();
        var reportedFloor = 0L;

        void ReportAggregated()
        {
            // `current` kilit DIŞINDA okunup `Report` kilit DIŞINDA çağrılıyordu; iki
            // segment şu şekilde iç içe geçebiliyordu: T1 40 MB okur, T2 80 MB okur,
            // T2 raporlar, T1 raporlar -> arayüz bayt sayacını geriye götürür. HLS
            // yolunda bu sınıf düzeltilmişti (E9/C1b) ama `ParallelMaxSegments`
            // yolunda (8 MB üzeri ve `Accept-Ranges` olan **her gerçek** MP4) hiç
            // monotonluk koruması yoktu.
            lock (progressLock)
            {
                var current = Interlocked.Read(ref sharedTotal[0]);
                if (current < reportedFloor)
                {
                    current = reportedFloor;
                }

                reportedFloor = current;
                if (ShouldReport(lastReportTime, current, total))
                {
                    lastReportTime = Stopwatch.GetTimestamp();
                    Report(progress, DownloadStage.Downloading, current, total);
                }
            }
        }

        void AddBytes(long count)
        {
            Interlocked.Add(ref sharedTotal[0], count);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var segmentToken = linkedCts.Token;
        var tasks = new Task[segmentCount];
        for (var i = 0; i < segmentCount; i++)
        {
            var index = i;
            tasks[index] = Task.Run(async () =>
            {
                try
                {
                    await DownloadOneSegmentAsync(client,
                                                  sourceUri,
                                                  baseHeaders,
                                                  partPath,
                                                  index,
                                                  ranges[index].Start,
                                                  ranges[index].End,
                                                  total,
                                                  manifest,
                                                  AddBytes,
                                                  ReportAggregated,
                                                  existingLengths[index],
                                                  segmentToken)
                                  .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    try { await linkedCts.CancelAsync(); } catch { }
                    throw;
                }
            }, segmentToken);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            var fatal = tasks.SelectMany(t => t.Exception?.InnerExceptions ?? [])
                             .OfType<DownloadException>()
                             .FirstOrDefault();
            if (fatal is not null)
            {
                throw fatal;
            }

            var fallback = tasks.SelectMany(t => t.Exception?.InnerExceptions ?? [])
                                .OfType<ParallelFallbackException>()
                                .FirstOrDefault();
            if (fallback is not null)
            {
                DeleteParallelArtifacts(partPath, metadataPath);
                return null;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Report(progress, DownloadStage.Cancelled, Interlocked.Read(ref sharedTotal[0]), total);
                throw;
            }

            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();

        for (var i = 0; i < segmentCount; i++)
        {
            var expectedLength = ranges[i].End - ranges[i].Start + 1;
            long actualLength;
            try
            {
                actualLength = new FileInfo(GetSegmentPath(partPath, i)).Length;
            }
            catch (IOException)
            {
                throw new DownloadException("Video dosyası yazılamadı.");
            }
            catch (UnauthorizedAccessException)
            {
                throw new DownloadException("Video dosyasına erişilemedi.");
            }

            if (actualLength != expectedLength)
            {
                throw new DownloadException("Video indirilemedi; gövde beklenen aralıkta değil.");
            }
        }

        Report(progress, DownloadStage.Finalizing, total, total);
        await MergeSegmentsAsync(partPath, segmentCount, cancellationToken).ConfigureAwait(false);
        MoveCompletedPart(partPath, finalPath, overwrite, cancellationToken);
        DeleteParallelSegments(partPath);
        Mp4ResumeMetadata.Delete(metadataPath);
        Report(progress, DownloadStage.Completed, total, total);

        return new MediaDownloadResult(finalPath, total, total, resumeManifest is not null);
    }

    private async Task DownloadOneSegmentAsync(
        HttpClient                     client,
        Uri                            sourceUri,
        Dictionary<string, string>     baseHeaders,
        string                         partPath,
        int                            index,
        long                           rangeStart,
        long                           rangeEnd,
        long                           total,
        Mp4ResumeMetadata              manifest,
        Action<long>                   addBytes,
        Action                         reportAggregated,
        long                           existingLength,
        CancellationToken              cancellationToken)
    {
        var segmentLength = rangeEnd - rangeStart + 1;
        if (existingLength == segmentLength)
        {
            return;
        }

        var requestStart = rangeStart + existingLength;
        var headers = DownloadHttp.CopyHeaders(baseHeaders);
        headers["Range"] = "bytes="
                           + requestStart.ToString(CultureInfo.InvariantCulture)
                           + "-"
                           + rangeEnd.ToString(CultureInfo.InvariantCulture);

        using var response = await DownloadHttp.SendWithRedirectsAsync(client,
                                                                       sourceUri,
                                                                       headers,
                                                                       cancellationToken,
                                                                       _stallOptions)
                                     .ConfigureAwait(false);
        if (DownloadHttp.IsHtml(response))
        {
            throw new DownloadException("Video kaynağı HTML içerik döndürdü.");
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            throw new ParallelFallbackException();
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                throw new ParallelFallbackException();
            }

            throw new DownloadException("Video sunucusu indirilebilir bir yanıt vermedi.");
        }

        if (DownloadHttp.IsNonMediaContent(response))
        {
            throw new DownloadException("Video kaynağı medya dışı bir içerik döndürdü.");
        }

        if (!TryParseContentRange(GetContentRange(response), out var actualStart, out var actualEnd, out var actualTotal)
            || actualStart != requestStart
            || actualEnd != rangeEnd
            || (actualTotal.HasValue && actualTotal.Value != total)
            || !actualTotal.HasValue)
        {
            throw new ParallelFallbackException();
        }

        if (!manifest.MatchesResponse(GetETag(response), GetLastModified(response)))
        {
            throw new ParallelFallbackException();
        }

        var contentLength = response.Content.Headers.ContentLength;
        var expectedRemaining = rangeEnd - requestStart + 1;
        if (contentLength.HasValue && contentLength.Value != expectedRemaining)
        {
            throw new ParallelFallbackException();
        }

        var segmentPath = GetSegmentPath(partPath, index);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(segmentPath) ?? ".");
        }
        catch (IOException)
        {
            throw new DownloadException("Video dosyası yazılamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new DownloadException("Video dosyasına erişilemedi.");
        }

        // Bekçi akıştan önce bildirilir; bkz. DownloadStallGuard açıklaması.
        using var stall = new DownloadStallGuard(_stallOptions, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(segmentPath,
                                              existingLength > 0 ? FileMode.Append : FileMode.Create,
                                              FileAccess.Write,
                                              FileShare.Read,
                                              BufferSize,
                                              FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (existingLength > 0 && file.Length != existingLength)
        {
            throw new DownloadException("Video parçası indirme sırasında değişti.");
        }

        var buffer = new byte[BufferSize];
        var remaining = expectedRemaining;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readLength = (int)Math.Min(buffer.Length, remaining);
            var read = await stall.ReadAsync(body, buffer.AsMemory(0, readLength)).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
            addBytes(read);
            reportAggregated();
        }

        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (remaining != 0)
        {
            throw new DownloadException("Video indirilemedi; gövde beklenen aralıkta değil.");
        }

        var extraProbe = new byte[1];
        var extra = await stall.ReadAsync(body, extraProbe.AsMemory()).ConfigureAwait(false);
        if (extra != 0)
        {
            throw new DownloadException("Video sunucusu aralık uzunluğu tutarsız.");
        }
    }

    private static async Task MergeSegmentsAsync(string partPath, int segmentCount, CancellationToken cancellationToken)
    {
        try
        {
            await using var output = new FileStream(partPath,
                                                    FileMode.Create,
                                                    FileAccess.Write,
                                                    FileShare.None,
                                                    BufferSize,
                                                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[BufferSize];
            for (var i = 0; i < segmentCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segmentPath = GetSegmentPath(partPath, i);
                await using var input = new FileStream(segmentPath,
                                                       FileMode.Open,
                                                       FileAccess.Read,
                                                       FileShare.Read,
                                                       BufferSize,
                                                       FileOptions.Asynchronous | FileOptions.SequentialScan);
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new DownloadException("Video dosyası yazılamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new DownloadException("Video dosyasına erişilemedi.");
        }
    }

    private sealed class ParallelFallbackException : Exception
    {
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
