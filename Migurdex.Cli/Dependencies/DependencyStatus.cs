namespace Migurdex.Cli.Dependencies;

public enum DependencyState
{
    Installed,
    Missing
}

public sealed record DependencyStatus(
    DependencyKind  Kind,
    DependencyState State,
    string?         ResolvedPath,
    string?         Version,
    string?         Error = null)
{
    public bool IsInstalled => State == DependencyState.Installed;

    public string DisplayName => DependencyCatalog.DisplayName(Kind);
}
