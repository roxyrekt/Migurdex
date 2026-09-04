using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Models;

public class MediaMetadata
{
    public string         ExternalId    { get; set; } = string.Empty;
    public MetadataSource Source        { get; set; }
    public string         Title         { get; set; } = string.Empty;
    public string?        EnglishTitle  { get; set; }
    public string?        RomajiTitle   { get; set; }
    public string?        JapaneseTitle { get; set; }
    public string?        OriginalTitle { get; set; }
    public string?        Summary       { get; set; }
    public string?        PosterUrl     { get; set; }
    public string?        BannerUrl     { get; set; }
    public string?        Status        { get; set; }
    public int?           Year          { get; set; }
    public double?        Score         { get; set; }
    public int?           TotalEpisodes { get; set; }
    public ContentFormat  Format        { get; set; } = ContentFormat.Unknown;
    public List<string>   Genres        { get; set; } = [];
    public List<string>   Synonyms      { get; set; } = [];

    public string? AniListId     { get; set; }
    public string? MyAnimeListId { get; set; }
}
