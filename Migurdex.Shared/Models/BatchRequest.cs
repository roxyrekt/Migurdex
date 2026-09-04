using System.Text.Json.Serialization;

namespace Migurdex.Shared.Models;

public class BatchRequest
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("no_follow")]
    public bool? NoFollow { get; set; }
}
