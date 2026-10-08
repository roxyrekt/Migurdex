using System.Text.Json.Serialization;

namespace Migurdex.Shared.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentFormat
{
    Tv,
    Movie,
    Ova,
    Special,
    Manga,
    Unknown
}
