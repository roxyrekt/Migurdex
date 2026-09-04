using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Models;

public class SearchResult
{
    public string       Id           { get; set; } = string.Empty;
    public string       Title        { get; set; } = string.Empty;
    public string       ProviderName { get; set; } = string.Empty;
    public ProviderType Type         { get; set; }

    public string?       EnglishTitle      { get; set; }
    public string?       RomajiTitle       { get; set; }
    public string?       JapaneseTitle     { get; set; }
    public List<string>? AlternativeTitles { get; set; } = [];
    public string?       PosterUrl         { get; set; }
    public string?       Year              { get; set; }
    public double?       Score             { get; set; }
    public List<string>? Categories        { get; set; }
    public string?       Url               { get; set; }

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
}
