namespace Migurdex.Shared.Update;

public sealed record GitHubAsset(string Name, string BrowserDownloadUrl, long Size);

public sealed record GitHubRelease(
    string                     Tag,
    bool                       IsPrerelease,
    bool                       IsDraft,
    string                     HtmlUrl,
    string                     Notes,
    IReadOnlyList<GitHubAsset> Assets)
{
    public string Version => AppInfo.NormalizeTag(Tag);
}

public static class ReleaseSelector
{
    public static GitHubRelease? SelectRelease(IEnumerable<GitHubRelease> releases, bool includePrerelease)
    {
        foreach (var r in releases)
        {
            if (r.IsDraft)
            {
                continue;
            }

            if (!includePrerelease && r.IsPrerelease)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(r.Version))
            {
                continue;
            }

            return r;
        }

        return null;
    }
}
