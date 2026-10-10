using Migurdex.Core.PluginSystem;
using Migurdex.Core.Services;
using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Infrastructure;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Migurdex.Tests;

public sealed class ExtractionExceptionTests
{
    private sealed class ThrowingExtractor(UpstreamErrorKind kind, string message) : IExtractor
    {
        public string Name => "Thrower";

        public bool CanExtract(string url)
        {
            return true;
        }

        public Task<List<VideoSource>> ExtractAsync(string url,
            IDictionary<string, string>?               headers           = null,
            CancellationToken                          cancellationToken = default)
        {
            throw new ExtractionException(kind, message);
        }
    }

    private sealed class EmptyExtractor : IExtractor
    {
        public string Name => "Empty";

        public bool CanExtract(string url)
        {
            return true;
        }

        public Task<List<VideoSource>> ExtractAsync(string url,
            IDictionary<string, string>?               headers           = null,
            CancellationToken                          cancellationToken = default)
        {
            return Task.FromResult(new List<VideoSource>());
        }
    }

    private sealed class NullReader : IMp4MetadataReader
    {
        public Task<string> GetVideoQualityAsync(string videoUrl,
            string?                                     referer           = null,
            string?                                     userAgent         = null,
            CancellationToken                           cancellationToken = default)
        {
            return Task.FromResult("Auto");
        }

        public Task<string> GetVideoQualityAsync(string videoUrl,
            Dictionary<string, string>                        headers,
            CancellationToken                                 cancellationToken = default)
        {
            return Task.FromResult("Auto");
        }

        public Task<Mp4Metadata> GetVideoMetadataAsync(string videoUrl,
            string?                                                      referer           = null,
            string?                                                      userAgent         = null,
            CancellationToken                                            cancellationToken = default)
        {
            return Task.FromResult(new Mp4Metadata("Auto", null, null, null));
        }

        public Task<Mp4Metadata> GetVideoMetadataAsync(string videoUrl,
            Dictionary<string, string>                                     headers,
            CancellationToken                                              cancellationToken = default)
        {
            return Task.FromResult(new Mp4Metadata("Auto", null, null, null));
        }
    }

    private sealed class NullBridge(ILoggerFactory factory) : ISharedBridge
    {
        public HttpClient HttpClient => new();
        public IMp4MetadataReader MetadataReader => throw new NotSupportedException();
        public ILoggerFactory LoggerFactory => factory;

        public HttpClient CreateHttpClient(HttpClientOptions? options = null)
        {
            return new HttpClient();
        }

        public HttpClient CreateHttpClient(Action<HttpClientOptions> configure)
        {
            return new HttpClient();
        }

        public ILogger<T> CreateLogger<T>()
        {
            return factory.CreateLogger<T>();
        }
    }

    private static ExtractorManager Setup(out PluginLoader loader)
    {
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        loader = new PluginLoader(new NullBridge(factory),
                                  factory.CreateLogger<PluginLoader>(),
                                  factory);

        return new ExtractorManager(factory.CreateLogger<ExtractorManager>(), new NullReader(), loader);
    }

    [Fact]
    public async Task ThrownExtractionException_BecomesFailureWithKind()
    {
        var manager = Setup(out _);
        manager.RegisterExtractor(new ThrowingExtractor(UpstreamErrorKind.Private, "Özel içerik"));

        var outcome = await manager.ExtractDetailedAsync("https://example.com/embed", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(outcome.Sources);
        var failure = Assert.Single(outcome.Failures);
        Assert.Equal("Thrower", failure.Extractor);
        Assert.Equal(UpstreamErrorKind.Private, failure.Kind);
        Assert.Equal("Özel içerik", failure.Detail);
    }

    [Fact]
    public async Task ThrownUpstreamChanged_IsPreserved()
    {
        var manager = Setup(out _);
        manager.RegisterExtractor(new ThrowingExtractor(UpstreamErrorKind.UpstreamChanged, "files JSON yok"));

        var outcome = await manager.ExtractDetailedAsync("https://example.com/embed", cancellationToken: TestContext.Current.CancellationToken);

        var failure = Assert.Single(outcome.Failures);
        Assert.Equal(UpstreamErrorKind.UpstreamChanged, failure.Kind);
    }

    [Fact]
    public async Task LegacyEmptyExtractor_YieldsNoFailures()
    {
        var manager = Setup(out _);
        manager.RegisterExtractor(new EmptyExtractor());

        var outcome = await manager.ExtractDetailedAsync("https://example.com/embed", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(outcome.Sources);
        Assert.Empty(outcome.Failures);
    }

    [Fact]
    public void From_ClassifiesFromStatusAndBody()
    {
        var ex = ExtractionException.From(429, "playback quota exhausted");

        Assert.Equal(UpstreamErrorKind.QuotaExceeded, ex.Kind);
        Assert.Contains("429", ex.Message);
    }
}
