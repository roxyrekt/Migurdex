namespace Migurdex.Cli.Services;

public enum InstallType
{
    Managed,
    AppImage,
    Source,
    Unknown
}

public sealed record UpdateCheckResult(
    string  CurrentVersion,
    string  LatestVersion,
    bool    IsUpdateAvailable,
    bool    IsPrerelease,
    string  ReleaseUrl,
    string  ReleaseNotes,
    string? AssetName,
    string? AssetUrl,
    long?   AssetSize,
    string? ChecksumUrl);

public interface IUpdateService
{
    string CurrentVersion { get; }

    Task<UpdateCheckResult?> CheckForUpdatesAsync(bool force             = false,
        string?                                        channelOverride   = null,
        CancellationToken                              cancellationToken = default);

    InstallType DetectInstallType();
    bool        IsMpvRunning();
}
