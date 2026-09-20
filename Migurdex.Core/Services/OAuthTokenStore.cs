using Migurdex.Core.Database;
using Migurdex.Shared.Models;

namespace Migurdex.Core.Services;

public sealed class OAuthTokenStore
{
    private readonly MigurdexDatabase _db;

    public OAuthTokenStore(MigurdexDatabase db)
    {
        _db = db;
    }

    public OAuthTokenStore(string? configDirectory = null) : this(new MigurdexDatabase(configDirectory))
    {
    }

    public IReadOnlyList<string> Providers => _db.GetTokenProviders();

    public bool TryGet(string provider, out OAuthToken? token)
    {
        return _db.TryGetToken(provider, out token);
    }

    public void Set(OAuthToken token)
    {
        _db.SetToken(token);
    }

    public bool Remove(string provider)
    {
        return _db.RemoveToken(provider);
    }
}
