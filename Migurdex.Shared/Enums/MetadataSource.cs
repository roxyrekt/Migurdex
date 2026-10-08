using System.Text.Json.Serialization;

namespace Migurdex.Shared.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetadataSource
{
    AniList,
    Jikan,
    MyAnimeList,
    Tmdb
}
