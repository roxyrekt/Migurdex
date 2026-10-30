using Migurdex.Cli.Services.Downloads;
using System.Diagnostics;

namespace Migurdex.Cli.Dependencies;

public sealed class WindowsDependencyInstaller : IDependencyInstaller
{
    private const string MpvWingetId = "shinchiro.mpv";

    private const string FfmpegBase   = "https://www.gyan.dev/ffmpeg/builds/";
    private const string Ffmpeg7z     = FfmpegBase + "ffmpeg-release-essentials.7z";
    private const string Ffmpeg7zSha  = FfmpegBase + "ffmpeg-release-essentials.7z.sha256";
    private const string FfmpegZip    = FfmpegBase + "ffmpeg-release-essentials.zip";
    private const string FfmpegZipSha = FfmpegBase + "ffmpeg-release-essentials.zip.sha256";

    private const long MinimumExecutableBytes = 256 * 1024;

    private readonly HttpClient             _httpClient;
    private readonly IExternalProcessRunner _processRunner;
    private readonly string                 _toolsDirectory;
    private readonly DependencyVersionStore _versions;

    public WindowsDependencyInstaller(
        string                  toolsDirectory,
        IExternalProcessRunner? processRunner = null,
        HttpClient?             httpClient    = null)
    {
        _toolsDirectory = toolsDirectory ?? throw new ArgumentNullException(nameof(toolsDirectory));
        _processRunner  = processRunner ?? new ExternalProcessRunner();
        _httpClient     = httpClient ?? DependencyDownload.CreateHttpClient();
        _versions       = new DependencyVersionStore(_toolsDirectory);
    }

    public string PlatformName => "Windows";

    public bool CanAutoInstall => true;

    public IReadOnlyCollection<DependencyKind> SupportedTools { get; } =
        [DependencyKind.Mpv, DependencyKind.YtDlp, DependencyKind.Ffmpeg];

    public string GetManualInstructions(DependencyKind kind)
    {
        return kind switch
        {
            DependencyKind.Mpv =>
                "MPV kurulu değil. 'migurdex deps install mpv' çalıştırın; alternatif olarak "
                + "'winget install shinchiro.mpv' veya https://mpv.io/installation adresini kullanın.",
            DependencyKind.YtDlp =>
                "yt-dlp kurulu değil. 'migurdex deps install yt-dlp' çalıştırın veya "
                + "https://github.com/yt-dlp/yt-dlp/releases adresinden indirin.",
            DependencyKind.Ffmpeg =>
                "ffmpeg kurulu değil. 'migurdex deps install ffmpeg' çalıştırın veya "
                + "https://www.gyan.dev/ffmpeg/builds/ adresinden indirin.",
            _ => $"{DependencyCatalog.DisplayName(kind)} kurulu değil."
        };
    }

    public async Task<DependencyInstallResult> InstallAsync(
        DependencyKind     kind,
        IProgress<string>? progress          = null,
        CancellationToken  cancellationToken = default)
    {
        Directory.CreateDirectory(_toolsDirectory);

        return kind switch
        {
            DependencyKind.YtDlp  => await InstallYtDlpAsync(progress, cancellationToken).ConfigureAwait(false),
            DependencyKind.Ffmpeg => await InstallFfmpegAsync(progress, cancellationToken).ConfigureAwait(false),
            DependencyKind.Mpv    => await InstallMpvAsync(progress, cancellationToken).ConfigureAwait(false),
            _                     => DependencyInstallResult.Failed(kind, GetManualInstructions(kind))
        };
    }

