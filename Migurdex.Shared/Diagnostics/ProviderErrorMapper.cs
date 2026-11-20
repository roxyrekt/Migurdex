using Migurdex.Shared.Enums;
using System.Text.Json;

namespace Migurdex.Shared.Diagnostics;

public static class ProviderErrorMapper
{
    public static (BlameOutcome Outcome, string Message) Map(Exception ex, string fallback)
    {
        if (ex is HttpRequestException http)
        {
            if (http.StatusCode.HasValue)
            {
                return (BlameOutcome.Error, $"{fallback} (HTTP {(int) http.StatusCode.Value})");
            }

            var msg = http.Message;
            if (http.HttpRequestError == HttpRequestError.NameResolutionError
                || msg.Contains("dns", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            {
                return (BlameOutcome.Error, $"{fallback} (DNS çözülemedi)");
            }

            if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                return (BlameOutcome.Timeout, $"{fallback} (zaman aşımı)");
            }

            if (http.HttpRequestError == HttpRequestError.SecureConnectionError
                || msg.Contains("tls", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("ssl", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            {
                return (BlameOutcome.Error, $"{fallback} (TLS/SSL hatası)");
            }

            if (http.HttpRequestError == HttpRequestError.ConnectionError
                || msg.Contains("connect", StringComparison.OrdinalIgnoreCase))
            {
                return (BlameOutcome.Error, $"{fallback} (bağlantı hatası)");
            }

            return (BlameOutcome.Error, $"{fallback} (ağ hatası)");
        }

        if (ex is TimeoutException)
        {
            return (BlameOutcome.Timeout, $"{fallback} (zaman aşımı)");
        }

        if (ex is JsonException)
        {
            return (BlameOutcome.Error, $"{fallback} (upstream yanıtı ayrıştırılamadı)");
        }

        return (BlameOutcome.Error, fallback);
    }
}
