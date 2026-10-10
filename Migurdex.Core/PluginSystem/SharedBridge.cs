using Microsoft.Extensions.Logging;
using Migurdex.Core.Infrastructure.Http;
using Migurdex.Core.Interop;
using Migurdex.Core.Services.Turnstile;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;

namespace Migurdex.Core.PluginSystem;

public class SharedBridge : ISharedBridge
{
    private static readonly RustHttpMessageHandler _rustHandler = new();

    public SharedBridge(HttpClient httpClient,
        IMp4MetadataReader         metadataReader,
        ILoggerFactory             loggerFactory)
    {
        HttpClient     = httpClient;
        MetadataReader = metadataReader;
        LoggerFactory  = loggerFactory;
    }

    public HttpClient         HttpClient     { get; }
    public IMp4MetadataReader MetadataReader { get; }
    public ILoggerFactory     LoggerFactory  { get; }

    public CfClearanceStore? ClearanceStore { get; set; }

    public HttpClient CreateHttpClient(HttpClientOptions? options = null)
    {
        options ??= new HttpClientOptions();
        HttpMessageHandler innermostHandler = new NonDisposableHttpMessageHandler(_rustHandler);

        innermostHandler =
            new RustHttpDefaultOptionsHandler(innermostHandler, options.SkipCertVerify, options.Emulation);

        if (ClearanceStore is not null)
        {
            innermostHandler = new CfClearanceHandler(ClearanceStore, innermostHandler);
        }

        if (options.UseCookies)
        {
            innermostHandler = new CookieSessionHandler(innermostHandler);
        }

        if (options.AllowAutoRedirect)
        {
            innermostHandler = new AutoRedirectHandler(innermostHandler);
        }

        innermostHandler = new ExtractionCaptureHandler(innermostHandler);

        if (options.ConfigureHandler != null)
        {
            var wrappedHandler = options.ConfigureHandler(innermostHandler);

            return new HttpClient(wrappedHandler, true);
        }

        return new HttpClient(innermostHandler, true);
    }

    public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)
    {
        var options = new HttpClientOptions();
        configure(options);
        return CreateHttpClient(options);
    }

    public ILogger<T> CreateLogger<T>()
    {
        return LoggerFactory.CreateLogger<T>();
    }
}
