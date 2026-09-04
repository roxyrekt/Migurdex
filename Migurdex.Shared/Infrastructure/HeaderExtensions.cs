namespace Migurdex.Shared.Infrastructure;

public static class HeaderExtensions
{
    public static string? GetReferer(this IDictionary<string, string>? headers)
    {
        if (headers == null)
        {
            return null;
        }

        if (headers.TryGetValue("Referer", out var val) || headers.TryGetValue("referer", out val))
        {
            return val;
        }

        return null;
    }

    public static void AddHeaders(this HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers == null)
        {
            return;
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }
}
