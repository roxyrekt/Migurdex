using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

public sealed class SourceExtractionReportTests
{
    [Fact]
    public void NewReport_StartsEmpty()
    {
        var report = new SourceExtractionReport();

        Assert.Equal(0, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.Empty(report.Errors);
        Assert.Equal(0, report.TotalItems);
    }

    [Fact]
    public void TryTrackSource_CountsEachUniqueSourceOnce()
    {
        var report = new SourceExtractionReport();

        Assert.True(report.TryTrackSource(Source("https://cdn.test/a.mp4")));
        Assert.True(report.TryTrackSource(Source("https://cdn.test/b.m3u8")));

        Assert.Equal(2, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.Equal(2, report.TotalItems);
    }

    [Fact]
    public void TryTrackSource_DeduplicatesCaseInsensitively()
    {
        var report = new SourceExtractionReport();

        Assert.True(report.TryTrackSource(Source("https://cdn.test/a.mp4")));
        Assert.False(report.TryTrackSource(Source("https://CDN.test/A.MP4")));

        Assert.Equal(1, report.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryTrackSource_RejectsBlankOrMissingUrl(string? url)
    {
        var report = new SourceExtractionReport();

        Assert.False(report.TryTrackSource(new VideoSource { Url = url! }));
        Assert.Equal(0, report.Succeeded);
    }

    [Fact]
    public void TryTrackSource_RejectsNullSource()
    {
        var report = new SourceExtractionReport();

        Assert.False(report.TryTrackSource(null));
        Assert.Equal(0, report.Succeeded);
    }

    [Fact]
    public void RecordFailure_IncrementsFailedAndKeepsErrorsAligned()
    {
        var report = new SourceExtractionReport();

        report.RecordFailure("Animexe");
        report.RecordFailure("Animexe");

        Assert.Equal(0, report.Succeeded);
        Assert.Equal(2, report.Failed);
        Assert.Equal(2, report.Errors.Count);
        Assert.All(report.Errors, error =>
        {
            Assert.Equal("Animexe", error.Provider);
            Assert.Equal("extract", error.Scope);
            Assert.Equal(SourceExtractionReport.FailureMessage, error.Error);
        });
    }

    [Fact]
    public void RecordFailure_KeepsCustomMessage()
    {
        var report = new SourceExtractionReport();

        report.RecordFailure("Animexe", "Extractor zaman aşımı.");

        Assert.Equal("Extractor zaman aşımı.", Assert.Single(report.Errors).Error);
    }

    [Fact]
    public void Scope_IsTheDocumentedExtractValue()
    {
        Assert.Equal("extract", SourceExtractionReport.Scope);
    }

    [Fact]
    public void ToDoneSummary_ReflectsFailuresInsteadOfAlwaysReportingZero()
    {
        var report = new SourceExtractionReport();
        report.TryTrackSource(Source("https://cdn.test/a.mp4"));
        report.RecordFailure("Animexe");

        var summary = report.ToDoneSummary();

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, summary.TotalItems);
        Assert.Equal("extract", Assert.Single(summary.Errors).Scope);
    }

    [Fact]
    public void ToDoneSummary_SnapshotsStateAtCallTime()
    {
        var report = new SourceExtractionReport();
        report.RecordFailure("Animexe");

        var summary = report.ToDoneSummary();
        report.RecordFailure("Animexe");

        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, report.Failed);
    }

    [Fact]
    public async Task ConcurrentUpdates_DoNotLoseCounts()
    {
        var report = new SourceExtractionReport();

        await Task.WhenAll(Enumerable.Range(0, 200)
                                     .Select(i => Task.Run(() =>
                                     {
                                         report.TryTrackSource(Source($"https://cdn.test/{i}.mp4"));
                                         report.RecordFailure("Animexe");
                                     })));

        Assert.Equal(200, report.Succeeded);
        Assert.Equal(200, report.Failed);
        Assert.Equal(200, report.Errors.Count);
        Assert.Equal(400, report.TotalItems);
    }

    private static VideoSource Source(string url) => new() { Url = url, Type = VideoType.Mp4 };
}
