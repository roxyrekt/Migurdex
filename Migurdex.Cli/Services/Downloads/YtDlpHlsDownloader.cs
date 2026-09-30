using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Migurdex.Cli.Services.Downloads;

public sealed class YtDlpHlsDownloader : IHlsDownloader
{
    private static readonly Regex ProgressPercentRegex = new(
        @"(?<value>\d+(?:[.,]\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressBytesRegex = new(
        @"(?<current>\d+(?:[.,]\d+)?)\s*(?<currentUnit>B|KiB|MiB|GiB|TiB)\s*/\s*"
        + @"(?<total>\d+(?:[.,]\d+)?)\s*(?<totalUnit>B|KiB|MiB|GiB|TiB)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressTotalRegex = new(
        @"of\s+(?<estimate>~)?\s*(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>B|KiB|MiB|GiB|TiB)(?!/s)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressSpeedRegex = new(
        @"at\s+(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>B|KiB|MiB|GiB|TiB)/s",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressFragRegex = new(
        @"\(frag\s+(?<done>\d+)/(?<total>\d+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressEtaRegex = new(
        @"ETA\s+(?:(?<hours>\d+):)?(?<minutes>\d{1,2}):(?<seconds>\d{2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressDestinationRegex = new(
        @"^\[download\]\s+Destination:\s*(?<path>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// ffmpeg binary'si eksik. <c>DOWNLOAD.md</c> bu metni birebir anlatıyor,
    /// değiştirilmedi.
    /// </summary>
    internal const string FfmpegMissingMessage =
        "HLS için ffmpeg gerekiyor ancak ffmpeg bulunamadı veya çalışmıyor.";

    /// <summary>
    /// ffmpeg yüklü ama işi yapamadı (segment birleştirme sırasında çöktü).
    /// Eski hâlde bu durum da "ffmpeg gerekiyor" diye bildiriliyordu.
    /// </summary>
    internal const string FfmpegRuntimeMessage =
        "yt-dlp HLS indirmeyi tamamlayamadı; birleştirme sırasında ffmpeg hata verdi.";

    private readonly IExternalProcessRunner _processRunner;
    private readonly HlsDownloadOptions      _options;

    public YtDlpHlsDownloader(IExternalProcessRunner processRunner)
        : this(processRunner, new HlsDownloadOptions())
    {
    }

    public YtDlpHlsDownloader(
        IExternalProcessRunner processRunner,
        HlsDownloadOptions      options)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _options       = options ?? throw new ArgumentNullException(nameof(options));
    }

    public YtDlpHlsDownloader(
        IExternalProcessRunner processRunner,
        string                  executable  = "yt-dlp",
        int                     maxAttempts = 3,
        TimeSpan?               retryDelay  = null,
        int                     concurrentFragments = 5)
        : this(processRunner,
               new HlsDownloadOptions
               {
                   Executable   = executable,
                   MaxAttempts = maxAttempts,
                   RetryDelay  = retryDelay ?? TimeSpan.FromSeconds(1),
                   ConcurrentFragments = concurrentFragments
               })
    {
    }

    public async Task<MediaDownloadResult> DownloadAsync(
        VideoSource                  source,
        DownloadPath                 destination,
        bool                         overwrite           = false,
        IProgress<DownloadProgress>? progress           = null,
        CancellationToken             cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (source.Type != VideoType.M3U8)
        {
            throw new HlsDownloadException("HLS indirici yalnızca M3U8 kaynaklarını kabul eder.");
        }

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var sourceUri))
        {
            throw new HlsDownloadException("HLS kaynak URL'si geçersiz.");
        }

        await DownloadHttp.ValidateHttpUriAsync(sourceUri, cancellationToken).ConfigureAwait(false);
        if (sourceUri.AbsoluteUri.Contains('\r') || sourceUri.AbsoluteUri.Contains('\n'))
        {
            throw new HlsDownloadException("HLS kaynak URL'si geçersiz.");
        }

        Directory.CreateDirectory(destination.AnimeDirectory);
        var lockIdentity = Path.Combine(destination.AnimeDirectory, destination.FileStem);
        DownloadPathBuilder.EnsureFullPathBudget(lockIdentity,
                                                 DownloadPathBuilder.TemporarySuffixUtf8Bytes);
        using var targetLock = await AcquireTargetLockAsync(lockIdentity, cancellationToken)
                                             .ConfigureAwait(false);
        EnsureNoMediaConflict(destination, overwrite);

        // İş dizini indirme klasörünün DIŞINDA, sistem geçici klasöründe tutulur.
        // Ölçülen sızıntı: `taskkill /F` 130.022.831 B, `CTRL_BREAK` 103.792.544 B
        // kalıcı olarak indirme klasöründe bırakıyordu (finally yalnız graceful
        // çıkışta çalışıyor; Ctrl+C temiz, SIGTERM/SIGHUP/SIGKILL değil). Geçici
        // klasöre taşınca kalıntı kullanıcıya görünmez, indirme klasörü
        // kirletilmez ve SweepStaleJobDirectories ile sonraki koşuda temizlenir.
        SweepStaleJobDirectories();
        var jobDirectory = CreateJobDirectory();
        var inputPath = Path.Combine(jobDirectory, ".migurdex-input.txt");

