using System.Text.Json.Serialization;

namespace Migurdex.Shared.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoType
{
    M3U8,
    Mp4,
    Embed,
    Unknown
}
