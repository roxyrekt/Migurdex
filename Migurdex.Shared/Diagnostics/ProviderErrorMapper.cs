using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Diagnostics;

public static class ProviderErrorMapper
{
    public static (BlameOutcome Outcome, string Message) Map(Exception ex, string fallback)
    {
        if (ex is HttpRequestException http && http.StatusCode.HasValue)
        {
            return (BlameOutcome.Error, $"{fallback} (HTTP {(int) http.StatusCode.Value})");
        }

        if (ex is TimeoutException)
        {
            return (BlameOutcome.Timeout, $"{fallback} (zaman aşımı)");
        }

        if (ex is System.Text.Json.JsonException)
        {
            return (BlameOutcome.Error, $"{fallback} (upstream yanıtı ayrıştırılamadı)");
        }

        return (BlameOutcome.Error, fallback);
    }
}
