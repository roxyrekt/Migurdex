using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;

namespace Migurdex.Core.Services;

public sealed class BlameCollector : IBlameCollector
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, OperationAcc> _operations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProviderAcc> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _since = DateTime.UtcNow;

    public void RecordOperation(string operation, long elapsedMs, bool failed, bool clientError = false)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operation, out var acc))
            {
                acc = new OperationAcc();
                _operations[operation] = acc;
            }

            acc.Calls++;
            acc.TotalMs += elapsedMs;
            acc.MaxMs = Math.Max(acc.MaxMs, elapsedMs);
            if (failed)
            {
                acc.Errors++;
            }
            else if (clientError)
            {
                acc.ClientErrors++;
            }

            acc.LastAt = DateTime.UtcNow;
        }
    }

    public void RecordProvider(string provider, string operation, long elapsedMs, BlameOutcome outcome)
    {
        lock (_gate)
        {
            var key = $"{operation}|{provider}";
            if (!_providers.TryGetValue(key, out var acc))
            {
                acc = new ProviderAcc { Provider = provider, Operation = operation };
                _providers[key] = acc;
            }

            acc.Calls++;
            acc.TotalMs += elapsedMs;
            acc.MaxMs = Math.Max(acc.MaxMs, elapsedMs);
            switch (outcome)
            {
                case BlameOutcome.Timeout:
                    acc.Timeouts++;
                    break;
                case BlameOutcome.Mismatch:
                    acc.Mismatches++;
                    break;
                case BlameOutcome.Error:
                    acc.Errors++;
                    break;
                default:
                    acc.Matched++;
                    break;
            }

            acc.LastAt = DateTime.UtcNow;
        }
    }

    public BlameReport Snapshot()
    {
        lock (_gate)
        {
            return new BlameReport
            {
                Since = _since,
                Operations = _operations
                    .Select(kv => new OperationBlame
                    {
                        Operation    = kv.Key,
                        Calls        = kv.Value.Calls,
                        AvgMs        = kv.Value.Calls > 0 ? (double) kv.Value.TotalMs / kv.Value.Calls : 0,
                        MaxMs        = kv.Value.MaxMs,
                        Errors       = kv.Value.Errors,
                        ClientErrors = kv.Value.ClientErrors,
                        LastAt       = kv.Value.LastAt
                    })
                    .OrderByDescending(o => o.AvgMs)
                    .ToList(),
                Providers = _providers.Values
                    .Select(a => new ProviderBlame
                    {
                        Operation  = a.Operation,
                        Provider   = a.Provider,
                        Calls      = a.Calls,
                        AvgMs      = a.Calls > 0 ? (double) a.TotalMs / a.Calls : 0,
                        MaxMs      = a.MaxMs,
                        Timeouts   = a.Timeouts,
                        Mismatches = a.Mismatches,
                        Matched    = a.Matched
                    })
                    .OrderByDescending(p => p.AvgMs)
                    .ToList()
            };
        }
    }

    public string? PersistPath { get; set; }

    public string ToJson()
    {
        return JsonSerializer.Serialize(Snapshot());
    }

    public static BlameCollector? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var report = JsonSerializer.Deserialize<BlameReport>(File.ReadAllText(path));
            if (report is null)
            {
                return null;
            }

            var collector = new BlameCollector { PersistPath = path };
            foreach (var o in report.Operations)
            {
                collector._operations[o.Operation] = new OperationAcc
                {
                    Calls        = o.Calls,
                    TotalMs      = (long) Math.Round(o.AvgMs * o.Calls),
                    MaxMs        = o.MaxMs,
                    Errors       = o.Errors,
                    ClientErrors = o.ClientErrors,
                    LastAt       = o.LastAt
                };
            }

            foreach (var p in report.Providers)
            {
                collector._providers[$"{p.Operation}|{p.Provider}"] = new ProviderAcc
                {
                    Provider   = p.Provider,
                    Operation  = p.Operation,
                    Calls      = p.Calls,
                    TotalMs    = (long) Math.Round(p.AvgMs * p.Calls),
                    MaxMs      = p.MaxMs,
                    Timeouts   = p.Timeouts,
                    Mismatches = p.Mismatches,
                    Matched    = p.Matched
                };
            }

            return collector;
        }
        catch
        {
            return null;
        }
    }

    public void SaveIfStale(TimeSpan? maxAge = null)
    {
        var path = PersistPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (_gate)
        {
            if (DateTime.UtcNow - _lastSave < (maxAge ?? TimeSpan.FromMinutes(1)))
            {
                return;
            }

            _lastSave = DateTime.UtcNow;
        }

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, ToJson());
        }
        catch
        {
        }
    }

    private DateTime _lastSave = DateTime.MinValue;

    private sealed class OperationAcc
    {
        public int      Calls;
        public long     TotalMs;
        public long     MaxMs;
        public int      Errors;
        public int      ClientErrors;
        public DateTime LastAt;
    }

    private sealed class ProviderAcc
    {
        public string   Provider = string.Empty;
        public string   Operation = string.Empty;
        public int      Calls;
        public long     TotalMs;
        public long     MaxMs;
        public int      Timeouts;
        public int      Mismatches;
        public int      Matched;
        public int      Errors;
        public DateTime LastAt;
    }
}
