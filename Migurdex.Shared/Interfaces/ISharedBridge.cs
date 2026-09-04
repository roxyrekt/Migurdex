using Microsoft.Extensions.Logging;
using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface ISharedBridge
{
    IMp4MetadataReader MetadataReader { get; }
    ILoggerFactory     LoggerFactory  { get; }

    HttpClient CreateHttpClient(HttpClientOptions?        options = null);
    HttpClient CreateHttpClient(Action<HttpClientOptions> configure);

    ILogger<T> CreateLogger<T>();
}
