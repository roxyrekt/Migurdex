namespace Migurdex.Shared.Enums;

[Flags]
public enum ProviderType
{
    Anime   = 1 << 0,
    Manga   = 1 << 1,
    MovieTv = 1 << 2,
    Other   = 1 << 3
}
