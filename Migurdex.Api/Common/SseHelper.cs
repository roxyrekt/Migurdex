using System.Text.Json;
using System.Threading.Channels;

namespace Migurdex.Api.Common;

public sealed record SseEnvelope(string Event, object? Data);

public sealed record ProviderErrorPayload(string Provider, string Scope, string Error);

public sealed record DoneErrorItem(string Provider, string? Scope, string Error);

public sealed record DoneSummary(int Succeeded, int Failed, List<DoneErrorItem> Errors, int? TotalItems = null);

public static class SseHelper
{
    public const string EventSearchResult  = "searchResult";
    public const string EventSource        = "source";
    public const string EventProviderError = "providerError";
    public const string EventDone          = "done";
    public const string EventError         = "error";

    private static readonly JsonSerializerOptions _defaultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void InitializeSseResponse(HttpContext context)
    {
        context.Response.ContentType          = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection   = "keep-alive";

        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteEventAsync(
        HttpContext            context,
        string                 eventType,
        object?                payload,
        CancellationToken      cancellationToken,
        JsonSerializerOptions? jsonOptions = null)
    {
        var options = jsonOptions ?? _defaultJsonOptions;
        var json = JsonSerializer.Serialize(payload
                                            ?? new
                                            {
                                            },
                                            options);

        await context.Response.WriteAsync($"event: {eventType}\n", cancellationToken);
        await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    public static async Task StreamEnvelopesAsync(
        HttpContext                context,
        ChannelReader<SseEnvelope> reader,
        CancellationToken          cancellationToken,
        JsonSerializerOptions?     jsonOptions = null)
    {
        InitializeSseResponse(context);

        try
        {
            await foreach (var envelope in reader.ReadAllAsync(cancellationToken))
            {
                await WriteEventAsync(context, envelope.Event, envelope.Data, cancellationToken, jsonOptions);
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
    }

    public static Task WriteProviderErrorAsync(
        HttpContext       context,
        string            provider,
        string            scope,
        string            error,
        CancellationToken cancellationToken)
    {
        InitializeSseResponse(context);
        return WriteEventAsync(context,
                               EventProviderError,
                               new ProviderErrorPayload(provider, scope, error),
                               cancellationToken);
    }

    public static Task WriteDoneSummaryAsync(
        HttpContext       context,
        DoneSummary       summary,
        CancellationToken cancellationToken)
    {
        InitializeSseResponse(context);
        return WriteEventAsync(context, EventDone, summary, cancellationToken);
    }
}
