namespace Migurdex.Shared.Models;

public sealed class CanonicalEpisodeSource
{
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderAnimeId { get; set; } = string.Empty;
    public string ProviderEpisodeId { get; set; } = string.Empty;
    public string? EpisodeTitle { get; set; }
    public double MatchScore { get; set; }
}

public sealed class CanonicalEpisodeEntry
{
    public int Season { get; set; }
    public double Number { get; set; }
    public string? Title { get; set; }
    public List<CanonicalEpisodeSource> Sources { get; set; } = [];
}

public sealed class CanonicalEpisodeResult
{
    public CanonicalAnime Anime { get; set; } = new();
    public List<CanonicalEpisodeEntry> Episodes { get; set; } = [];
    public int ProvidersQueried { get; set; }
    public int ProvidersMatched { get; set; }
    public List<string> MatchedProviders { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public Dictionary<string, string> ProviderErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
