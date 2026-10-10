using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Migurdex.Core.Services.Turnstile;

public static class ChromiumDownloader
{
    public const string PinnedVersion = "155.0.8059.39";

    private const string BaseUrl = "https://storage.googleapis.com/chrome-for-testing-public";

    public static string? GetPlatformSlug()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "win64";
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "linux64";
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            return "linux-arm64";
        }

        return null;
    }

    public static string DownloadUrl(string version, string platformSlug)
    {
        return $"{BaseUrl}/{version}/{platformSlug}/chrome-{platformSlug}.zip";
    }

    public static string CacheDir(string cacheRoot, string version = PinnedVersion)
    {
        return Path.Combine(cacheRoot, $"chromium-{version}");
    }

    public static string? FindCachedExecutable(string cacheRoot, string version = PinnedVersion)
    {
        var dir    = CacheDir(cacheRoot, version);
        var marker = Path.Combine(dir, "installed.txt");
        if (!File.Exists(marker))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(marker).Trim();
            if (!content.StartsWith(version, StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        var exe = FindExecutableRecursive(dir);
        if (exe is null)
        {
            return null;
        }

        EnsureExecutable(exe);
        return exe;
    }

    public static async Task<string> DownloadAsync(string cacheRoot,
        IProgress<double>?                                progress          = null,
        CancellationToken                                 cancellationToken = default,
        string                                            version           = PinnedVersion)
    {
        var slug = GetPlatformSlug();
        if (slug is null)
        {
            throw new PlatformNotSupportedException(
                "Bu platform için paketlenmiş Chromium yok (destek: linux-x64, linux-arm64, win-x64).");
        }

        var cached = FindCachedExecutable(cacheRoot, version);
        if (cached is not null)
        {
            progress?.Report(1);
            return cached;
        }

        var dir = CacheDir(cacheRoot, version);
        Directory.CreateDirectory(dir);

        var url     = DownloadUrl(version, slug);
        var zipPath = Path.Combine(dir, $"chrome-{slug}.zip");

        using (var http = new HttpClient(new SocketsHttpHandler())
               {
                   Timeout = Timeout.InfiniteTimeSpan
               })
        using (var response = await http.GetAsync(url,
                                                  HttpCompletionOption.ResponseHeadersRead,
                                                  cancellationToken)
                                        .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                                                   .ConfigureAwait(false);
            await using var file   = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var             buffer = new byte[81920];
            long            read   = 0;
            int             n;
            while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                read += n;
                if (total > 0)
                {
                    progress?.Report((double) read / total * 0.9);
                }
            }
        }

        try
        {
            await using var zip = await ZipFile.OpenReadAsync(zipPath, cancellationToken);
            if (zip.Entries.Count == 0)
            {
                throw new InvalidDataException("Boş arşiv.");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            try { File.Delete(zipPath); }
            catch
            {
                // ignored
            }

            throw new InvalidDataException($"İndirilen arşiv bozuk: {ex.Message}");
        }

        await ZipFile.ExtractToDirectoryAsync(zipPath, dir, true, cancellationToken);
        try { File.Delete(zipPath); }
        catch
        {
            // ignored
        }

        var exe = FindExecutableRecursive(dir);
        if (exe is null)
        {
            throw new InvalidDataException("Arşivde çalıştırılabilir Chromium bulunamadı.");
        }

        EnsureExecutable(exe);
        await File.WriteAllTextAsync(Path.Combine(dir, "installed.txt"), $"{version} {slug}", cancellationToken);
        progress?.Report(1);
        return exe;
    }

    private static string? FindExecutableRecursive(string dir)
    {
        string[] names = OperatingSystem.IsWindows() ? ["chrome.exe"] : ["chrome", "headless_shell"];
        try
        {
            foreach (var name in names)
            {
                var hits = Directory.GetFiles(dir, name, SearchOption.AllDirectories);
                var exe  = hits.FirstOrDefault(p => !p.Contains("Driver", StringComparison.OrdinalIgnoreCase));
                if (exe is not null)
                {
                    return exe;
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private static void EnsureExecutable(string exe)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(exe);
            File.SetUnixFileMode(exe,
                                 mode
                                 | UnixFileMode.UserExecute
                                 | UnixFileMode.GroupExecute
                                 | UnixFileMode.OtherExecute);
        }
        catch
        {
            // ignored
        }
    }
}
