namespace Migurdex.Cli.Dependencies;

public sealed class ManualDependencyInstaller : IDependencyInstaller
{
    public string PlatformName => OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    public bool CanAutoInstall => false;

    public IReadOnlyCollection<DependencyKind> SupportedTools => [];

    public string GetManualInstructions(DependencyKind kind)
    {
        var tool = DependencyCatalog.CommandName(kind);
        if (OperatingSystem.IsMacOS())
        {
            return $"'{tool}' kurulu değil. Homebrew ile kurun: brew install {tool}";
        }

        return $"'{tool}' kurulu değil. Dağıtımınızın paket yöneticisiyle kurun "
               + $"(ör. apt install {tool}, pacman -S {tool}, dnf install {tool}) ve PATH'te olduğundan emin olun.";
    }

    public Task<DependencyInstallResult> InstallAsync(
        DependencyKind     kind,
        IProgress<string>? progress          = null,
        CancellationToken  cancellationToken = default)
    {
        return Task.FromResult(DependencyInstallResult.Failed(kind, GetManualInstructions(kind)));
    }
}
