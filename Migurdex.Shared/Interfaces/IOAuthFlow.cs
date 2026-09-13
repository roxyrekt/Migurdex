using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface IOAuthFlow
{
    string Provider { get; }

    string BuildAuthorizeUrl(string redirectUri);

    Task<OAuthToken?> ExchangeCodeAsync(string code,
        string                                 redirectUri,
        CancellationToken                      cancellationToken = default);

    Task<OAuthToken?> RefreshAsync(string refreshToken,
        CancellationToken                 cancellationToken = default);
}
