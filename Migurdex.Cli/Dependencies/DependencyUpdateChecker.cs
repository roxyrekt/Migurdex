namespace Migurdex.Cli.Dependencies;

public sealed record DependencyUpdate(
    DependencyKind Kind,
    string         InstalledVersion,
    string         LatestVersion)
{
    public string DisplayName => DependencyCatalog.DisplayName(Kind);
}

public sealed class DependencyUpdateChecker
{
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromHours(24);

    private readonly DependencyUpdateCache  _cache;
    private readonly string                 _cachePath;
    private readonly HttpClient             _httpClient;
    private readonly DependencyResolver     _resolver;
    private readonly DependencyVersionStore _versions;

    public DependencyUpdateChecker(
        DependencyResolver     resolver,
        HttpClient             httpClient,
        DependencyVersionStore versions,
        string                 cachePath)
    {
        _resolver   = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _versions   = versions ?? throw new ArgumentNullException(nameof(versions));
        _cachePath  = cachePath ?? throw new ArgumentNullException(nameof(cachePath));
        _cache      = DependencyUpdateCache.Load(_cachePath);
    }

    public async Task<IReadOnlyList<DependencyUpdate>> CheckAsync(
        bool                         force             = false,
        IEnumerable<DependencyKind>? kinds             = null,
        IProgress<string>?           progress          = null,
        CancellationToken            cancellationToken = default)
    {
        var targets = (kinds ?? DependencyCatalog.All).ToList();

        if (force || DateTimeOffset.UtcNow - _cache.CheckedAt > DefaultCacheTtl)
        {
            var fetched = 0;

            foreach (var kind in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report($"{DependencyCatalog.DisplayName(kind)} sürümü kontrol ediliyor...");

                var release = await DependencyReleaseCatalog.ResolveLatestAsync(_httpClient,
                                                                                kind,
                                                                                cancellationToken)
                                                            .ConfigureAwait(false);
                if (release is null || string.IsNullOrWhiteSpace(release.Tag))
                {
                    continue;
                }

                _cache.Set(kind, release.Tag);
                fetched++;
            }

            if (fetched > 0)
            {
                _cache.CheckedAt = DateTimeOffset.UtcNow;
                _cache.Save(_cachePath);
            }
        }

        var updates = new List<DependencyUpdate>();

        foreach (var kind in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var latest = _cache.Get(kind);
            if (string.IsNullOrWhiteSpace(latest))
            {
                continue;
            }

            var installed = InstalledVersionOf(kind);
            if (installed is null || IsSameVersion(installed, latest))
            {
                continue;
            }

            updates.Add(new DependencyUpdate(kind, installed, latest));
        }

        return updates;
    }

    public DependencyResolver Resolver => _resolver;

    public string? InstalledVersionOf(DependencyKind kind)
    {
        var marker = _versions.Read(kind);
        if (!string.IsNullOrWhiteSpace(marker))
        {
            return marker;
        }

        return null;
    }

    public static bool IsSameVersion(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        return Normalize(installed).Equals(Normalize(latest), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        return trimmed;
    }
}
