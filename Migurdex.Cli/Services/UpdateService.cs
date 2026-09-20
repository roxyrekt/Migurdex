using Migurdex.Shared.Update;
using System.Diagnostics;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public class UpdateService : IUpdateService
{
    public const  string Repo                = "roxyrekt/Migurdex";
    private const int    CheckTimeoutSeconds = 8;

    private readonly HttpClient            _httpClient;
    private readonly IConfigurationService _configService;

    public UpdateService(HttpClient httpClient, IConfigurationService configService)
    {
        _httpClient    = httpClient;
        _configService = configService;
    }

    public string CurrentVersion => AppInfo.GetVersion();

    public static bool IsPrereleaseChannel(string? channel)
    {
        return channel?.Trim().Equals("prerelease", StringComparison.OrdinalIgnoreCase) == true
               || channel?.Trim().Equals("beta", StringComparison.OrdinalIgnoreCase) == true
               || channel?.Trim().Equals("pre", StringComparison.OrdinalIgnoreCase) == true;
    }

    public async Task<UpdateCheckResult?> CheckForUpdatesAsync(bool force             = false,
        string?                                                     channelOverride   = null,
        CancellationToken                                           cancellationToken = default)
    {
        var config = _configService.Config;
        if (!force && !config.UpdateCheckEnabled)
        {
            return null;
        }

        if (!force && AppInfo.IsDevBuild)
        {
            return null;
        }

        var includePrerelease = channelOverride is not null
                                    ? IsPrereleaseChannel(channelOverride)
                                    : IsPrereleaseChannel(config.UpdateChannel);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));

            var release = includePrerelease
                              ? await GetLatestFromListAsync(cts.Token)
                              : await GetLatestStableAsync(cts.Token);

            if (release is null)
            {
                return null;
            }

            var current = AppInfo.GetVersion();
            var latest  = release.Version;

            return new UpdateCheckResult(
                current,
                latest,
                AppInfo.CompareVersions(current, latest) < 0,
                release.IsPrerelease,
                release.HtmlUrl,
                release.Notes,
                ResolveAsset(release, out var assetUrl, out var assetSize),
                assetUrl,
                assetSize,
                ResolveChecksumUrl(release));
        }
        catch
        {
            return null;
        }
    }

    public InstallType DetectInstallType()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE")))
        {
            return InstallType.AppImage;
        }

        var baseDir = AppContext.BaseDirectory ?? string.Empty;

        if (baseDir.Contains(".local/share/migurdex", StringComparison.OrdinalIgnoreCase))
        {
            return InstallType.Managed;
        }

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData)
                && baseDir.StartsWith(Path.Combine(localAppData, "Migurdex"),
                                      StringComparison.OrdinalIgnoreCase))
            {
                return InstallType.Managed;
            }
        }
        catch
        {
            // ignored
        }

        if (baseDir.Contains("Migurdex.Cli", StringComparison.OrdinalIgnoreCase)
            && (baseDir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                 StringComparison.OrdinalIgnoreCase)
                || baseDir.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                                    StringComparison.OrdinalIgnoreCase)))
        {
            return InstallType.Source;
        }

        return InstallType.Unknown;
    }

    public bool IsMpvRunning()
    {
        try
        {
            return Process.GetProcessesByName("mpv").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<GitHubRelease?> GetLatestStableAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
                                                   $"https://api.github.com/repos/{Repo}/releases/latest");
        AddGitHubHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseRelease(doc.RootElement);
    }

    private async Task<GitHubRelease?> GetLatestFromListAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
                                                   $"https://api.github.com/repos/{Repo}/releases?per_page=10");
        AddGitHubHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var releases = new List<GitHubRelease>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var r = ParseRelease(el);
            if (r is not null)
            {
                releases.Add(r);
            }
        }

        return ReleaseSelector.SelectRelease(releases, true);
    }

    private static void AddGitHubHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "migurdex");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    private static GitHubRelease? ParseRelease(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tag = el.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var assets = new List<GitHubAsset>();
        if (el.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assetsEl.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                               ? s.GetInt64()
                               : 0;

                assets.Add(new GitHubAsset(name, url, size));
            }
        }

        return new GitHubRelease(
            tag,
            el.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
            el.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
            el.TryGetProperty("html_url", out var html) ? html.GetString() ?? string.Empty : string.Empty,
            el.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            assets);
    }

    private static string? ResolveAsset(GitHubRelease release, out string? url, out long? size)
    {
        var expected = OperatingSystem.IsWindows() ? "migurdex-win-x64.zip" : "migurdex-linux-x64.tar.gz";
        var asset    = release.Assets.FirstOrDefault(a => a.Name.Equals(expected, StringComparison.OrdinalIgnoreCase));
        url  = asset?.BrowserDownloadUrl;
        size = asset is null || asset.Size <= 0 ? null : asset.Size;

        return asset?.Name;
    }

    private static string? ResolveChecksumUrl(GitHubRelease release)
    {
        var cands = release.Assets
                           .Where(a => a.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase))
                           .ToList();
        if (cands.Count == 0)
        {
            return null;
        }

        var runtime = OperatingSystem.IsWindows() ? "win" : "linux";
        return cands.FirstOrDefault(a => a.Name.Contains(runtime, StringComparison.OrdinalIgnoreCase))
                    ?.BrowserDownloadUrl
               ?? cands[0].BrowserDownloadUrl;
    }
}