    private async Task<DependencyInstallResult> InstallYtDlpAsync(
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        var target = Path.Combine(_toolsDirectory, DependencyCatalog.FileName(DependencyKind.YtDlp));

        try
        {
            progress?.Report("yt-dlp sürüm bilgisi alınıyor...");

            var release = await DependencyReleaseCatalog.ResolveLatestAsync(_httpClient,
                                                                            DependencyKind.YtDlp,
                                                                            cancellationToken)
                                                        .ConfigureAwait(false);

            if (release is null)
            {
                return DependencyInstallResult.Failed(DependencyKind.YtDlp,
                                                      "yt-dlp sürüm bilgisi alınamadı.");
            }

            await DependencyDownload.DownloadFileAsync(_httpClient,
                                                       release.DownloadUrl,
                                                       target,
                                                       release.Sha256,
                                                       progress,
                                                       cancellationToken)
                                    .ConfigureAwait(false);

            if (!await IsUsableExecutableAsync(target, cancellationToken).ConfigureAwait(false))
            {
                TryDelete(target);

                return DependencyInstallResult.Failed(DependencyKind.YtDlp,
                                                      "İndirilen yt-dlp geçerli bir çalıştırılabilir dosya değil.");
            }

            _versions.Write(DependencyKind.YtDlp, release.Tag);

            return DependencyInstallResult.Succeeded(DependencyKind.YtDlp,
                                                     target,
                                                     $"yt-dlp {release.Tag} indirildi ve doğrulandı.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return DependencyInstallResult.Failed(DependencyKind.YtDlp, $"yt-dlp kurulamadı: {ex.Message}");
        }
    }

    private async Task<DependencyInstallResult> InstallFfmpegAsync(
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        var fromGitHub = await TryInstallFfmpegFromGitHubAsync(progress, cancellationToken)
                             .ConfigureAwait(false);

        if (fromGitHub is not null)
        {
            return fromGitHub;
        }

        progress?.Report("GitHub mirror'ı kullanılamadı; gyan.dev deneniyor...");

        var attempt7z = await TryInstallFfmpegFromArchiveAsync(Ffmpeg7z,
                                                               Ffmpeg7zSha,
                                                               ".7z",
                                                               progress,
                                                               cancellationToken)
                            .ConfigureAwait(false);

        if (attempt7z is not null)
        {
            return attempt7z;
        }

        progress?.Report("7z indirilemedi; zip arşivi deneniyor...");

        var attemptZip = await TryInstallFfmpegFromArchiveAsync(FfmpegZip,
                                                                FfmpegZipSha,
                                                                ".zip",
                                                                progress,
                                                                cancellationToken)
                             .ConfigureAwait(false);

        return attemptZip
               ?? DependencyInstallResult.Failed(DependencyKind.Ffmpeg,
                                                 "ffmpeg indirilemedi (7z ve zip kaynakları başarısız).");
    }

    private async Task<DependencyInstallResult?> TryInstallFfmpegFromGitHubAsync(
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        var target      = Path.Combine(_toolsDirectory, DependencyCatalog.FileName(DependencyKind.Ffmpeg));
        var archivePath = Path.Combine(_toolsDirectory, ".ffmpeg-gh-" + Guid.NewGuid().ToString("N") + ".7z");

        try
        {
            progress?.Report("ffmpeg sürümü sorgulanıyor (GitHub mirror)...");

            var release = await DependencyReleaseCatalog.ResolveLatestAsync(_httpClient,
                                                                            DependencyKind.Ffmpeg,
                                                                            cancellationToken)
                                                        .ConfigureAwait(false);
            if (release is null)
            {
                return null;
            }

            await DependencyDownload.DownloadFileAsync(_httpClient,
                                                       release.DownloadUrl,
                                                       archivePath,
                                                       release.Sha256,
                                                       progress,
                                                       cancellationToken)
                                    .ConfigureAwait(false);

            progress?.Report("ffmpeg arşivi açılıyor...");

            if (!DependencyArchive.TryExtractFile(archivePath,
                                                  "ffmpeg.exe",
                                                  target,
                                                  progress,
                                                  cancellationToken))
            {
                return DependencyInstallResult.Failed(DependencyKind.Ffmpeg,
                                                      "ffmpeg arşivinde ffmpeg.exe bulunamadı.");
            }

            if (!await IsUsableExecutableAsync(target, cancellationToken).ConfigureAwait(false))
            {
                TryDelete(target);

                return DependencyInstallResult.Failed(DependencyKind.Ffmpeg,
                                                      "Çıkarılan ffmpeg geçerli bir çalıştırılabilir dosya değil.");
            }

            _versions.Write(DependencyKind.Ffmpeg, release.Tag);

            return DependencyInstallResult.Succeeded(DependencyKind.Ffmpeg,
                                                     target,
                                                     $"ffmpeg {release.Tag} indirildi ve doğrulandı.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return null;
        }
        finally
        {
            TryDelete(archivePath);
        }
    }

    private async Task<DependencyInstallResult?> TryInstallFfmpegFromArchiveAsync(
        string             archiveUrl,
        string             shaUrl,
        string             extension,
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        var target      = Path.Combine(_toolsDirectory, DependencyCatalog.FileName(DependencyKind.Ffmpeg));
        var archivePath = Path.Combine(_toolsDirectory, ".ffmpeg-" + Guid.NewGuid().ToString("N") + extension);

        try
        {
            progress?.Report($"ffmpeg özeti alınıyor ({extension})...");

            string? sha256 = null;
            try
            {
                var shaText = await DependencyDownload.GetStringAsync(_httpClient, shaUrl, cancellationToken)
                                                      .ConfigureAwait(false);
                sha256 = DependencyDownload.ExtractSha256(shaText);
            }
            catch (HttpRequestException)
            {
            }

            await DependencyDownload.DownloadFileAsync(_httpClient,
                                                       archiveUrl,
                                                       archivePath,
                                                       sha256,
                                                       progress,
                                                       cancellationToken)
                                    .ConfigureAwait(false);

            progress?.Report("ffmpeg arşivi açılıyor...");

            string? extracted;
            try
            {
                extracted = DependencyArchive.TryExtractFile(archivePath,
                                                             "ffmpeg.exe",
                                                             target,
                                                             progress,
                                                             cancellationToken)
                                ? target
                                : null;
            }
            catch (InvalidDataException ex)
            {
                return DependencyInstallResult.Failed(DependencyKind.Ffmpeg,
                                                      $"ffmpeg arşivi okunamadı: {ex.Message}");
            }

            if (extracted is null)
            {
                return DependencyInstallResult.Failed(DependencyKind.Ffmpeg,
                                                      "ffmpeg arşivinde ffmpeg.exe bulunamadı.");
            }

            if (!await IsUsableExecutableAsync(target, cancellationToken).ConfigureAwait(false))
            {
                TryDelete(target);

                return DependencyInstallResult.Failed(DependencyKind.Ffmpeg,
                                                      "Çıkarılan ffmpeg geçerli bir çalıştırılabilir dosya değil.");
            }

            return DependencyInstallResult.Succeeded(DependencyKind.Ffmpeg,
                                                     target,
                                                     "ffmpeg indirildi ve doğrulandı.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return null;
        }
        finally
        {
            TryDelete(archivePath);
        }
    }

    private async Task<DependencyInstallResult> InstallMpvAsync(
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        string? directError;

        try
        {
            var result = await InstallMpvFromGitHubAsync(progress, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                return result;
            }

            directError = result.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            directError = ex.Message;
        }

        progress?.Report("Doğrudan indirme başarısız; winget deneniyor...");

        var wingetResult = await InstallMpvWithWingetAsync(progress, cancellationToken).ConfigureAwait(false);
        if (wingetResult.Success)
        {
            return wingetResult;
        }

        return DependencyInstallResult.Failed(
            DependencyKind.Mpv,
            $"MPV kurulamadı. Doğrudan indirme: {directError}. winget: {wingetResult.Message}. "
            + GetManualInstructions(DependencyKind.Mpv));
    }

    private async Task<DependencyInstallResult> InstallMpvFromGitHubAsync(
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        progress?.Report("MPV sürümü sorgulanıyor (shinchiro build)...");

        var release = await DependencyReleaseCatalog.ResolveLatestAsync(_httpClient,
                                                                        DependencyKind.Mpv,
                                                                        cancellationToken)
                                                    .ConfigureAwait(false);
        if (release is null)
        {
            return DependencyInstallResult.Failed(DependencyKind.Mpv, "GitHub release bilgisi alınamadı.");
        }

        var archivePath = Path.Combine(_toolsDirectory, ".mpv-" + Guid.NewGuid().ToString("N") + ".7z");
        var stagingDir  = Path.Combine(_toolsDirectory, ".mpv-staging-" + Guid.NewGuid().ToString("N"));

        try
        {
            await DependencyDownload.DownloadFileAsync(_httpClient,
                                                       release.DownloadUrl,
                                                       archivePath,
                                                       release.Sha256,
                                                       progress,
                                                       cancellationToken)
                                    .ConfigureAwait(false);

            progress?.Report("MPV arşivi açılıyor...");

            await Task.Run(() => DependencyArchive.ExtractToDirectory(archivePath,
                                                                      stagingDir,
                                                                      progress,
                                                                      cancellationToken),
                           cancellationToken)
                      .ConfigureAwait(false);

            var mpvExe = Directory.EnumerateFiles(stagingDir, "mpv.exe", SearchOption.AllDirectories)
                                  .FirstOrDefault();
            if (mpvExe is null)
            {
                return DependencyInstallResult.Failed(DependencyKind.Mpv,
                                                      "MPV arşivinde mpv.exe bulunamadı.");
            }

            var mpvRoot = Path.GetDirectoryName(mpvExe)!;
            var targetDir = Path.Combine(_toolsDirectory,
                                         DependencyCatalog.ToolSubdirectory(DependencyKind.Mpv)!);

            ReplaceDirectory(mpvRoot, targetDir);

            var target = Path.Combine(targetDir, "mpv.exe");
            if (!await IsUsableExecutableAsync(target, cancellationToken).ConfigureAwait(false))
            {
                return DependencyInstallResult.Failed(DependencyKind.Mpv,
                                                      "Kurulan mpv.exe geçerli bir çalıştırılabilir dosya değil.");
            }

            _versions.Write(DependencyKind.Mpv, release.Tag);

            return DependencyInstallResult.Succeeded(DependencyKind.Mpv,
                                                     target,
                                                     $"MPV {release.Tag} indirildi ve doğrulandı.");
        }
        finally
        {
            TryDelete(archivePath);
            TryDeleteDirectory(stagingDir);
        }
    }

    private async Task<DependencyInstallResult> InstallMpvWithWingetAsync(
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        var winget = FindWinget();
        if (winget is null)
        {
            return DependencyInstallResult.Failed(DependencyKind.Mpv, "winget bulunamadı.");
        }

        progress?.Report("MPV winget ile kuruluyor...");

        var startInfo = new ProcessStartInfo
        {
            FileName               = winget,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--id");
        startInfo.ArgumentList.Add(MpvWingetId);
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add("--silent");
        startInfo.ArgumentList.Add("--accept-source-agreements");
        startInfo.ArgumentList.Add("--accept-package-agreements");
        startInfo.ArgumentList.Add("--disable-interactivity");

        ExternalProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(startInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalProcessStartException ex)
        {
            return DependencyInstallResult.Failed(DependencyKind.Mpv, $"winget çalıştırılamadı: {ex.Message}");
        }

        var installedPath = FindWingetMpv();
        if (installedPath is not null)
        {
            return DependencyInstallResult.Succeeded(DependencyKind.Mpv,
                                                     installedPath,
                                                     result.ExitCode == 0
                                                         ? "MPV winget ile kuruldu."
                                                         : "MPV zaten kurulu.");
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
                         ? result.StandardOutput
                         : result.StandardError;

        return DependencyInstallResult.Failed(DependencyKind.Mpv,
                                              string.IsNullOrWhiteSpace(detail)
                                                  ? $"winget {result.ExitCode} koduyla çıktı."
                                                  : FirstLine(detail));
    }

    public static string? FindWingetMpv()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "mpv.exe"));

            var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                try
                {
                    candidates.AddRange(Directory.EnumerateFiles(packages, "mpv.exe", SearchOption.AllDirectories));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindWinget()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory.Trim(), "winget.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "Microsoft",
                                 "WindowsApps",
                                 "winget.exe");

        return File.Exists(alias) ? alias : null;
    }

    private static void ReplaceDirectory(string sourceDirectory, string targetDirectory)
    {
        var parent = Path.GetDirectoryName(targetDirectory)!;
        Directory.CreateDirectory(parent);

        var backup = targetDirectory + ".old-" + Guid.NewGuid().ToString("N");
        var moved  = false;

        if (Directory.Exists(targetDirectory))
        {
            Directory.Move(targetDirectory, backup);
            moved = true;
        }

        try
        {
            try
            {
                Directory.Move(sourceDirectory, targetDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CopyDirectory(sourceDirectory, targetDirectory);
            }
        }
        catch
        {
            TryDeleteDirectory(targetDirectory);

            if (moved && Directory.Exists(backup))
            {
                try
                {
                    Directory.Move(backup, targetDirectory);
                }
                catch
                {
                }
            }

            throw;
        }

        TryDeleteDirectory(backup);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative    = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(targetDirectory, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static async Task<bool> IsUsableExecutableAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MinimumExecutableBytes)
            {
                return false;
            }

            var             header = new byte[2];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var             read   = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);

            return read == 2 && header[0] == (byte) 'M' && header[1] == (byte) 'Z';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FirstLine(string value)
    {
        var line = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault();

        return line is null
                   ? string.Empty
                   : line.Length > 200
                       ? line[..200]
                       : line;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }
}
