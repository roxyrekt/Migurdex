using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

public sealed class CliBridge : ISharedBridge
{
    public IMp4MetadataReader MetadataReader => throw new NotSupportedException();
    public ILoggerFactory     LoggerFactory  => NullLoggerFactory.Instance;

    public HttpClient CreateHttpClient(HttpClientOptions? options = null)
    {
        return new HttpClient(new SocketsHttpHandler(), true);
    }

    public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)
    {
        return CreateHttpClient();
    }

    public ILogger<T> CreateLogger<T>()
    {
        return NullLogger<T>.Instance;
    }
}