        try
        {
            var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 5);
            ExternalProcessResult? lastResult = null;
            string? lastError = null;
            var attemptWarnings = new List<string>();

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteGeneratedFiles(jobDirectory);
                await File.WriteAllTextAsync(inputPath,
                                            sourceUri.AbsoluteUri + Environment.NewLine,
                                            cancellationToken)
                          .ConfigureAwait(false);
                Report(progress, DownloadStage.Requesting, 0, null);

                var startInfo = BuildStartInfo(source, jobDirectory, inputPath);
                var heartbeat = new HlsProgressHeartbeat();
                var attemptErrors = new List<string>();
                ExternalProcessResult result;
                try
                {
                    result = await _processRunner.RunAsync(
                                                    startInfo,
                                                    line =>
                                                    {
                                                        CaptureErrorLine(line, attemptErrors);
                                                        heartbeat.Report(line, progress);
                                                    },
                                                    cancellationToken)
                                              .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (ConcurrentDownloadException)
                {
                    throw;
                }
                catch (ExternalProcessStartException)
                {
                    throw new HlsDownloadException(
                        "yt-dlp bulunamadı veya çalıştırılamadı. yt-dlp kurun ve PATH'te bulunduğundan emin olun.");
                }
                catch (FileNotFoundException)
                {
                    throw new HlsDownloadException(
                        "yt-dlp bulunamadı veya çalıştırılamadı. yt-dlp kurun ve PATH'te bulunduğundan emin olun.");
                }
                catch (Win32Exception)
                {
                    throw new HlsDownloadException(
                        "yt-dlp bulunamadı veya çalıştırılamadı. yt-dlp kurun ve PATH'te bulunduğundan emin olun.");
                }

                lastResult = result;
                if (result.ExitCode == 0)
                {
                    var outputPath = FindOutputFile(result, jobDirectory);
                    if (outputPath is not null)
                    {
                        return MoveOutput(outputPath,
                                          destination,
                                          overwrite,
                                          progress,
                                          cancellationToken,
                                          attemptWarnings);
                    }

                    lastError = WithCause("yt-dlp geçerli medya çıktısı üretmedi.", attemptErrors);
                }
                else if (LooksLikeFfmpegFailure(result))
                {
                    // ffmpeg binary'si gerçekten yok: tekrar denemek anlamsız,
                    // kullanıcıdan PATH'e kurması gerekiyor.
                    lastError = FfmpegMissingMessage;
                    break;
                }
                else
                {
                    // ffmpeg'in kendisi hata verdiyse mesaj nötrleştirilir:
                    // "ffmpeg kurun" demek yanlış olurdu, gerçek neden (ağ, 404,
                    // disk dolu) ERROR: satırlarıyla birlikte korunur.
                    var fallback = LooksLikeFfmpegRuntimeFailure(result)
                                       ? FfmpegRuntimeMessage
                                       : "yt-dlp HLS indirmeyi tamamlayamadı.";
                    lastError = WithCause(fallback, attemptErrors);
                }

                if (attempt < maxAttempts)
                {
                    attemptWarnings.Add($"Deneme {attempt}/{maxAttempts} başarısız ({ShortCause(lastError)}); tekrar deneniyor.");
                    var delay = _options.RetryDelay < TimeSpan.Zero
                                    ? TimeSpan.Zero
                                    : _options.RetryDelay;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var message = lastError ?? "yt-dlp HLS indirmeyi tamamlayamadı.";
            if (lastResult is not null && LooksLikeFfmpegFailure(lastResult))
            {
                message = FfmpegMissingMessage;
            }

            throw new HlsDownloadException(message);
        }
        catch (OperationCanceledException)
        {
            Report(progress, DownloadStage.Cancelled, 0, null);
            throw;
        }
        catch (ConcurrentDownloadException exception)
        {
            throw new HlsDownloadException(exception.Message);
        }
        catch (HlsDownloadException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new HlsDownloadException("HLS indirilemedi.");
        }
        finally
        {
            DeleteDirectory(jobDirectory);
        }
    }

