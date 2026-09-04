namespace Migurdex.Cli.Configuration;

public class FavoriteEntry
{
    public string   AnimeId      { get; set; } = string.Empty;
    public string   AnimeTitle   { get; set; } = string.Empty;
    public string   ProviderName { get; set; } = string.Empty;
    public string   PosterUrl    { get; set; } = string.Empty;
    public DateTime AddedAt      { get; set; } = DateTime.UtcNow;
}
