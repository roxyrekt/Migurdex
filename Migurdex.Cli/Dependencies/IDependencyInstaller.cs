namespace Migurdex.Cli.Dependencies;

public sealed record DependencyInstallResult(
    DependencyKind Kind,
    bool           Success,
    string?        Path,
    string         Message)
{
    public static DependencyInstallResult Succeeded(DependencyKind kind, string path, string message)
    {
        return new DependencyInstallResult(kind, true, path, message);
    }

    public static DependencyInstallResult Failed(DependencyKind kind, string message)
    {
        return new DependencyInstallResult(kind, false, null, message);
    }
}

public interface IDependencyInstaller
{
    string PlatformName { get; }

    bool CanAutoInstall { get; }

    IReadOnlyCollection<DependencyKind> SupportedTools { get; }

    string GetManualInstructions(DependencyKind kind);

    Task<DependencyInstallResult> InstallAsync(
        DependencyKind     kind,
        IProgress<string>? progress          = null,
        CancellationToken  cancellationToken = default);
}

public static class DependencyInstallers
{
    public static IDependencyInstaller CreateDefault(string toolsDirectory)
    {
        return OperatingSystem.IsWindows()
                   ? new WindowsDependencyInstaller(toolsDirectory)
                   : new ManualDependencyInstaller();
    }
}
