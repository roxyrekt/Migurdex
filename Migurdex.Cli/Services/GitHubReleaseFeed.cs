using Migurdex.Shared.Update;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Migurdex.Cli.Services;

internal sealed record FeedEntry(string Tag, DateTimeOffset UpdatedAt, string Notes, string HtmlUrl)
{
    public bool IsNightly => Tag.Trim().Equals("nightly", StringComparison.OrdinalIgnoreCase);

    public bool IsPrerelease => !IsNightly && AppInfo.NormalizeTag(Tag).Contains('-');
}

internal static partial class GitHubReleaseFeed
{
    private const string WebBase = "https://github.com";

    public static async Task<IReadOnlyList<FeedEntry>> GetEntriesAsync(HttpClient http,
        string                                                                    repo,
        CancellationToken                                                         cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{WebBase}/{repo}/releases.atom");
        request.Headers.TryAddWithoutValidation("User-Agent", "migurdex");
        request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return await ParseAtomAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<GitHubAsset>> GetAssetsAsync(HttpClient http,
        string                                                                     repo,
        string                                                                     tag,
        CancellationToken                                                          cancellationToken)
    {
        var url = $"{WebBase}/{repo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "migurdex");

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ParseExpandedAssets(html);
    }

    internal static async Task<IReadOnlyList<FeedEntry>> ParseAtomAsync(Stream stream,
        CancellationToken                                                      cancellationToken)
    {
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var        feed = doc.Root ?? throw new InvalidDataException("atom kökü yok");

        var entries = new List<FeedEntry>();

        foreach (var entry in feed.Elements(atom + "entry"))
        {
            var tag = entry.Element(atom + "title")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var updated = DateTimeOffset.TryParse(entry.Element(atom + "updated")?.Value, out var parsed)
                              ? parsed
                              : default;

            var htmlUrl = entry.Elements(atom + "link")
                               .FirstOrDefault(l => (string?) l.Attribute("rel") == "alternate")
                               ?.Attribute("href")
                               ?.Value
                          ?? $"{WebBase}/{RepoPathFromTag(entry)}";

            var notes = HtmlToText(entry.Element(atom + "content")?.Value);

            entries.Add(new FeedEntry(tag, updated, notes, htmlUrl));
        }

        return entries;
    }

    internal static IReadOnlyList<GitHubAsset> ParseExpandedAssets(string html)
    {
        var assets = new List<GitHubAsset>();

        foreach (Match row in AssetRowRegex().Matches(html))
        {
            var body = row.Groups["body"].Value;

            var href = DownloadHrefRegex().Match(body);
            if (!href.Success)
            {
                continue;
            }

            var raw = href.Groups["href"].Value;

            var name = Uri.UnescapeDataString(raw[(raw.LastIndexOf('/') + 1)..]);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var size      = 0L;
            var sizeMatch = SizeRegex().Match(body);
            if (sizeMatch.Success)
            {
                size = ParseSize(sizeMatch.Groups["value"].Value, sizeMatch.Groups["unit"].Value);
            }

            assets.Add(new GitHubAsset(name,
                                       raw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                           ? raw
                                           : WebBase + raw,
                                       size));
        }

        return assets;
    }

    private static long ParseSize(string value, string unit)
    {
        if (!double.TryParse(value,
                             System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out var number))
        {
            return 0;
        }

        var factor = unit.ToUpperInvariant() switch
        {
            "GB" => 1024d * 1024d * 1024d,
            "MB" => 1024d * 1024d,
            "KB" => 1024d,
            _    => 1d
        };

        return (long) (number * factor);
    }

    internal static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = html
                   .Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace("<li>", "\n- ", StringComparison.OrdinalIgnoreCase)
                   .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                   .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                   .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
                   .Replace("</p>", "\n", StringComparison.OrdinalIgnoreCase)
                   .Replace("</h1>", "\n", StringComparison.OrdinalIgnoreCase)
                   .Replace("</h2>", "\n", StringComparison.OrdinalIgnoreCase)
                   .Replace("</h3>", "\n", StringComparison.OrdinalIgnoreCase)
                   .Replace("</ul>", "\n", StringComparison.OrdinalIgnoreCase);

        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        text = RepeatedNewlineRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    private static string RepoPathFromTag(XElement entry)
    {
        var id    = entry.Element(XNamespace.Get("http://www.w3.org/2005/Atom") + "id")?.Value ?? string.Empty;
        var slash = id.LastIndexOf('/');

        return slash < 0 ? string.Empty : $"releases/tag/{id[(slash + 1)..]}";
    }

    [GeneratedRegex("<li[^>]*class=\"[^\"]*Box-row[^\"]*\"[^>]*>(?<body>.*?)</li>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AssetRowRegex();

    [GeneratedRegex("href=\"(?<href>/[^\"]*/releases/download/[^\"]+)\"",
                    RegexOptions.IgnoreCase)]
    private static partial Regex DownloadHrefRegex();

    [GeneratedRegex(">\\s*(?<value>[0-9.]+)\\s*(?<unit>Bytes?|KB|MB|GB)\\s*<",
                    RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex("\n{3,}")]
    private static partial Regex RepeatedNewlineRegex();
}
