using Migurdex.Core.Services;
using Migurdex.Shared.Enums;
using Xunit;

namespace Migurdex.Tests;

public sealed class BlameCollectorTests
{
    [Fact]
    public void EmptySnapshot_HasNoEntries()
    {
        var report = new BlameCollector().Snapshot();

        Assert.Empty(report.Operations);
        Assert.Empty(report.Providers);
    }

    [Fact]
    public void RecordOperation_AggregatesAvgAndMax()
    {
        var blame = new BlameCollector();
        blame.RecordOperation("GET /x", 100, false);
        blame.RecordOperation("GET /x", 300, false);
        blame.RecordOperation("GET /x", 200, true);

        var op = Assert.Single(blame.Snapshot().Operations);
        Assert.Equal("GET /x", op.Operation);
        Assert.Equal(3, op.Calls);
        Assert.Equal(200, op.AvgMs);
        Assert.Equal(300, op.MaxMs);
        Assert.Equal(1, op.Errors);
    }

    [Fact]
    public void RecordOperation_TracksClientErrorsSeparately()
    {
        var blame = new BlameCollector();
        blame.RecordOperation("GET /x", 100, false);
        blame.RecordOperation("GET /x", 50, false, true);
        blame.RecordOperation("GET /x", 70, true);

        var op = Assert.Single(blame.Snapshot().Operations);
        Assert.Equal(3, op.Calls);
        Assert.Equal(1, op.Errors);
        Assert.Equal(1, op.ClientErrors);
    }

    [Fact]
    public void RecordProvider_TracksOutcomesSeparately()
    {
        var blame = new BlameCollector();
        blame.RecordProvider("Slow", "episodes", 18000, BlameOutcome.Ok);
        blame.RecordProvider("Slow", "episodes", 30000, BlameOutcome.Timeout);
        blame.RecordProvider("Slow", "episodes", 500, BlameOutcome.Mismatch);

        var p = Assert.Single(blame.Snapshot().Providers);
        Assert.Equal("Slow", p.Provider);
        Assert.Equal(3, p.Calls);
        Assert.Equal(1, p.Matched);
        Assert.Equal(1, p.Timeouts);
        Assert.Equal(1, p.Mismatches);
        Assert.Equal(30000, p.MaxMs);
    }

    [Fact]
    public void RoundTrip_PreservesCounts()
    {
        var blame = new BlameCollector();
        blame.RecordOperation("GET /x", 100, false);
        blame.RecordOperation("GET /x", 300, false);
        blame.RecordProvider("Slow", "search", 9000, BlameOutcome.Timeout);

        var loaded = BlameCollector.TryLoad(WriteTemp(blame.ToJson()));
        Assert.NotNull(loaded);

        var report = loaded!.Snapshot();
        var op = Assert.Single(report.Operations);
        Assert.Equal(2, op.Calls);
        Assert.Equal(200, op.AvgMs);
        var provider = Assert.Single(report.Providers);
        Assert.Equal("Slow", provider.Provider);
        Assert.Equal(1, provider.Timeouts);
    }

    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"blame-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void RecordProvider_TracksEmptyAndErrorSeparately()
    {
        var blame = new BlameCollector();
        blame.RecordProvider("VP", "extract", 100, BlameOutcome.Empty);
        blame.RecordProvider("VP", "extract", 200, BlameOutcome.Error);
        blame.RecordProvider("VP", "extract", 300, BlameOutcome.Ok);

        var p = Assert.Single(blame.Snapshot().Providers);
        Assert.Equal(3, p.Calls);
        Assert.Equal(1, p.Empties);
        Assert.Equal(1, p.Errors);
        Assert.Equal(1, p.Matched);
    }

    [Fact]
    public void RoundTrip_PreservesEmptyAndErrorCounts()
    {
        var blame = new BlameCollector();
        blame.RecordProvider("VP", "extract", 100, BlameOutcome.Empty);
        blame.RecordProvider("VP", "extract", 200, BlameOutcome.Error);

        var loaded = BlameCollector.TryLoad(WriteTemp(blame.ToJson()));
        Assert.NotNull(loaded);

        var provider = Assert.Single(loaded!.Snapshot().Providers);
        Assert.Equal(1, provider.Empties);
        Assert.Equal(1, provider.Errors);
    }

    [Fact]
    public void Snapshot_OrdersByAvgDesc()
    {
        var blame = new BlameCollector();
        blame.RecordProvider("Fast", "episodes", 200, BlameOutcome.Ok);
        blame.RecordProvider("Slow", "episodes", 9000, BlameOutcome.Ok);

        var providers = blame.Snapshot().Providers;
        Assert.Equal(2, providers.Count);
        Assert.Equal("Slow", providers[0].Provider);
        Assert.Equal("Fast", providers[1].Provider);
    }
}
