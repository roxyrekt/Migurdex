using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Models;

public sealed class SeasonChainEntry
{
    public int           SeasonNumber  { get; set; }
    public string?       AniListId     { get; set; }
    public string?       MyAnimeListId { get; set; }
    public string        Title         { get; set; } = string.Empty;
    public int?          TotalEpisodes { get; set; }
    public int?          Year          { get; set; }
    public ContentFormat Format        { get; set; } = ContentFormat.Unknown;
}

public sealed class SeasonChain
{
    public string                 RootAniListId { get; set; } = string.Empty;
    public List<SeasonChainEntry> Entries       { get; set; } = [];
    public bool                   Truncated     { get; set; }
}

public sealed class RelationEdge
{
    public string        RelationType { get; set; } = string.Empty;
    public string        Id           { get; set; } = string.Empty;
    public ContentFormat Format       { get; set; } = ContentFormat.Unknown;
}

public enum EntryNumberingMode
{
    Unknown,
    PerSeason,
    Absolute
}

public sealed class AlignedSeason
{
    public int ProviderSeasonNumber { get; set; }

    public int     CanonicalSeasonNumber { get; set; }
    public string? AniListId             { get; set; }
    public string? MyAnimeListId         { get; set; }
    public int     ProviderEpisodeCount  { get; set; }
    public int?    CanonicalEpisodeCount { get; set; }

    public int StartOffset { get; set; } = 1;
}

public sealed class EntryAlignment
{
    public EntryNumberingMode  NumberingMode { get; set; } = EntryNumberingMode.Unknown;
    public List<AlignedSeason> Seasons       { get; set; } = [];
    public List<string>        Warnings      { get; set; } = [];
}

public sealed class CanonicalEpisode
{
    public int    Season { get; set; }
    public double Number { get; set; }

    public bool IsOverflow { get; set; }
}
