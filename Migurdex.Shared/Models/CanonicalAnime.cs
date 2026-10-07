using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Models;

public sealed class CanonicalAnime
{
    public string CanonicalId { get; set; } = string.Empty;
    public string? AniListId { get; set; }
    public string? MyAnimeListId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? EnglishTitle { get; set; }
    public string? RomajiTitle { get; set; }
    public string? JapaneseTitle { get; set; }
    public List<string> Synonyms { get; set; } = [];

    public int? Year { get; set; }
    public int? TotalEpisodes { get; set; }
    public ContentFormat Format { get; set; } = ContentFormat.Unknown;
    public double? Score { get; set; }
    public string? PosterUrl { get; set; }

    public static string BuildId(string? anilistId, string? malId, string fallbackTitle)
    {
        if (!string.IsNullOrWhiteSpace(anilistId))
        {
            return $"anilist:{anilistId.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(malId))
        {
            return $"mal:{malId.Trim()}";
        }

        var slug = string.IsNullOrWhiteSpace(fallbackTitle)
            ? Guid.NewGuid().ToString("N")[..8]
            : fallbackTitle.Trim().ToLowerInvariant();
        return $"legacy:{slug}";
    }

    public static CanonicalAnime FromMetadata(MediaMetadata m)
    {
        var anilistId = m.AniListId
            ?? (m.Source == MetadataSource.AniList ? m.ExternalId : null);
        var malId = m.MyAnimeListId
            ?? (m.Source is MetadataSource.Jikan or MetadataSource.MyAnimeList ? m.ExternalId : null);

        var synonyms = new List<string>(m.Synonyms ?? []);
        if (!string.IsNullOrWhiteSpace(m.EnglishTitle) && !synonyms.Contains(m.EnglishTitle))
        {
            synonyms.Add(m.EnglishTitle);
        }

        if (!string.IsNullOrWhiteSpace(m.RomajiTitle) && !synonyms.Contains(m.RomajiTitle))
        {
            synonyms.Add(m.RomajiTitle);
        }

        return new CanonicalAnime
        {
            CanonicalId = BuildId(anilistId, malId, m.Title),
            AniListId = string.IsNullOrWhiteSpace(anilistId) ? null : anilistId.Trim(),
            MyAnimeListId = string.IsNullOrWhiteSpace(malId) ? null : malId.Trim(),
            Title = m.Title,
            EnglishTitle = m.EnglishTitle,
            RomajiTitle = m.RomajiTitle,
            JapaneseTitle = m.JapaneseTitle,
            Synonyms = synonyms,
            Year = m.Year,
            TotalEpisodes = m.TotalEpisodes,
            Format = m.Format,
            Score = m.Score,
            PosterUrl = m.PosterUrl,
        };
    }

    public void Merge(MediaMetadata m)
    {
        if (!string.IsNullOrWhiteSpace(m.AniListId) && string.IsNullOrWhiteSpace(AniListId))
        {
            AniListId = m.AniListId.Trim();
            CanonicalId = BuildId(AniListId, MyAnimeListId, Title);
        }

        if (!string.IsNullOrWhiteSpace(m.MyAnimeListId) && string.IsNullOrWhiteSpace(MyAnimeListId))
        {
            MyAnimeListId = m.MyAnimeListId.Trim();
            CanonicalId = BuildId(AniListId, MyAnimeListId, Title);
        }

        foreach (var s in m.Synonyms ?? [])
        {
            if (!string.IsNullOrWhiteSpace(s) && !Synonyms.Contains(s))
            {
                Synonyms.Add(s);
            }
        }

        Year ??= m.Year;
        if (Score is null)
        {
            Score = m.Score;
        }

        PosterUrl ??= m.PosterUrl;
        EnglishTitle ??= m.EnglishTitle;
        RomajiTitle ??= m.RomajiTitle;
        JapaneseTitle ??= m.JapaneseTitle;
    }

    public IEnumerable<string> GetAllTitles()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Title) && seen.Add(Title.Trim()))
        {
            yield return Title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(EnglishTitle) && seen.Add(EnglishTitle.Trim()))
        {
            yield return EnglishTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(RomajiTitle) && seen.Add(RomajiTitle.Trim()))
        {
            yield return RomajiTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(JapaneseTitle) && seen.Add(JapaneseTitle.Trim()))
        {
            yield return JapaneseTitle.Trim();
        }

        foreach (var s in Synonyms)
        {
            if (!string.IsNullOrWhiteSpace(s) && seen.Add(s.Trim()))
            {
                yield return s.Trim();
            }
        }
    }

    public IReadOnlyList<string> GetProviderQueryTitles(int max = 2)
    {
        return GetAllTitles().Take(Math.Max(1, max)).ToArray();
    }
}

public sealed class ProviderMatch
{
    public SearchResult Result { get; set; } = new();
    public double Score { get; set; }
    public bool Matched { get; set; }
}
