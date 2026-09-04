namespace Migurdex.Core.Infrastructure.Http;

internal class NonDisposableHttpMessageHandler : DelegatingHandler
{
    public NonDisposableHttpMessageHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override void Dispose(bool disposing)
    {
        // prevent dispose
    }
}
