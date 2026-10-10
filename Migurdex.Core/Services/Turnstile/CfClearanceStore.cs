using Migurdex.Core.Database;

namespace Migurdex.Core.Services.Turnstile;

public sealed class CfClearanceStore
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);

    private readonly MigurdexDatabase                                          _db;
    private readonly bool                                                      _memoryOnly;
    private readonly Lock                                                      _lock   = new();
    private readonly Dictionary<string, (string Value, DateTime ExpiresAtUtc)> _memory = new(StringComparer.Ordinal);

    public CfClearanceStore(MigurdexDatabase db, bool memoryOnly = false)
    {
        _db         = db;
        _memoryOnly = memoryOnly;
    }

    public bool TryGet(string host, out string? value, out DateTime expiresAtUtc)
    {
        var normalized = TurnstileDetector.NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized))
        {
            value        = null;
            expiresAtUtc = DateTime.MinValue;
            return false;
        }

        if (_memoryOnly)
        {
            lock (_lock)
            {
                if (_memory.TryGetValue(normalized, out var entry) && entry.ExpiresAtUtc > DateTime.UtcNow)
                {
                    value        = entry.Value;
                    expiresAtUtc = entry.ExpiresAtUtc;
                    return true;
                }

                _memory.Remove(normalized);
                value        = null;
                expiresAtUtc = DateTime.MinValue;
                return false;
            }
        }

        return _db.TryGetCfClearance(normalized, out value, out expiresAtUtc);
    }

    public void Set(string host, string value, DateTime? expiresAtUtc = null, string source = "")
    {
        var normalized = TurnstileDetector.NormalizeHost(host);
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Verilen tarih aynen saklanır (geçmişse TryGet zaten görünmez sayar);
        // yalnızca tarih verilmediyse öntanımlı TTL uygulanır.
        var expires = expiresAtUtc ?? DateTime.UtcNow.Add(DefaultTtl);

        if (_memoryOnly)
        {
            lock (_lock)
            {
                _memory[normalized] = (value.Trim(), expires);
            }

            return;
        }

        _db.SetCfClearance(normalized, value.Trim(), expires, source ?? string.Empty);
    }

    public IReadOnlyList<(string Host, DateTime ExpiresAtUtc, string Source)> GetHosts()
    {
        if (_memoryOnly)
        {
            lock (_lock)
            {
                return _memory
                       .Where(kvp => kvp.Value.ExpiresAtUtc > DateTime.UtcNow)
                       .Select(kvp => (kvp.Key, kvp.Value.ExpiresAtUtc, "memory"))
                       .OrderBy(x => x.Key, StringComparer.Ordinal)
                       .ToList();
            }
        }

        _db.ClearExpiredCfClearance();
        return _db.GetCfClearanceHosts();
    }
}
