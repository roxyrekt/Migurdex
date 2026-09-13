using System.Text.Json.Serialization;

namespace Migurdex.Shared.Models;

public sealed class OAuthToken
{
    public string   Provider     { get; set; } = string.Empty;
    public string   AccessToken  { get; set; } = string.Empty;
    public string   RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }

    [JsonIgnore]
    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc;

    [JsonIgnore]
    public bool NeedsRefresh => DateTime.UtcNow >= ExpiresAtUtc - RefreshSkew;

    public static TimeSpan RefreshSkew { get; } = TimeSpan.FromMinutes(5);
}
