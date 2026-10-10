using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Diagnostics;

public sealed class ExtractionException : Exception
{
    public UpstreamErrorKind Kind { get; }

    public ExtractionException(UpstreamErrorKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    public ExtractionException(UpstreamErrorKind kind, string message, Exception inner) : base(message, inner)
    {
        Kind = kind;
    }

    public static ExtractionException From(int statusCode, string? body)
    {
        var failure = Models.ExtractionFailure.From(string.Empty, statusCode, body);

        return new ExtractionException(failure.Kind, failure.Detail);
    }
}
