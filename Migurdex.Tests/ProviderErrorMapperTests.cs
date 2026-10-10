using Migurdex.Shared.Diagnostics;
using Migurdex.Shared.Enums;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Migurdex.Tests;

public sealed class ProviderErrorMapperTests
{
    [Fact]
    public void HttpError_MapsStatusIntoMessage()
    {
        var (outcome, message) = ProviderErrorMapper.Map(
            new HttpRequestException(null, null, HttpStatusCode.Forbidden), "Upstream arama hatası.");

        Assert.Equal(BlameOutcome.Error, outcome);
        Assert.Equal("Upstream arama hatası. (HTTP 403)", message);
    }

    [Fact]
    public void Timeout_MapsToTimeoutOutcome()
    {
        var (outcome, message) = ProviderErrorMapper.Map(new TimeoutException(), "Upstream kaynak hatası.");

        Assert.Equal(BlameOutcome.Timeout, outcome);
        Assert.Contains("zaman aşımı", message);
    }

    [Fact]
    public void JsonError_HintsAtStructureChange()
    {
        var (outcome, message) = ProviderErrorMapper.Map(
            new JsonException("bad json"), "Upstream detay hatası.");

        Assert.Equal(BlameOutcome.Error, outcome);
        Assert.Contains("ayrıştırılamadı", message);
    }

    [Fact]
    public void GenericError_KeepsFallback()
    {
        var (outcome, message) = ProviderErrorMapper.Map(new InvalidOperationException("x"), "Upstream grup hatası.");

        Assert.Equal(BlameOutcome.Error, outcome);
        Assert.Equal("Upstream grup hatası.", message);
    }
}
