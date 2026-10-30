namespace Migurdex.Cli.Dependencies;

public static class DependencySetupRunner
{
    public static async Task<IReadOnlyList<DependencyInstallResult>> InstallAsync(
        DependencyResolver          resolver,
        IEnumerable<DependencyKind> kinds,
        IProgress<string>?          progress          = null,
        CancellationToken           cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(kinds);

        var results = new List<DependencyInstallResult>();

        foreach (var kind in kinds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var displayName = DependencyCatalog.DisplayName(kind);

            if (!resolver.CanAutoInstall || !resolver.Installer.SupportedTools.Contains(kind))
            {
                results.Add(DependencyInstallResult.Failed(kind, resolver.Installer.GetManualInstructions(kind)));
                continue;
            }

            progress?.Report($"{displayName} kuruluyor...");

            DependencyInstallResult result;
            try
            {
                result = await resolver.Installer.InstallAsync(kind, progress, cancellationToken)
                                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = DependencyInstallResult.Failed(kind, $"{displayName} kurulamadı: {ex.Message}");
            }

            results.Add(result);
            progress?.Report(result.Success ? $"{displayName} kuruldu." : $"{displayName}: {result.Message}");
        }

        resolver.InvalidateCache();

        return results;
    }

    public static IReadOnlyList<DependencyKind> FindMissing(
        DependencyResolver           resolver,
        IEnumerable<DependencyKind>? kinds = null)
    {
        return (kinds ?? DependencyCatalog.All).Where(kind => resolver.Resolve(kind) is null).ToList();
    }
}
