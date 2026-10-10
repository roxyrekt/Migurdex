using Migurdex.Core.Infrastructure.Http;
using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;
using System.Net;
using System.Text;
using Xunit;

namespace Migurdex.Tests;

public sealed class ExtractionCaptureTests
{
    private sealed class StubHandler(HttpStatusCode status, string body, string mediaType = "application/json")
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType)
            };

            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task FailedResponse_IsCapturedWithKind()
    {
        using var scope  = ExtractionCapture.Begin();
        using var client = new HttpClient(new ExtractionCaptureHandler(new StubHandler(
            HttpStatusCode.TooManyRequests,
            "{\"message\":\"playback quota exhausted\"}")));

        var response = await client.GetAsync("https://example.com/playback", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        var failure = Assert.Single(scope.Failures);
        Assert.Equal(UpstreamErrorKind.QuotaExceeded, failure.Kind);
        Assert.Contains("429", failure.Detail);
    }

    [Fact]
    public async Task CallerCanStillReadBody_AfterCapture()
    {
        using var scope  = ExtractionCapture.Begin();
        using var client = new HttpClient(new ExtractionCaptureHandler(new StubHandler(
            HttpStatusCode.Forbidden,
            "{\"error\":\"private\"}")));

        var response = await client.GetAsync("https://example.com/x", TestContext.Current.CancellationToken);
        var body     = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("private", body);
        Assert.Single(scope.Failures);
    }

    [Fact]
    public async Task BinaryBody_IsSkippedButStatusRecorded()
    {
        using var scope  = ExtractionCapture.Begin();
        using var client = new HttpClient(new ExtractionCaptureHandler(new StubHandler(
            HttpStatusCode.Forbidden,
            "binary-bytes",
            "video/mp4")));

        await client.GetAsync("https://example.com/x", TestContext.Current.CancellationToken);

        var failure = Assert.Single(scope.Failures);
        Assert.Equal("HTTP 403", failure.Detail);
    }

    [Fact]
    public async Task SmallDeletedBody_IsCapturedLowConfidence()
    {
        using var scope  = ExtractionCapture.Begin();
        using var client = new HttpClient(new ExtractionCaptureHandler(new StubHandler(
            HttpStatusCode.OK,
            "File was deleted")));

        var response = await client.GetAsync("https://example.com/e/abc", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var failure = Assert.Single(scope.Failures);
        Assert.Equal(UpstreamErrorKind.Deleted, failure.Kind);
        Assert.False(failure.Confident);
    }

    [Fact]
    public async Task CleanSmallBody_IsNotCaptured()
    {
        using var scope  = ExtractionCapture.Begin();
        using var client = new HttpClient(new ExtractionCaptureHandler(new StubHandler(
            HttpStatusCode.OK,
            "<html><body>normal player page</body></html>")));

        await client.GetAsync("https://example.com/e/abc", TestContext.Current.CancellationToken);

        Assert.Empty(scope.Failures);
    }

    private sealed class ChunkedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken                                                     cancellationToken)
        {
            var content = new StringContent(body);
            content.Headers.ContentLength = null;
            content.Headers.ContentType   = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    [Fact]
    public async Task ChunkedStubBody_IsCaptured()
    {
        var stub = new string('x', 31000) + "Video has been blocked due to author's rights infingement"
                   + " yandexError('copyrightsRestricted')" + new string('y', 1000);
        using var scope  = ExtractionCapture.Begin();
        using var client = new HttpClient(new ExtractionCaptureHandler(new ChunkedHandler(stub)));

        var response = await client.GetAsync("https://example.com/e/1", TestContext.Current.CancellationToken);
        var body     = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(stub.Length, body.Length);
        var failure = Assert.Single(scope.Failures);
        Assert.Equal(UpstreamErrorKind.CopyrightBlocked, failure.Kind);
    }

    [Fact]
    public async Task SuccessResponse_IsNotCaptured()
    {
        using var scope  = ExtractionCapture.Begin();
        using var client = new HttpClient(new ExtractionCaptureHandler(new StubHandler(
            HttpStatusCode.OK,
            "ok")));

        await client.GetAsync("https://example.com/x", TestContext.Current.CancellationToken);

        Assert.Empty(scope.Failures);
    }

    [Fact]
    public async Task NoScope_NoCaptureNoCrash()
    {
        Assert.False(ExtractionCapture.IsActive);
        using var client = new HttpClient(new ExtractionCaptureHandler(new StubHandler(
            HttpStatusCode.InternalServerError,
            "boom")));

        var response = await client.GetAsync("https://example.com/x", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("boom", body);
    }

    [Fact]
    public void ConcurrentRecords_DoNotLoseEntries()
    {
        using var scope = ExtractionCapture.Begin();

        Parallel.For(0, 500, i => ExtractionCapture.Record(404 + (i % 3), $"body-{i}"));

        Assert.Equal(500, scope.Failures.Count);
    }

    [Fact]
    public void NestedScopes_RestoreParent()
    {
        using var outer = ExtractionCapture.Begin();
        using (var inner = ExtractionCapture.Begin())
        {
            ExtractionCapture.Record(404, "not found");
            Assert.Single(inner.Failures);
        }

        Assert.Empty(outer.Failures);
        ExtractionCapture.Record(429, "too many requests");
        Assert.Single(outer.Failures);
    }
}
