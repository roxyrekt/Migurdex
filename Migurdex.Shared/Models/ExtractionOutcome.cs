using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Models;

public sealed record ExtractionFailure(string Extractor,
    UpstreamErrorKind Kind,
    string Detail,
    bool Confident = true)
{
    public static ExtractionFailure From(string extractor, int statusCode, string? body)
    {
        var kind = UpstreamErrorClassifier.Detect(statusCode, body);
        var text = UpstreamErrorClassifier.Summarize(body);
        var detail = text.Length >= 8 ? $"HTTP {statusCode}: {text}" : $"HTTP {statusCode}";

        return new ExtractionFailure(extractor, kind, detail);
    }
}

public sealed record ExtractionOutcome(List<VideoSource> Sources, List<ExtractionFailure> Failures)
{
    public static readonly ExtractionOutcome Empty = new([], []);
}
