using Migurdex.Cli.Configuration;
using Migurdex.Shared.Update;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public class UpdateService : IUpdateService
{
    public const     string                Repo                = "roxyrekt/Migurdex";
    private const    int                   CheckTimeoutSeconds = 8;
    private readonly IConfigurationService _configService;

    private readonly HttpClient _httpClient;

    public UpdateService(HttpClient httpClient, IConfigurationService configService)
    {
        _httpClient    = httpClient;
        _configService = configService;
    }

    public string CurrentVersion => AppInfo.GetVersion();

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

        var useNightly = channelOverride is not null
                             ? IsNightlyChannel(channelOverride)
                             : IsNightlyChannel(config.UpdateChannel);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(CheckTimeoutSeconds));

            if (useNightly)
            {
                return await CheckNightlyAsync(config, cts.Token);
            }

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
                ResolveChecksumUrl(release),
                ResolveDelta(release));
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

    public static bool IsNightlyChannel(string? channel)
    {
        return channel?.Trim().Equals("nightly", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static bool IsPrereleaseChannel(string? channel)
    {
        return channel?.Trim().Equals("prerelease", StringComparison.OrdinalIgnoreCase) == true
               || channel?.Trim().Equals("beta", StringComparison.OrdinalIgnoreCase) == true
               || channel?.Trim().Equals("pre", StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<GitHubRelease?> GetLatestStableAsync(CancellationToken cancellationToken)
    {
        return await SelectFromFeedAsync(false, cancellationToken).ConfigureAwait(false)
               ?? await GetLatestStableFromApiAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubRelease?> SelectFromFeedAsync(bool includePrerelease,
        CancellationToken                                       cancellationToken)
    {
        IReadOnlyList<FeedEntry> entries;

        try
        {
            entries = await GitHubReleaseFeed.GetEntriesAsync(_httpClient, Repo, cancellationToken)
                                             .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        var best = entries.FirstOrDefault(e => !e.IsNightly
                                               && (includePrerelease || !e.IsPrerelease)
                                               && !string.IsNullOrWhiteSpace(AppInfo.NormalizeTag(e.Tag)));

        if (best is null)
        {
            return null;
        }

        IReadOnlyList<GitHubAsset> assets;

        try
        {
            assets = await GitHubReleaseFeed.GetAssetsAsync(_httpClient, Repo, best.Tag, cancellationToken)
                                            .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        return new GitHubRelease(best.Tag, best.IsPrerelease, false, best.HtmlUrl, best.Notes, assets, best.UpdatedAt);
    }

    private async Task<GitHubRelease?> GetLatestStableFromApiAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
                                                   $"https://api.github.com/repos/{Repo}/releases/latest");
        AddGitHubHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseRelease(doc.RootElement);
    }

    private async Task<GitHubRelease?> GetLatestFromListAsync(CancellationToken cancellationToken)
    {
        return await SelectFromFeedAsync(true, cancellationToken).ConfigureAwait(false)
               ?? await GetLatestFromListFromApiAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubRelease?> GetLatestFromListFromApiAsync(CancellationToken cancellationToken)
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

    private async Task<UpdateCheckResult?> CheckNightlyAsync(CliConfig config, CancellationToken cancellationToken)
    {
        var nightly = await GetNightlyFromFeedAsync(cancellationToken).ConfigureAwait(false)
                      ?? await GetNightlyAsync(cancellationToken).ConfigureAwait(false);

        if (nightly is null)
        {
            return null;
        }

        var marker = nightly.PublishedAt == default
                         ? $"nightly@{nightly.Tag.Trim()}"
                         : $"nightly@{nightly.PublishedAt:yyyyMMddTHHmmss}";

        return new UpdateCheckResult(
            AppInfo.GetVersion(),
            marker,
            !marker.Equals(config.SkippedVersion, StringComparison.Ordinal),
            true,
            nightly.HtmlUrl,
            nightly.Notes,
            ResolveAsset(nightly, out var assetUrl, out var assetSize),
            assetUrl,
            assetSize,
            ResolveChecksumUrl(nightly),
            ResolveDelta(nightly));
    }

    private async Task<GitHubRelease?> GetNightlyFromFeedAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FeedEntry> entries;

        try
        {
            entries = await GitHubReleaseFeed.GetEntriesAsync(_httpClient, Repo, cancellationToken)
                                             .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        var nightly = entries.FirstOrDefault(e => e.IsNightly);
        if (nightly is null)
        {
            return null;
        }

        IReadOnlyList<GitHubAsset> assets;

        try
        {
            assets = await GitHubReleaseFeed.GetAssetsAsync(_httpClient, Repo, nightly.Tag, cancellationToken)
                                            .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        return new GitHubRelease(nightly.Tag,
                                 true,
                                 false,
                                 nightly.HtmlUrl,
                                 nightly.Notes,
                                 assets,
                                 nightly.UpdatedAt);
    }

    private async Task<GitHubRelease?> GetNightlyAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
                                                   $"https://api.github.com/repos/{Repo}/releases/tags/nightly");
        AddGitHubHeaders(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseRelease(doc.RootElement);
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
            assets,
            el.TryGetProperty("published_at", out var pub)
            && pub.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(pub.GetString(), out var published)
                ? published
                : default);
    }

    private static string GetArchLabel()
    {
        return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    }

    private static string? ResolveAsset(GitHubRelease release, out string? url, out long? size)
    {
        var arch = GetArchLabel();

        // Tek format: tar.gz. Windows 10'un bsdtar 3.3.2'si xz bilmiyor,
        // tar.gz ise her platformda sistem tar'i ile aciliyor.
        var candidates = new[]
        {
            $"migurdex-{RuntimeLabel()}-{arch}.tar.gz",
            $"migurdex-{RuntimeLabel()}-x64.tar.gz"
        };

        var asset = candidates
                    .Select(name =>
                                release.Assets.FirstOrDefault(a => a.Name.Equals(
                                                                  name,
                                                                  StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(a => a is not null);

        url  = asset?.BrowserDownloadUrl;
        size = asset is null || asset.Size <= 0 ? null : asset.Size;

        return asset?.Name;
    }

    private static string RuntimeLabel()
    {
        return OperatingSystem.IsWindows() ? "win" : "linux";
    }

    private static BlobDelta? ResolveDelta(GitHubRelease release)
    {
        var arch    = GetArchLabel();
        var runtime = RuntimeLabel();

        var storeName    = $"migurdex-{runtime}-{arch}.blobs.tar";
        var manifestName = $"migurdex-{runtime}-{arch}.manifest.json.gz";

        var store = release.Assets.FirstOrDefault(a => a.Name.Equals(storeName, StringComparison.OrdinalIgnoreCase));
        var manifest =
            release.Assets.FirstOrDefault(a => a.Name.Equals(manifestName, StringComparison.OrdinalIgnoreCase));

        if (store is null || manifest is null)
        {
            return null;
        }

        return new BlobDelta(store.Name,
                             store.BrowserDownloadUrl,
                             store.Size,
                             manifest.Name,
                             manifest.BrowserDownloadUrl,
                             manifest.Size);
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

        var arch    = GetArchLabel();
        var runtime = OperatingSystem.IsWindows() ? "win" : "linux";
        return cands.FirstOrDefault(a => a.Name.Contains(runtime, StringComparison.OrdinalIgnoreCase)
                                         && a.Name.Contains(arch, StringComparison.OrdinalIgnoreCase))
                    ?.BrowserDownloadUrl
               ?? cands.FirstOrDefault(a => a.Name.Contains(runtime, StringComparison.OrdinalIgnoreCase))
                       ?.BrowserDownloadUrl
               ?? cands[0].BrowserDownloadUrl;
    }
}
