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
        TimeSpan?               retryDelay  = null)
        : this(processRunner,
               new HlsDownloadOptions
               {
                   Executable   = executable,
                   MaxAttempts = maxAttempts,
                   RetryDelay  = retryDelay ?? TimeSpan.FromSeconds(1)
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

        DownloadHttp.ValidateHttpUri(sourceUri);
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

        var jobDirectory = Path.Combine(destination.AnimeDirectory,
                                       ".migurdex-job-" + Guid.NewGuid().ToString("N"));
        DownloadPathBuilder.EnsureFullPathBudget(jobDirectory, reservedSuffixBytes: 64);
        Directory.CreateDirectory(jobDirectory);
        var inputPath = Path.Combine(jobDirectory, ".migurdex-input.txt");

        try
        {
            var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 5);
            ExternalProcessResult? lastResult = null;
            string? lastError = null;

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
                ExternalProcessResult result;
                try
                {
                    result = await _processRunner.RunAsync(
                                                    startInfo,
                                                    line => ReportProcessProgress(line, progress),
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
                                          cancellationToken);
                    }

                    lastError = "yt-dlp geçerli medya çıktısı üretmedi.";
                }
                else if (LooksLikeFfmpegFailure(result))
                {
                    lastError = "HLS için ffmpeg gerekiyor ancak ffmpeg bulunamadı veya çalışmıyor.";
                    break;
                }
                else
                {
                    lastError = "yt-dlp HLS indirmeyi tamamlayamadı.";
                }

                if (attempt < maxAttempts)
                {
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
                message = "HLS için ffmpeg gerekiyor ancak ffmpeg bulunamadı veya çalışmıyor.";
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
        startInfo.ArgumentList.Add("--batch-file");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--paths");
        startInfo.ArgumentList.Add(jobDirectory);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add("media.%(ext)s");
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("after_move:filepath");

        // yt-dlp'ye global --add-header yalnız güvenli allowlist ile verilir.
        // Cookie/Authorization/API-key benzeri başlıklar dış sürece hiç taşınmaz.
        foreach (var (name, value) in DownloadHttp.CopyExternalProcessHeaders(source.Headers))
        {
            startInfo.ArgumentList.Add("--add-header");
            startInfo.ArgumentList.Add($"{name}: {value}");
        }

        return startInfo;
    }

    private static void ReportProcessProgress(
        string                         line,
        IProgress<DownloadProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(line) || progress is null)
        {
            return;
        }

        var bytesMatch = ProgressBytesRegex.Match(line);
        if (bytesMatch.Success
            && TryParseSize(bytesMatch.Groups["current"].Value, bytesMatch.Groups["currentUnit"].Value,
                            out var current)
            && TryParseSize(bytesMatch.Groups["total"].Value, bytesMatch.Groups["totalUnit"].Value,
                            out var total))
        {
            progress.Report(new DownloadProgress(DownloadStage.Downloading, current, total));
            return;
        }

        var percentMatch = ProgressPercentRegex.Match(line);
        if (percentMatch.Success
            && double.TryParse(percentMatch.Groups["value"].Value,
                               NumberStyles.Float,
                               CultureInfo.InvariantCulture,
                               out var percent))
        {
            progress.Report(new DownloadProgress(DownloadStage.Downloading,
                                                 (long)Math.Round(percent),
                                                 100));
            return;
        }

        if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[ExtractAudio", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[Fixup", StringComparison.OrdinalIgnoreCase))
        {
            progress.Report(new DownloadProgress(DownloadStage.Finalizing, 0, null));
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

    private static MediaDownloadResult MoveOutput(
        string                       outputPath,
        DownloadPath                 destination,
        bool                         overwrite,
        IProgress<DownloadProgress>? progress,
        CancellationToken             cancellationToken)
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
            File.Move(outputPath, finalPath, overwrite);
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
        return new MediaDownloadResult(finalPath, bytes, bytes, false);
    }

    private static string? FindOutputFile(ExternalProcessResult result, string jobDirectory)
    {
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

        return Directory.EnumerateFiles(jobDirectory, "*", SearchOption.TopDirectoryOnly)
                        .Where(IsMediaFile)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
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

    private static bool LooksLikeFfmpegFailure(ExternalProcessResult result)
    {
        var output = result.StandardOutput + "\n" + result.StandardError;
        return output.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
               && (output.Contains("not found", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("enoent", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("failed", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("bulunamadı", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("çalışmıyor", StringComparison.OrdinalIgnoreCase));
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
