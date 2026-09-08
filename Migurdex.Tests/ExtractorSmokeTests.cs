using Migurdex.Shared.Interfaces;
using System.Diagnostics;
using Xunit;

namespace Migurdex.Tests;

public class ExtractorSmokeTests : IClassFixture<ExtractorFixture>
{
    private static readonly TimeSpan _perUrlTimeout = TimeSpan.FromSeconds(20);

    private readonly ExtractorFixture  _fixture;
    private readonly ITestOutputHelper _output;

    public ExtractorSmokeTests(ExtractorFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output  = output;
    }

    public static TheoryData<string, string[]> ExtractorCases()
    {
        var data = new TheoryData<string, string[]>();

        foreach (var (name, urls) in SampleLinks.Load())
        {
            data.Add(name, urls);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ExtractorCases))]
    public async Task Extractor_Resolves_AtLeast_One_Sample_Link(string extractorName, string[] urls)
    {
        var extractor = _fixture.ExtractorManager.Extractors
                                .FirstOrDefault(e => e.Name.Equals(extractorName, StringComparison.OrdinalIgnoreCase));

        Assert.True(extractor is not null,
                    $"no registered extractor matches sample category '{extractorName}'. "
                    + $"Registered: {string.Join(", ", _fixture.ExtractorManager.Extractors.Select(e => e.Name).OrderBy(n => n))}");

        var failures = new List<string>();

        foreach (var url in urls)
        {
            var (ok, detail, elapsed) = await TryResolveAsync(extractor!, url);

            _output.WriteLine($"{(ok ? "OK  " : "FAIL")} [{elapsed.TotalSeconds,5:F1}s] {url} -> {detail}");

            if (ok)
            {
                return;
            }

            failures.Add($"{url} ({detail})");
        }

        Assert.Fail($"{extractorName}: all {urls.Length} sample link(s) failed:{Environment.NewLine}"
                    + string.Join(Environment.NewLine, failures.Select(f => "  - " + f)));
    }

    private static async Task<(bool Ok, string Detail, TimeSpan Elapsed)> TryResolveAsync(IExtractor extractor,
        string                                                                                       url)
    {
        using var cts = new CancellationTokenSource(_perUrlTimeout);
        var       sw  = Stopwatch.StartNew();

        try
        {
            var sources = await extractor.ExtractAsync(url, cancellationToken: cts.Token);
            sw.Stop();

            return sources.Count > 0
                       ? (true,
                          $"{sources.Count} source(s): {string.Join(", ", sources.Take(3).Select(s => s.Quality))}",
                          sw.Elapsed)
                       : (false, "0 results", sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();

            return (false, $"timeout after {_perUrlTimeout.TotalSeconds:F0}s", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();

            return (false, $"{ex.GetType().Name}: {ex.Message}", sw.Elapsed);
        }
    }
}
