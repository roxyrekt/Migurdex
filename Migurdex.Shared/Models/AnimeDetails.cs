using Migurdex.Shared.Enums;
using System.Text.RegularExpressions;

namespace Migurdex.Shared.Models;

public class SeasonMapping
{
    public int     SeasonNumber  { get; set; }
    public string? AniListId     { get; set; }
    public string? MyAnimeListId { get; set; }
    public string? TmdbId        { get; set; }
}

public partial class AnimeDetails
{
    public string        Title             { get; set; } = string.Empty;
    public string?       EnglishTitle      { get; set; }
    public string?       RomajiTitle       { get; set; }
    public string?       JapaneseTitle     { get; set; }
    public string?       PosterUrl         { get; set; }
    public List<string>? AlternativeTitles { get; set; } = [];
    public string        Summary           { get; set; } = string.Empty;
    public ContentFormat Format            { get; set; } = ContentFormat.Tv;
    public List<Episode> Episodes          { get; set; } = [];

    public List<SeasonMapping> SeasonMappings { get; set; } = [];

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

        if (AlternativeTitles != null)
        {
            foreach (var alt in AlternativeTitles)
            {
                if (!string.IsNullOrWhiteSpace(alt) && seen.Add(alt.Trim()))
                {
                    yield return alt.Trim();
                }
            }
        }
    }

    [GeneratedRegex(@"\b(?:Season|Sezon)\s*(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPrefixRegex();

    [GeneratedRegex(@"\b(\d+)(?:st|nd|rd|th)?\.?\s*(?:Season|Sezon)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonSuffixRegex();

    [GeneratedRegex(@"\b(I|II|III|IV|V|VI|VII|VIII|IX|X)\b$", RegexOptions.IgnoreCase)]
    private static partial Regex RomanNumeralRegex();

    public static int ParseSeasonNumber(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return 1;
        }

        var prefixMatch = SeasonPrefixRegex().Match(title);
        if (prefixMatch.Success && int.TryParse(prefixMatch.Groups[1].Value, out var sNum1))
        {
            return sNum1;
        }

        var suffixMatch = SeasonSuffixRegex().Match(title);

        if (suffixMatch.Success && int.TryParse(suffixMatch.Groups[1].Value, out var sNum2))
        {
            return sNum2;
        }

        var titleTrimmed = title.Trim();
        var romanMatch   = RomanNumeralRegex().Match(titleTrimmed);
        if (romanMatch.Success)
        {
            var roman = romanMatch.Groups[1].Value.ToUpperInvariant();

            return roman switch
            {
                "I"    => 1,
                "II"   => 2,
                "III"  => 3,
                "IV"   => 4,
                "V"    => 5,
                "VI"   => 6,
                "VII"  => 7,
                "VIII" => 8,
                "IX"   => 9,
                "X"    => 10,
                _      => 1
            };
        }

        return 1;
    }
}
