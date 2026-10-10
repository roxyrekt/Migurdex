namespace Migurdex.Core.Services.Turnstile;

public static class TurnstileDetector
{
    public static string NormalizeHost(string hostOrUrl)
    {
        if (string.IsNullOrWhiteSpace(hostOrUrl))
        {
            return string.Empty;
        }

        var input = hostOrUrl.Trim();
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host.Trim().ToLowerInvariant();
        }

        if (Uri.TryCreate($"https://{input}", UriKind.Absolute, out var bare) && !string.IsNullOrEmpty(bare.Host))
        {
            return bare.Host.Trim().ToLowerInvariant();
        }

        return input.ToLowerInvariant();
    }
}
