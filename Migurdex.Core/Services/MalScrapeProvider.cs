using AngleSharp.Html.Parser;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Services;

public partial class MalScrapeProvider : IMetadataProvider
{
    private static readonly HtmlParser _parser = new();
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static long _lastCallTicks;

    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _httpClient;

    public MalScrapeProvider(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient(o =>
        {
            o.Emulation = BrowserEmulation.Chrome147;
            o.UseCookies = true;
            o.AllowAutoRedirect = true;
        });
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    public string Name => "MyAnimeList";

    public async Task<List<MediaMetadata>> SearchMetadataAsync(string title,
        ContentFormat expectedFormat = ContentFormat.Unknown,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var q = title.Trim();
        if (q.Length > 200)
        {
            q = q[..200];
        }

        try
        {
            return await SearchHtmlAsync(q, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    public async Task<MediaMetadata?> GetMetadataByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var cleanId = id.Trim();
        if (string.IsNullOrWhiteSpace(cleanId) || !cleanId.All(char.IsDigit))
        {
            return null;
        }

        try
        {
            await ThrottleAsync(cancellationToken);
            using var response = await _httpClient.GetAsync(
                $"https://myanimelist.net/anime/{cleanId}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = await _parser.ParseDocumentAsync(html, cancellationToken);
            return ParseDetail(doc, cleanId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<MediaMetadata>> SearchHtmlAsync(string q, CancellationToken ct)
    {
        await ThrottleAsync(ct);
        using var response = await _httpClient.GetAsync(
            $"https://myanimelist.net/anime.php?q={Uri.EscapeDataString(q)}&cat=anime", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var html = await response.Content.ReadAsStringAsync(ct);
        using var doc = await _parser.ParseDocumentAsync(html, ct);

        var list = new List<MediaMetadata>();
        var rows = doc.QuerySelectorAll("div.js-categories-seasonal table tr");
        foreach (var row in rows.Skip(1))
        {
            var anchor = row.QuerySelector("td:nth-child(2) .title a.hoverinfo_trigger");
            if (anchor is null)
            {
                continue;
            }

            var href = anchor.GetAttribute("href") ?? string.Empty;
            var idMatch = MalIdRegex().Match(href);
            if (!idMatch.Success)
            {
                continue;
            }

            var malId = idMatch.Groups[1].Value;
            var animeTitle = anchor.QuerySelector("strong")?.TextContent.Trim()
                ?? anchor.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(animeTitle))
            {
                continue;
            }

            var img = row.QuerySelector("td:nth-child(1) .picSurround img");
            var poster = img?.GetAttribute("data-src") ?? img?.GetAttribute("src");
            var blurb = row.QuerySelector("td:nth-child(2) .pt4")?.TextContent.Trim() ?? string.Empty;
            var typeText = row.QuerySelector("td:nth-child(3)")?.TextContent.Trim() ?? string.Empty;
            var epsText = row.QuerySelector("td:nth-child(4)")?.TextContent.Trim() ?? string.Empty;
            var scoreText = row.QuerySelector("td:nth-child(5)")?.TextContent.Trim() ?? string.Empty;

            int.TryParse(epsText, out var eps);
            double.TryParse(scoreText, NumberStyles.Any, CultureInfo.InvariantCulture, out var scoreVal);

            list.Add(new MediaMetadata
            {
                ExternalId = malId,
                MyAnimeListId = malId,
                Source = MetadataSource.MyAnimeList,
                Title = animeTitle,
                PosterUrl = poster,
                Summary = blurb,
                Year = null,
                Score = scoreVal > 0 ? scoreVal : null,
                TotalEpisodes = eps > 0 ? eps : null,
                Format = MapFormat(typeText),
                Genres = [],
                Synonyms = [],
            });

            if (list.Count >= 10)
            {
                break;
            }
        }

        return list;
    }

    internal static MediaMetadata? ParseDetail(AngleSharp.Html.Dom.IHtmlDocument doc, string malId)
    {
        var title = doc.QuerySelector("h1.title-name strong")?.TextContent.Trim()
            ?? doc.QuerySelector("h1.title-name")?.TextContent.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var poster = doc.QuerySelector("div.leftside img[itemprop='image']")?.GetAttribute("data-src")
            ?? doc.QuerySelector("div.leftside img[itemprop='image']")?.GetAttribute("src");
        var synopsis = doc.QuerySelector("p[itemprop='description']")?.TextContent.Trim()
            ?? doc.QuerySelector("span[itemprop='description']")?.TextContent.Trim()
            ?? string.Empty;

        var scoreStr = doc.QuerySelector("div.stats-block .score-label")?.TextContent.Trim()
            ?? doc.QuerySelector("span[itemprop='ratingValue']")?.TextContent.Trim();
        double.TryParse(scoreStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var score);

        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pad in doc.QuerySelectorAll("div.leftside div.spaceit_pad"))
        {
            var dark = pad.QuerySelector("span.dark_text");
            if (dark is null)
            {
                continue;
            }

            var key = dark.TextContent.Trim().TrimEnd(':');
            var full = pad.TextContent ?? string.Empty;
            var idx = full.IndexOf(dark.TextContent, StringComparison.Ordinal);
            var val = idx >= 0 ? full[(idx + dark.TextContent.Length)..].Trim() : string.Empty;
            info[key] = Regex.Replace(val, @"\s+", " ");
        }

        info.TryGetValue("English", out var english);
        info.TryGetValue("Japanese", out var japanese);
        info.TryGetValue("Synonyms", out var synonymsRaw);
        info.TryGetValue("Episodes", out var episodesStr);
        info.TryGetValue("Status", out var status);
        info.TryGetValue("Aired", out var aired);
        info.TryGetValue("Premiered", out var premiered);
        info.TryGetValue("Type", out var typeStr);

        int? year = null;
        var yearMatch = YearRegex().Match(string.IsNullOrWhiteSpace(premiered) ? aired ?? string.Empty : premiered);
        if (yearMatch.Success && int.TryParse(yearMatch.Value, out var y))
        {
            year = y;
        }

        var genres = doc.QuerySelectorAll("div.leftside span[itemprop='genre']")
            .Select(g => g.TextContent.Trim())
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct()
            .ToList();
        if (genres.Count == 0)
        {
            genres = doc.QuerySelectorAll("div.leftside div.spaceit_pad a[href*='/anime/genre/']")
                .Select(a => a.TextContent.Trim())
                .Where(g => !string.IsNullOrEmpty(g))
                .Distinct()
                .ToList();
        }

        int.TryParse(episodesStr, out var totalEpisodes);

        return new MediaMetadata
        {
            ExternalId = malId,
            MyAnimeListId = malId,
            Source = MetadataSource.MyAnimeList,
            Title = title,
            EnglishTitle = string.IsNullOrWhiteSpace(english) ? null : english,
            JapaneseTitle = string.IsNullOrWhiteSpace(japanese) ? null : japanese,
            OriginalTitle = string.IsNullOrWhiteSpace(japanese) ? null : japanese,
            Summary = synopsis,
            PosterUrl = poster,
            Status = status ?? string.Empty,
            Year = year,
            Score = score > 0 ? score : null,
            TotalEpisodes = totalEpisodes > 0 ? totalEpisodes : null,
            Format = MapFormat(typeStr),
            Genres = genres,
            Synonyms = synonymsRaw?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() ?? [],
        };
    }

    internal static ContentFormat MapFormat(string? format)
    {
        return format?.Trim().ToLowerInvariant() switch
        {
            "tv" => ContentFormat.Tv,
            "movie" => ContentFormat.Movie,
            "ova" => ContentFormat.Ova,
            "special" => ContentFormat.Special,
            "manga" => ContentFormat.Manga,
            _ => ContentFormat.Unknown,
        };
    }

    private static async Task ThrottleAsync(CancellationToken ct)
    {
        long waitMs = 0;
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow.Ticks;
            var last = Interlocked.Read(ref _lastCallTicks);
            var elapsed = TimeSpan.FromTicks(now - last);
            if (elapsed < MinGap)
            {
                waitMs = (long)(MinGap - elapsed).TotalMilliseconds;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (waitMs > 0)
        {
            await Task.Delay((int)waitMs, ct);
        }

        Interlocked.Exchange(ref _lastCallTicks, DateTime.UtcNow.Ticks);
    }

    [GeneratedRegex(@"/anime/(\d+)")]
    private static partial Regex MalIdRegex();

    [GeneratedRegex(@"\b(19\d\d|20\d\d)\b")]
    private static partial Regex YearRegex();
}
