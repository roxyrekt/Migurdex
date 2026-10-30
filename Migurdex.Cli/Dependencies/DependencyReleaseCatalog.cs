using System.Text.RegularExpressions;

namespace Migurdex.Cli.Dependencies;

public sealed record DependencyReleaseInfo(
    DependencyKind Kind,
    string         Tag,
    string         AssetName,
    string         DownloadUrl,
    string?        Sha256);

public static class DependencyReleaseCatalog
{
    public const string YtDlpRepo      = "yt-dlp/yt-dlp";
    public const string YtDlpAssetName = "yt-dlp.exe";

    public const string YtDlpSumsUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";

    public const string FfmpegRepo        = "GyanD/codexffmpeg";
    public const string FfmpegAssetSuffix = "essentials_build.7z";

    public const string MpvRepo = "shinchiro/mpv-winbuild-cmake";

    private static readonly Regex _mpvAsset = new(
        @"^mpv-x86_64-(?<date>\d{8})-git-[0-9a-f]+\.7z$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<DependencyReleaseInfo?> ResolveLatestAsync(
        HttpClient        httpClient,
        DependencyKind    kind,
        CancellationToken cancellationToken)
    {
        return kind switch
        {
            DependencyKind.YtDlp  => await ResolveYtDlpAsync(httpClient, cancellationToken).ConfigureAwait(false),
            DependencyKind.Ffmpeg => await ResolveFfmpegAsync(httpClient, cancellationToken).ConfigureAwait(false),
            DependencyKind.Mpv    => await ResolveMpvAsync(httpClient, cancellationToken).ConfigureAwait(false),
            _                     => null
        };
    }

    private static async Task<DependencyReleaseInfo?> ResolveYtDlpAsync(
        HttpClient        httpClient,
        CancellationToken cancellationToken)
    {
        var release = await DependencyDownload.GetLatestGitHubReleaseAsync(httpClient,
                                                                           YtDlpRepo,
                                                                           cancellationToken)
                                              .ConfigureAwait(false);
        if (release is null)
        {
            return null;
        }

        var asset = release.Assets.FirstOrDefault(a =>
                                                      a.Name.Equals(YtDlpAssetName,
                                                                    StringComparison.OrdinalIgnoreCase));

        var sha256 = asset?.Sha256;

        if (sha256 is null)
        {
            try
            {
                var manifest = await DependencyDownload.GetStringAsync(httpClient,
                                                                       YtDlpSumsUrl,
                                                                       cancellationToken)
                                                       .ConfigureAwait(false);
                sha256 = DependencyDownload.ParseSha256Manifest(manifest, YtDlpAssetName);
            }
            catch (HttpRequestException)
            {
                // ignored
            }
        }

        var url = asset?.DownloadUrl
                  ?? $"https://github.com/{YtDlpRepo}/releases/latest/download/{YtDlpAssetName}";

        return new DependencyReleaseInfo(DependencyKind.YtDlp,
                                         release.Tag,
                                         YtDlpAssetName,
                                         url,
                                         sha256);
    }

    private static async Task<DependencyReleaseInfo?> ResolveFfmpegAsync(
        HttpClient        httpClient,
        CancellationToken cancellationToken)
    {
        var release = await DependencyDownload.GetLatestGitHubReleaseAsync(httpClient,
                                                                           FfmpegRepo,
                                                                           cancellationToken)
                                              .ConfigureAwait(false);
        if (release is null)
        {
            return null;
        }

        var asset = release.Assets.FirstOrDefault(a =>
                                                      a.Name.EndsWith(FfmpegAssetSuffix,
                                                                      StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            return null;
        }

        return new DependencyReleaseInfo(DependencyKind.Ffmpeg,
                                         release.Tag,
                                         asset.Name,
                                         asset.DownloadUrl,
                                         asset.Sha256);
    }

    private static async Task<DependencyReleaseInfo?> ResolveMpvAsync(
        HttpClient        httpClient,
        CancellationToken cancellationToken)
    {
        var release = await DependencyDownload.GetLatestGitHubReleaseAsync(httpClient,
                                                                           MpvRepo,
                                                                           cancellationToken)
                                              .ConfigureAwait(false);
        if (release is null)
        {
            return null;
        }

        var asset = release.Assets
                           .Where(a => _mpvAsset.IsMatch(a.Name))
                           .OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase)
                           .FirstOrDefault();
        if (asset is null)
        {
            return null;
        }

        return new DependencyReleaseInfo(DependencyKind.Mpv,
                                         release.Tag,
                                         asset.Name,
                                         asset.DownloadUrl,
                                         asset.Sha256);
    }
}
