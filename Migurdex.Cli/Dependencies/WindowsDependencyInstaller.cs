using Migurdex.Cli.Services.Downloads;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

namespace Migurdex.Cli.Dependencies;

public sealed class WindowsDependencyInstaller : IDependencyInstaller
{
    private const string YtDlpUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    private const string FfmpegUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private const string MpvWingetId = "shinchiro.mpv";

    private const long MinimumExecutableBytes = 256 * 1024;

    private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout        = TimeSpan.FromSeconds(15)
    })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly IExternalProcessRunner _processRunner;
    private readonly string                 _toolsDirectory;

    public WindowsDependencyInstaller(string toolsDirectory, IExternalProcessRunner? processRunner = null)
    {
        _toolsDirectory = toolsDirectory ?? throw new ArgumentNullException(nameof(toolsDirectory));
        _processRunner  = processRunner ?? new ExternalProcessRunner();
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
                "MPV kurulu değil. 'winget install shinchiro.mpv' çalıştırın veya https://mpv.io adresinden indirip PATH'e ekleyin.",
            DependencyKind.YtDlp =>
                "yt-dlp kurulu değil. 'winget install yt-dlp.yt-dlp' çalıştırın veya https://github.com/yt-dlp/yt-dlp/releases adresinden indirin.",
            DependencyKind.Ffmpeg =>
                "ffmpeg kurulu değil. 'winget install Gyan.FFmpeg' çalıştırın veya https://ffmpeg.org/download.html adresinden indirip PATH'e ekleyin.",
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

        await DownloadFileAsync(YtDlpUrl, target, progress, cancellationToken).ConfigureAwait(false);

        if (!await IsUsableExecutableAsync(target, cancellationToken).ConfigureAwait(false))
        {
            TryDelete(target);

            return DependencyInstallResult.Failed(DependencyKind.YtDlp,
                                                  "İndirilen yt-dlp dosyası geçerli bir çalıştırılabilir dosya değil.");
        }

        return DependencyInstallResult.Succeeded(DependencyKind.YtDlp,
                                                 target,
                                                 "yt-dlp indirildi.");
    }

    private async Task<DependencyInstallResult> InstallFfmpegAsync(
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        var target = Path.Combine(_toolsDirectory, DependencyCatalog.FileName(DependencyKind.Ffmpeg));
        var zipPath = Path.Combine(_toolsDirectory, ".ffmpeg-" + Guid.NewGuid().ToString("N") + ".zip");
        var extractDir = Path.Combine(_toolsDirectory, ".ffmpeg-" + Guid.NewGuid().ToString("N"));

        try
        {
            await DownloadFileAsync(FfmpegUrl, zipPath, progress, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(extractDir);
            progress?.Report("ffmpeg arşivi açılıyor...");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir, true), cancellationToken)
                      .ConfigureAwait(false);

            var ffmpeg = Directory.EnumerateFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                                  .FirstOrDefault();
            if (ffmpeg is null)
            {
                return DependencyInstallResult.Failed(DependencyKind.Ffmpeg,
                                                      "ffmpeg arşivinde ffmpeg.exe bulunamadı.");
            }

            File.Copy(ffmpeg, target, true);

            if (!await IsUsableExecutableAsync(target, cancellationToken).ConfigureAwait(false))
            {
                TryDelete(target);

                return DependencyInstallResult.Failed(DependencyKind.Ffmpeg,
                                                      "Çıkarılan ffmpeg geçerli bir çalıştırılabilir dosya değil.");
            }

            return DependencyInstallResult.Succeeded(DependencyKind.Ffmpeg,
                                                     target,
                                                     "ffmpeg indirildi.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException)
        {
            return DependencyInstallResult.Failed(DependencyKind.Ffmpeg, $"ffmpeg kurulamadı: {ex.Message}");
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDirectory(extractDir);
        }
    }

    private async Task<DependencyInstallResult> InstallMpvAsync(
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        var winget = await FindWingetAsync(cancellationToken).ConfigureAwait(false);
        if (winget is null)
        {
            return DependencyInstallResult.Failed(DependencyKind.Mpv,
                                                  "winget bulunamadı. " + GetManualInstructions(DependencyKind.Mpv));
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
        if (result.ExitCode == 0 && installedPath is not null)
        {
            return DependencyInstallResult.Succeeded(DependencyKind.Mpv,
                                                     installedPath,
                                                     "MPV winget ile kuruldu.");
        }

        if (installedPath is not null)
        {
            return DependencyInstallResult.Succeeded(DependencyKind.Mpv,
                                                     installedPath,
                                                     "MPV zaten kurulu.");
        }

        var detail = string.IsNullOrWhiteSpace(result.StandardError)
                         ? result.StandardOutput
                         : result.StandardError;

        return DependencyInstallResult.Failed(
            DependencyKind.Mpv,
            "MPV kurulamadı. " + GetManualInstructions(DependencyKind.Mpv)
            + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" (winget: {FirstLine(detail)})"));
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

    private async Task<string?> FindWingetAsync(CancellationToken cancellationToken)
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
        if (File.Exists(alias))
        {
            return alias;
        }

        await Task.CompletedTask.ConfigureAwait(false);

        return null;
    }

    private static async Task DownloadFileAsync(
        string             url,
        string             destination,
        IProgress<string>? progress,
        CancellationToken  cancellationToken)
    {
        var tempPath = destination + ".download-" + Guid.NewGuid().ToString("N");

        try
        {
            progress?.Report($"İndiriliyor: {url}");
            using var response = await _httpClient.GetAsync(url,
                                                            HttpCompletionOption.ResponseHeadersRead,
                                                            cancellationToken)
                                                  .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                                                 .ConfigureAwait(false))
            await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long read  = 0;
                int  n;
                while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    read += n;
                    if (total > 0)
                    {
                        progress?.Report($"İndiriliyor: %{(int) (read * 100 / total)}");
                    }
                }
            }

            if (new FileInfo(tempPath).Length == 0)
            {
                throw new IOException("İndirilen dosya boş.");
            }

            File.Move(tempPath, destination, true);
        }
        finally
        {
            TryDelete(tempPath);
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

            var header = new byte[2];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);

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

        return line is null ? string.Empty : line.Length > 200 ? line[..200] : line;
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
