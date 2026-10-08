using System.Text.Json.Serialization;

namespace Migurdex.Shared.Enums;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderCapabilities
{
    None    = 0,
    Search  = 1 << 0,
    Fansubs = 1 << 1
}
