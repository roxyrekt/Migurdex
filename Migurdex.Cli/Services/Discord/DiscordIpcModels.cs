using System.Text.Json.Serialization;

namespace Migurdex.Cli.Services.Discord;

public enum DiscordOpcode
{
    Handshake = 0,
    Frame     = 1,
    Close     = 2,
    Ping      = 3,
    Pong      = 4
}

public class DiscordHandshake
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;
}

public class DiscordCommand<TArgs>
{
    [JsonPropertyName("cmd")]
    public string Command { get; set; } = "SET_ACTIVITY";

    [JsonPropertyName("args")]
    public TArgs? Args { get; set; }

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = Guid.NewGuid().ToString("N");
}

public class SetActivityArgs
{
    [JsonPropertyName("pid")]
    public int ProcessId { get; set; }

    [JsonPropertyName("activity")]
    public DiscordActivity? Activity { get; set; }
}

public class DiscordActivity
{
    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("timestamps")]
    public DiscordTimestamps? Timestamps { get; set; }

    [JsonPropertyName("assets")]
    public DiscordAssets? Assets { get; set; }

    [JsonPropertyName("buttons")]
    public List<DiscordButton>? Buttons { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; } = 3; // 3 = watching

    [JsonPropertyName("instance")]
    public bool Instance { get; set; } = false;
}

public class DiscordTimestamps
{
    [JsonPropertyName("start")]
    public long? Start { get; set; }

    [JsonPropertyName("end")]
    public long? End { get; set; }
}

public class DiscordAssets
{
    [JsonPropertyName("large_image")]
    public string? LargeImage { get; set; }

    [JsonPropertyName("large_text")]
    public string? LargeText { get; set; }

    [JsonPropertyName("small_image")]
    public string? SmallImage { get; set; }

    [JsonPropertyName("small_text")]
    public string? SmallText { get; set; }
}

public class DiscordButton
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(DiscordHandshake))]
[JsonSerializable(typeof(DiscordCommand<SetActivityArgs>))]
[JsonSerializable(typeof(SetActivityArgs))]
[JsonSerializable(typeof(DiscordActivity))]
[JsonSerializable(typeof(DiscordTimestamps))]
[JsonSerializable(typeof(DiscordAssets))]
[JsonSerializable(typeof(DiscordButton))]
[JsonSerializable(typeof(List<DiscordButton>))]
internal partial class DiscordJsonContext : JsonSerializerContext
{
}