    private static async Task<IDisposable> AcquireTargetLockAsync(
        string            lockIdentity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadTargetLock.AcquireAsync(lockIdentity, cancellationToken)
                                         .ConfigureAwait(false);
        }
        catch (ConcurrentDownloadException exception)
        {
            throw new HlsDownloadException(exception.Message);
        }
    }

    /// <summary>
    /// yt-dlp iş dizininin kökü. Tek bir bilinen yer kullanmak, kalıntıların
    /// nereye bırakıldığını izlenebilir kılar ve <see cref="SweepStaleJobDirectories"/>
    /// ile hepsini topluca temizlemeye izin verir.
    /// </summary>
    internal static string JobRootDirectory => Path.Combine(Path.GetTempPath(), "migurdex-jobs");

    /// <summary>
    /// Zorla sonlandırılmış (SIGKILL, SIGTERM, SIGHUP) koşulardan kalan iş
    /// dizinlerini siler. Eşik saat olarak seçildi: normal bir HLS indirmesi
    /// saatlerce sürebildiği için çok kısa bir eşik uzun süren geçerli bir
    /// indirmeyi silerdi.
    /// </summary>
    internal static int SweepStaleJobDirectories(TimeSpan? maxAge = null)
    {
        var root = JobRootDirectory;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var threshold = DateTime.UtcNow - (maxAge ?? TimeSpan.FromHours(6));
        var removed    = 0;
        foreach (var candidate in Directory.EnumerateDirectories(root))
        {
            // Yalnız bizim ürettiğimiz GUID adlı dizinlere dokun; geçici klasör
            // başka uygulamalarla paylaşılıyor.
            if (!Guid.TryParse(Path.GetFileName(candidate), out _))
            {
                continue;
            }

            if (SafeDirectoryAgeUtc(candidate) > threshold)
            {
                continue;
            }

            if (TryDeleteDirectory(candidate))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }

            return !Directory.Exists(directory);
        }
        catch
        {
            return false;
        }
    }

    private static DateTime SafeDirectoryAgeUtc(string directory)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(directory);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }

    private static string CreateJobDirectory()
    {
        var jobDirectory = Path.Combine(JobRootDirectory, Guid.NewGuid().ToString("N"));
        DownloadPathBuilder.EnsureFullPathBudget(jobDirectory, reservedSuffixBytes: 64);
        Directory.CreateDirectory(jobDirectory);
        return jobDirectory;
    }

    private ProcessStartInfo BuildStartInfo(
        VideoSource source,
        string      jobDirectory,
        string      inputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = string.IsNullOrWhiteSpace(_options.Executable) ? "yt-dlp" : _options.Executable,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = jobDirectory
        };

        startInfo.ArgumentList.Add("--no-config");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--no-part");
        startInfo.ArgumentList.Add("--newline");
        // --print after_move:filepath bilerek kullanılmıyor: --print, [download]
        // Destination satırlarını bastırıyor ve track takibi kör kalıyor. Final
        // dosya bunun yerine iş dizinindeki en yeni medya dosyasından bulunuyor.
        startInfo.ArgumentList.Add("--progress");
        startInfo.ArgumentList.Add("--batch-file");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--paths");
        startInfo.ArgumentList.Add(jobDirectory);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add("media.%(ext)s");
        startInfo.ArgumentList.Add("--concurrent-fragments");
        startInfo.ArgumentList.Add(Math.Clamp(_options.ConcurrentFragments, 1, 16)
                                      .ToString(CultureInfo.InvariantCulture));

        // yt-dlp'ye global --add-header yalnız güvenli allowlist ile verilir.
        // Cookie/Authorization/API-key benzeri başlıklar dış sürece hiç taşınmaz.
        foreach (var (name, value) in DownloadHttp.CopyExternalProcessHeaders(source.Headers))
        {
            startInfo.ArgumentList.Add("--add-header");
            startInfo.ArgumentList.Add($"{name}: {value}");
        }

        return startInfo;
    }

    /// <summary>
    /// yt-dlp <c>--newline</c> ilerleme satırlarını <see cref="DownloadProgress"/>'e
    /// çeviren durum makinesi. <c>internal</c> (Migurdex.Tests
    /// <c>InternalsVisibleTo</c> ile erişir) çünkü monoton toplam kuralı ve
    /// faz geçişi ayrımı satır düzeyinde doğrudan doğrulanmalıdır.
    /// </summary>
    internal sealed class HlsProgressHeartbeat
    {
        private long _bytes;
        private long? _total;
        private string? _track;
        private int _trackCount;

        // BULGU 23: yt-dlp `of ~Y` toplamını her satırda YENİDEN TAHMİN eder ve
        // tahmin can dalgalar. Canlı ölçüm (aynı indirme, tek dosya):
        //   1 KiB → 7.81 MiB → 177.03 MiB → 434.27 MiB → 521.3 MiB → 362.26 MiB → 280.65 MiB
        // Yüzde zaten [0,100] aralığına kırpılıyor ama PAYDA (toplam) kırpılmadığı
        // için ilerleme çubuğu sürekli geri gidiyordu. Tahmin edilen toplam, faz
        // içinde monoton artan yapılır; tahminin kendisi değişmez.
        private long? _estimatedTotalFloor;

        // Frag sayacı faz geçişini bildirir: HLS'te iki gerçek faz var
        // (segment ham ~277 MiB → mux sonrası ~260 MiB) ve toplam gerçekten
        // küçülebilir. Sayaç %100'e ulaşıp yeniden başladığında monotonluk
        // sıfırlanır; aksi halde kilit tutulur.
        private int? _lastFragDone;
        private bool _fragPhaseCompleted;

        public void Report(string line, IProgress<DownloadProgress>? progress)
        {
            if (string.IsNullOrWhiteSpace(line) || progress is null)
            {
                return;
            }

            var destination = ProgressDestinationRegex.Match(line);
            if (destination.Success)
            {
                var name = TrackName(destination.Groups["path"].Value);
                if (!string.IsNullOrWhiteSpace(name)
                    && !string.Equals(name, _track, StringComparison.Ordinal))
                {
                    _track = name;
                    _trackCount++;
                    BeginNewTrack();
                    progress.Report(new DownloadProgress(DownloadStage.Downloading,
                                                         0,
                                                         null,
                                                         track: _track,
                                                         isAudioTrack: _trackCount > 1));
                }

                return;
            }

            var speed = TryParseSpeed(line);
            var (fragDone, fragTotal) = TryParseFrags(line);
            ObserveFragments(fragDone, fragTotal);
            var eta = TryParseEta(line);

            var bytesMatch = ProgressBytesRegex.Match(line);
            if (bytesMatch.Success
                && TryParseSize(bytesMatch.Groups["current"].Value, bytesMatch.Groups["currentUnit"].Value,
                                out var current)
                && TryParseSize(bytesMatch.Groups["total"].Value, bytesMatch.Groups["totalUnit"].Value,
                                out var total))
            {
                // `X / Y` biçimi yt-dlp'nin BİLİNEN gerçek toplamı, tahmin değil:
                // monoton kural uygulanmaz (aksi halde kesin toplam, önceki tahmin
                // yüzünden şişirilmiş bir değerle değiştirilirdi).
                _bytes = current;
                _total = total;
                progress.Report(new DownloadProgress(DownloadStage.Downloading, current, total, speed, fragDone, fragTotal, eta, null, _track, _trackCount > 1));
                return;
            }

            var percentMatch = ProgressPercentRegex.Match(line);
            if (percentMatch.Success
                && double.TryParse(percentMatch.Groups["value"].Value,
                                   NumberStyles.Float,
                                   CultureInfo.InvariantCulture,
                                   out var percent))
            {
                var totalMatch = ProgressTotalRegex.Match(line);
                if (totalMatch.Success
                    && TryParseSize(totalMatch.Groups["value"].Value, totalMatch.Groups["unit"].Value,
                                    out var percentTotal))
                {
                    // yt-dlp toplamı `~` ile yazdığında bunun bir **tahmin**
                    // olduğunu bildiğimiz kesin. Tahmin kesin toplam gibi
                    // sunulursa ilerleme yanlış bir %100 gösterir (ölçüm:
                    // tahmin 1,25 GiB, gerçek dosya 486,16 MiB). Bu yüzden yüzde
                    // ve ETA tahminden türetilmez; parça sayacı kullanılır.
                    var isEstimated = totalMatch.Groups["estimate"].Success;
                    percentTotal = MonotonicEstimatedTotal(percentTotal);
                    var downloaded = (long)Math.Round(percent / 100 * percentTotal);
                    _bytes = downloaded;
                    _total = percentTotal;
                    progress.Report(new DownloadProgress(DownloadStage.Downloading,
                                                          downloaded,
                                                          percentTotal,
                                                          speed,
                                                          fragDone,
                                                          fragTotal,
                                                          isEstimated ? null : eta,
                                                          isEstimated ? null : percent,
                                                          _track,
                                                          _trackCount > 1)
                                     {
                                         IsEstimatedTotal = isEstimated
                                     });
                    return;
                }

                progress.Report(new DownloadProgress(DownloadStage.Downloading, _bytes, _total, speed, fragDone, fragTotal, eta, percent, _track, _trackCount > 1));
                return;
            }

            if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[ExtractAudio", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[Fixup", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report(new DownloadProgress(DownloadStage.Finalizing, 0, null));
                return;
            }

            if (line.Contains("[download]", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report(new DownloadProgress(DownloadStage.Downloading, _bytes, _total, speed, fragDone, fragTotal, eta, null, _track, _trackCount > 1));
            }
        }

        /// <summary>
        /// Yeni track (farklı <c>Destination:</c> satırı) başladığında tüm
        /// faz durumu sıfırlanır. Ayrı indirilen bir track'in toplamı öncekinin
        /// altında olabilir; bu bir faz geçişi değil, ayrı dosya.
        /// </summary>
        private void BeginNewTrack()
        {
            _bytes = 0;
            _total = null;
            ResetEstimatedTotal();
        }

        private void ResetEstimatedTotal()
        {
            _estimatedTotalFloor = null;
            _lastFragDone = null;
            _fragPhaseCompleted = false;
        }

        /// <summary>
        /// Tahmin edilen toplamı aynı faz içinde monoton artan yapar.
        /// Yeni tahmin eskisinden küçükse yok sayılır; payda geri gitmez.
        /// </summary>
        private long MonotonicEstimatedTotal(long candidate)
        {
            _estimatedTotalFloor = _estimatedTotalFloor is { } floor
                                       ? Math.Max(floor, candidate)
                                       : candidate;
            return _estimatedTotalFloor.Value;
        }

        /// <summary>
        /// Frag sayacını izleyip gerçek faz geçişini yakalar. Sadece sayac
        /// %100'e ulaştıktan sonra **yeniden başladığında** (done küçüldü)
        /// sıfırlama yapılır; sıfırlanmış gibi görünen ama %100'e hiç ulaşmamış
        /// sayaçlar (ör. track değişimi) kilidi bozmaz.
        /// </summary>
        private void ObserveFragments(int? done, int? total)
        {
            if (done is not { } currentDone || total is not { } currentTotal || currentTotal <= 0)
            {
                return;
            }

            if (_fragPhaseCompleted
                && _lastFragDone is { } previousDone
                && currentDone < previousDone)
            {
                // frag %100'e ulaştı ve yeniden başladı → segment → mux fazı.
                // Gerçek faz geçişi: toplam küçülebilir, monotonluğu bırak.
                _total = null;
                ResetEstimatedTotal();
            }

            if (currentDone >= currentTotal)
            {
                _fragPhaseCompleted = true;
            }

            _lastFragDone = currentDone;
        }

        private static TimeSpan? TryParseEta(string line)
        {
            var match = ProgressEtaRegex.Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups["minutes"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                || !int.TryParse(match.Groups["seconds"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                || seconds is < 0 or > 59
                || minutes < 0)
            {
                return null;
            }

            var hours = 0;
            if (match.Groups["hours"].Success
                && !int.TryParse(match.Groups["hours"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
            {
                return null;
            }

            try
            {
                return new TimeSpan(hours, minutes, seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private static string? TrackName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string name;
            try
            {
                name = Path.GetFileName(path.Trim());
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return name.Length > 48 ? name[..48] + "…" : name;
        }

        private static (int? Done, int? Total) TryParseFrags(string line)
        {
            var match = ProgressFragRegex.Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups["done"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var done)
                || !int.TryParse(match.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
                || done < 0
                || total <= 0)
            {
                return (null, null);
            }

            return (done, total);
        }

        private static double? TryParseSpeed(string line)
        {
            var match = ProgressSpeedRegex.Match(line);
            if (!match.Success
                || !TryParseSize(match.Groups["value"].Value, match.Groups["unit"].Value, out var bytes))
            {
                return null;
            }

            return bytes;
        }
    }

    private static bool TryParseSize(string value, string unit, out long bytes)
    {
        bytes = 0;
        if (!double.TryParse(value,
                             NumberStyles.Float,
                             CultureInfo.InvariantCulture,
                             out var number))
        {
            return false;
        }

        var multiplier = unit.ToLowerInvariant() switch
        {
            "b"    => 1D,
            "kib"  => 1024D,
            "mib"  => 1024D * 1024,
            "gib"  => 1024D * 1024 * 1024,
            "tib"  => 1024D * 1024 * 1024 * 1024,
            _      => 0D
        };
        if (multiplier <= 0 || !double.IsFinite(number))
        {
            return false;
        }

        var result = number * multiplier;
        if (result <= 0 || result > long.MaxValue)
        {
            return false;
        }

        bytes = (long)result;
        return true;
    }

    internal static MediaDownloadResult MoveOutputForTest(
        string        outputPath,
        DownloadPath  destination,
        bool          overwrite)
    {
        return MoveOutput(outputPath, destination, overwrite, null, CancellationToken.None);
    }

    private static MediaDownloadResult MoveOutput(
        string                       outputPath,
        DownloadPath                 destination,
        bool                         overwrite,
        IProgress<DownloadProgress>? progress,
        CancellationToken             cancellationToken,
        IReadOnlyList<string>?        warnings = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(outputPath);
        if (string.IsNullOrWhiteSpace(extension) || !IsMediaExtension(extension))
        {
            throw new HlsDownloadException("yt-dlp geçerli medya dosyası üretmedi.");
        }

        var finalPath = destination.GetMediaPath(extension);
        if (!overwrite && File.Exists(finalPath))
        {
            throw new HlsDownloadException("Video hedefi zaten var; overwrite kapalı.");
        }

        var bytes = new FileInfo(outputPath).Length;
        Report(progress, DownloadStage.Downloading, bytes, bytes);
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, DownloadStage.Finalizing, bytes, bytes);

        try
        {
            MoveFileAcrossVolumes(outputPath, finalPath, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(finalPath))
        {
            throw new HlsDownloadException("Video hedefi zaten var; overwrite kapalı.");
        }
        catch (IOException)
        {
            throw new HlsDownloadException("HLS medya dosyası taşınamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new HlsDownloadException("HLS medya dosyası taşınamadı.");
        }

        bytes = new FileInfo(finalPath).Length;
        Report(progress, DownloadStage.Completed, bytes, bytes);
        return new MediaDownloadResult(finalPath, bytes, bytes, false, warnings);
    }

    /// <summary>
    /// İş dizini artık sistem geçici klasöründe olduğu için hedef klasörle
    /// **farklı dosya sisteminde** olabilir (Linux'ta `/tmp` çoğu dağıtımda ayrı
    /// bir tmpfs/disk'tir; `TMPDIR` değiştirilmiş olabilir). .NET'in
    /// <see cref="File.Move(string,string,bool)"/> çağrısı Unix'te `rename(2)`
    /// kullanır ve farklı dosya sistemlerinde `EXDEV` ile başarısız olur.
    ///
    /// Bu yüzden önce atomik taşıma denenir; `EXDEV` karşılığı olan
    /// <see cref="IOException"/> gelirse kopyala-sil yedeğine düşülür. Aynı
    /// dosya sisteminde davranış değişmez (taşıma hâlâ atomik).
    /// </summary>
    private static void MoveFileAcrossVolumes(string sourcePath, string finalPath, bool overwrite)
    {
        // `CopyThenDelete` taşıma sırasında `<hedef>.migurdex-partial` yazıyor.
        // SIGKILL veya güç kesintisi `File.Copy` ile `File.Move` arasında
        // yakalanırsa bu dosya kalıcı çöp olarak kalıyor ve sonraki çalışmada
        // temizlenmiyordu (aynı dosya sisteminde taşıma başarılı olursa
        // `CopyThenDelete` hiç çağrılmıyor). Kilit dosyası için bu yol açıldı,
        // burada da aynı kural uygulanıyor: yalnızca kimse tutmuyorsa dokunulur.
        TryRemoveStalePartialFile(finalPath);

        try
        {
            File.Move(sourcePath, finalPath, overwrite);
            return;
        }
        catch (IOException ex) when (ShouldFallBackToCopy(ex, sourcePath, finalPath))
        {
            // Aşağıda kopyala-sil.
        }

        CopyThenDelete(sourcePath, finalPath, overwrite);
    }

    /// <summary>
    /// Taşıma hatası kopyala-sil yedeğini gerektiriyor mu?
    ///
    /// Unix'te yalnızca <c>EXDEV</c> (errno 18) gerektirir. Önceden Unix'te
    /// "her zaman farklı dosya sistemi" varsayılıyordu; bu yüzden disk dolu,
    /// izin yok veya hedef kilitli gibi <c>EXDEV</c> dışı hatalarda da tam boy
    /// kopyalama deniyordu — hepsi zaten başarısız olacak, yalnızca kullanıcı
    /// boşuna bekliyordu ve bir <c>.migurdex-partial</c> bırakıp siliniyordu.
    ///
    /// Windows'ta taşıma birbirine bağlı dosya sistemleri arasında
    /// <c>EXDEV</c> üretmez; orada kök karşılaştırması kullanılmaya devam eder.
    ///
    /// <b>Linux ölçüm notu:</b> .NET'in <see cref="File.Move(string,string,bool)"/>
    /// Unix'te <c>EXDEV</c>'i çoğu zaman <em>kendi içinde</em> copy+delete ile
    /// yutuyor, dolayısıyla bu yedek yol sık tetiklenmez (ölçüldü: 5 senaryonun
    /// tamamında `File.Move` doğrudan başarılı oldu). Yedek <b>bilerek korundu</b>:
    /// farklı .NET sürümlerinde, özel dosya sistemlerinde ya da kopyalama
    /// seçenekleri değiştiğinde gerekebilir. Bilinen yan etkisi: .NET kendi
    /// yedeğini kullandığında kopyalama doğrudan nihai yola yapılır, dolayısıyla
    /// <see cref="CopyThenDelete"/>'nin sağladığı <c>.migurdex-partial</c> ara
    /// dosyasının atomiklik koruması o yolda devreye girmez. Sonuç yine de aynıdır;
    /// ara dosya adı farklı olduğu için kısmi dosya temizliği geçerlidir.
    /// </summary>
    private static bool ShouldFallBackToCopy(IOException ex, string sourcePath, string finalPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return IsCrossDeviceLink(ex);
        }

        try
        {
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
            var finalRoot  = Path.GetPathRoot(Path.GetFullPath(finalPath));
            if (string.IsNullOrEmpty(sourceRoot) || string.IsNullOrEmpty(finalRoot))
            {
                return true;
            }

            return !string.Equals(sourceRoot, finalRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception rootEx) when (rootEx is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// <see cref="IOException"/> <c>EXDEV</c> ile mi geldi? .NET, Unix'te errno
    /// değerini <c>HResult</c>'ın düşük 16 bitine koyar; iç içe zincirde de
    /// aranır.
    /// </summary>
    private static bool IsCrossDeviceLink(IOException ex)
    {
        const int exdevErrno = 18;
        for (var current = ex; current is not null; current = current.InnerException as IOException)
        {
            if ((current.HResult & 0xFFFF) == exdevErrno)
            {
                return true;
            }
        }

        return false;
    }

    private static void TryRemoveStalePartialFile(string finalPath)
    {
        var partialPath = finalPath + ".migurdex-partial";
        try
        {
            if (!File.Exists(partialPath))
            {
                return;
            }

            // Silme önce **yoklama akışı açıkken** denenir. Unix'te `FileShare.None`
            // `flock` alır, böylece silme anında başka bir süreç dosyayı açamaz ve
            // yarış penceresi kapanır. Windows'ta açık dosya silinemediği için deneme
            // `IOException` verir; akış kapanır ve aynı silme bir kez daha denenir.
            var deletedWhileOpen = false;
            using (var probe = new FileStream(partialPath,
                                            FileMode.Open,
                                            FileAccess.ReadWrite,
                                            FileShare.None,
                                            bufferSize: 1,
                                            FileOptions.None))
            {
                try
                {
                    File.Delete(partialPath);
                    deletedWhileOpen = true;
                }
                catch (IOException)
                {
                    // Windows: akış kapanınca tekrar denenecek.
                }
            }

            if (!deletedWhileOpen)
            {
                File.Delete(partialPath);
            }
        }
        catch (IOException)
        {
            // Başka bir işlem tutuyor; normal akış devreder.
        }
        catch (UnauthorizedAccessException)
        {
            // Klasör yazma izni yoksa zaten taşıma da başarısız olur.
        }
    }

    private static void CopyThenDelete(string sourcePath, string finalPath, bool overwrite)
    {
        if (!overwrite && File.Exists(finalPath))
        {
            throw new IOException("Hedef dosya zaten var.");
        }

        var temporaryPath = finalPath + ".migurdex-partial";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: true);
            if (overwrite)
            {
                File.Move(temporaryPath, finalPath, overwrite: true);
            }
            else
            {
                File.Move(temporaryPath, finalPath);
            }

            File.Delete(sourcePath);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static readonly Regex ErrorUrlScrubRegex = new(
        @"https?://\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void CaptureErrorLine(string line, List<string> sink)
    {
        if (!line.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sink.Add(SanitizeProcessLine(line));
        while (sink.Count > 3)
        {
            sink.RemoveAt(0);
        }
    }

    private static string SanitizeProcessLine(string line)
    {
        var clean = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
        clean = ErrorUrlScrubRegex.Replace(clean, "[adres]");
        return clean.Length > 200 ? clean[..200] + "…" : clean;
    }

    private static string WithCause(string fallback, List<string> errors)
    {
        if (errors.Count == 0)
        {
            return fallback;
        }

        return fallback + " " + string.Join(" ", errors.TakeLast(2));
    }

    private static string ShortCause(string message)
    {
        var clean = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length > 160 ? clean[..160] + "…" : clean;
    }

    private static string? FindOutputFile(ExternalProcessResult result, string jobDirectory)
    {
        var merged = Directory.EnumerateFiles(jobDirectory, "*", SearchOption.TopDirectoryOnly)
                              .Where(IsMediaFile)
                              .OrderByDescending(File.GetLastWriteTimeUtc)
                              .FirstOrDefault();
        if (merged is not null)
        {
            return merged;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.OutputPath))
        {
            candidates.Add(result.OutputPath);
        }

        if (result.OutputFiles is not null)
        {
            candidates.AddRange(result.OutputFiles);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            candidates.AddRange(result.StandardOutput.Split(['\r', '\n'],
                                                          StringSplitOptions.RemoveEmptyEntries
                                                          | StringSplitOptions.TrimEntries));
        }

        foreach (var candidate in candidates)
        {
            var resolved = ResolveCandidate(candidate, jobDirectory);
            if (resolved is not null
                && IsInsideDirectory(resolved, jobDirectory)
                && IsMediaFile(resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ResolveCandidate(string candidate, string jobDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            return Path.GetFullPath(Path.IsPathRooted(candidate)
                                        ? candidate
                                        : Path.Combine(jobDirectory, candidate));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
            var comparison = OperatingSystem.IsWindows()
                                 ? StringComparison.OrdinalIgnoreCase
                                 : StringComparison.Ordinal;
            return !Path.IsPathRooted(relative)
                   && !relative.Equals("..", comparison)
                   && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)
                   && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, comparison);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMediaFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (!IsMediaExtension(extension))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                return false;
            }

            using var stream = new FileStream(path,
                                              FileMode.Open,
                                              FileAccess.Read,
                                              FileShare.Read,
                                              512,
                                              FileOptions.SequentialScan);
            var buffer = new byte[512];
            var read   = stream.Read(buffer, 0, buffer.Length);
            var text   = System.Text.Encoding.UTF8.GetString(buffer, 0, read)
                              .TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            return !text.StartsWith("<", StringComparison.Ordinal)
                   && !text.StartsWith("{", StringComparison.Ordinal)
                   && !text.StartsWith("[", StringComparison.Ordinal)
                   && !text.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMediaExtension(string extension)
    {
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".avi", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".flv", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".mpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".mpeg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".3gp", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ogv", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".opus", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoMediaConflict(DownloadPath destination, bool overwrite)
    {
        if (overwrite || !Directory.Exists(destination.AnimeDirectory))
        {
            return;
        }

        var conflict = Directory.EnumerateFiles(destination.AnimeDirectory, "*", SearchOption.TopDirectoryOnly)
                                   .Where(IsMediaFile)
                                   .FirstOrDefault(path =>
                                   {
                                       var name = Path.GetFileNameWithoutExtension(path);
                                       return name.Equals(destination.FileStem, StringComparison.OrdinalIgnoreCase)
                                              || name.StartsWith(destination.FileStem + ".",
                                                                StringComparison.OrdinalIgnoreCase);
                                   });
        if (conflict is not null)
        {
            throw new HlsDownloadException("Video hedefi zaten var; overwrite kapalı.");
        }
    }

    /// <summary>
    /// Araç adıyla birlikte geçen, binary'nin **kendisinin bulunamadığına**
    /// işaret eden kalıplar. Genel "failed" bilinçli olarak yok: yt-dlp neredeyse
    /// her hatada "failed" yazar (ağ, 404, disk dolu) ve bu satırlar çıktının
    /// herhangi bir yerinde "ffmpeg" kelimesi geçtiği anda "ffmpeg gerekiyor"
    /// teşhisi tetikliyordu → gerçek neden gizleniyordu.
    /// </summary>
    private static readonly string[] MissingBinaryPatterns =
    [
        "is not installed",
        "not installed",
        "no such file",
        "not found",
        "is not recognized",
        "command not found",
        "enoent",
        "cannot be found",
        "could not be found",
        "unable to find",
        "no ffmpeg",
        "ffmpeg-location",
        "yüklü değil",
        "bulunamad",
        "çalışm"
    ];

    /// <summary>
    /// ffmpeg'e **özgü** hata sinyalleri. Bunlar "ffmpeg yok" değil, "ffmpeg var
    /// ama işi yapamadı" demektir; kullanıcıya "ffmpeg kurun" demek yanlış olurdu.
    /// </summary>
    private static readonly string[] FfmpegRuntimePatterns =
    [
        "exited with code",
        "exit code",
        "exit status",
        "error while",
        "invalid data",
        "could not write",
        "unknown encoder",
        "no such filter",
        "permission denied"
    ];

    private static readonly string[] FfmpegToolNames = ["ffmpeg", "ffprobe", "avconv"];

    /// <summary>
    /// C6: ffmpeg teşhisi **satır bazında** ve **araç adı + eksiklik sinyali
    /// aynı satırda** olacak şekilde daraltıldı.
    ///
    /// Önceki hâli: <c>output.Contains("ffmpeg") &amp;&amp; output.Contains("failed")</c>
    /// — iki koşul da tüm stdout+stderr kümesinde aranıyordu. yt-dlp bir 404
    /// hatası verdiğinde ("ERROR: ... HTTP Error 404: Not Found ... failed")
    /// stderr'in herhangi bir yerinde "ffmpeg" geçtiği anda kullanıcı
    /// "HLS için ffmpeg gerekiyor" mesajını alıyor, gerçek neden (ağ/404/disk
    /// dolu) <c>WithCause</c> ile eklenmeden atılıyordu.
    ///
    /// Satır bazlı eşleşme ayrıca URL tuzağını da kapatır: yt-dlp hata
    /// satırlarına ham URL yazdığı için
    /// "https://cdn/.../ffmpeg-master/frag12.ts: HTTP Error 404: Not Found"
    /// satırı hem "ffmpeg" hem "not found" içerir. URL'ler önce
    /// <see cref="ErrorUrlScrubRegex"/> ile maskelendiği için bu satır artık
    /// eşleşmez.
    /// </summary>
    internal static bool LooksLikeFfmpegFailure(ExternalProcessResult result)
    {
        return MatchesFfmpegLine(result.StandardOutput)
               || MatchesFfmpegLine(result.StandardError);
    }

    /// <summary>
    /// ffmpeg adı geçen bir hata satırı var mı — ama **eksiklik** mi, yoksa
    /// **çalışma hatası** mı? İkisi farklı mesaj verir.
    /// </summary>
    internal static bool LooksLikeFfmpegRuntimeFailure(ExternalProcessResult result)
    {
        return MatchesFfmpegPattern(result.StandardOutput, FfmpegRuntimePatterns)
               || MatchesFfmpegPattern(result.StandardError, FfmpegRuntimePatterns);
    }

    private static bool MatchesFfmpegLine(string output)
    {
        return MatchesFfmpegPattern(output, MissingBinaryPatterns);
    }

    private static bool MatchesFfmpegPattern(string output, string[] patterns)
    {
        if (string.IsNullOrEmpty(output))
        {
            return false;
        }

        foreach (var raw in output.Split(['\r', '\n'],
                                        StringSplitOptions.RemoveEmptyEntries
                                        | StringSplitOptions.TrimEntries))
        {
            // URL'ler önce maskelenir: 404 satırı "not found" içerir ve
            // "ffmpeg" bir dosya adının parçası olabilir.
            var line = ErrorUrlScrubRegex.Replace(raw, " [adres] ");
            if (!MentionsFfmpeg(line))
            {
                continue;
            }

            foreach (var pattern in patterns)
            {
                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MentionsFfmpeg(string line)
    {
        foreach (var tool in FfmpegToolNames)
        {
            if (line.Contains(tool, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void DeleteGeneratedFiles(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                TryDelete(file);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
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
