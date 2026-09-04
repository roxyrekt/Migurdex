using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;

namespace Migurdex.Core.Infrastructure.Http;

public class RustHttpDefaultOptionsHandler : DelegatingHandler
{
    private readonly BrowserEmulation? _defaultEmulation;
    private readonly bool              _skipCertVerify;

    public RustHttpDefaultOptionsHandler(HttpMessageHandler innerHandler,
        bool                                                skipCertVerify   = true,
        BrowserEmulation?                                   defaultEmulation = null)
        : base(innerHandler)
    {
        _skipCertVerify   = skipCertVerify;
        _defaultEmulation = defaultEmulation;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken                                                     cancellationToken)
    {
        if (!request.Options.TryGetValue(RustHttpOptions.SkipCertVerifyKey, out _))
        {
            request.Options.Set(RustHttpOptions.SkipCertVerifyKey, _skipCertVerify);
        }

        if (_defaultEmulation.HasValue && !request.Options.TryGetValue(RustHttpOptions.EmulationKey, out _))
        {
            request.WithEmulation(_defaultEmulation.Value);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
