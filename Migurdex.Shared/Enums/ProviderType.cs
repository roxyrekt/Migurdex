using System.Text.Json.Serialization;

namespace Migurdex.Shared.Enums;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderType
{
    Anime   = 1 << 0,
    Manga   = 1 << 1,
    MovieTv = 1 << 2,
    Other   = 1 << 3
}
