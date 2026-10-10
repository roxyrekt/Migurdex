using Migurdex.Shared.Models;

namespace Migurdex.Shared.Diagnostics;

public sealed class ExtractionCapture : IDisposable
{
    private sealed class ScopeState
    {
        public readonly Lock                  Gate = new();
        public readonly List<ExtractionFailure> Failures = [];
    }

    private static readonly AsyncLocal<ScopeState?> _current = new();

    private readonly ScopeState? _parent;
    private readonly ScopeState  _state = new();

    private ExtractionCapture(ScopeState? parent)
    {
        _parent = parent;
    }

    public static bool IsActive => _current.Value is not null;

    public IReadOnlyList<ExtractionFailure> Failures
    {
        get
        {
            lock (_state.Gate)
            {
                return _state.Failures.ToList();
            }
        }
    }

    public static ExtractionCapture Begin()
    {
        var scope = new ExtractionCapture(_current.Value);
        _current.Value = scope._state;

        return scope;
    }

    public static void Record(int statusCode, string? body)
    {
        Add(ExtractionFailure.From(string.Empty, statusCode, body));
    }

    public static void RecordLowConfidence(int statusCode, string? body)
    {
        var failure = ExtractionFailure.From(string.Empty, statusCode, body);
        Add(failure with { Confident = false });
    }

    private static void Add(ExtractionFailure failure)
    {
        var state = _current.Value;
        if (state is null)
        {
            return;
        }

        lock (state.Gate)
        {
            state.Failures.Add(failure);
        }
    }

    public void Dispose()
    {
        _current.Value = _parent;
    }
}
