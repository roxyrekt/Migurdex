using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Parça bağlantı bütçesi süreç genelinde tek bir durumdur; bu yüzden bütçe testleriyle
/// aynı koleksiyonda, sırayla koşar (paralel koşarsa biri diğerinin sayacını bozar).
/// </summary>
[Collection("DownloadConnectionBudget")]
public sealed class DownloadQueueServiceTests
{
    [Fact]
    public async Task RunAsync_Sequential_KeepsInputOrderAndNeverOverlaps()
    {
        var probe   = new ConcurrencyProbe();
        var items   = Enumerable.Range(0, 4).Select(index => SuccessfulItem(probe, index)).ToList();
        var options = new DownloadQueueOptions
        {
            ParallelEnabled = true,
            MaxConcurrency  = 1
        };

        var summary = await new DownloadQueueService()
                            .RunAsync(items, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, probe.Peak);
        Assert.Equal(new[] { 0, 1, 2, 3 }, probe.Started);
        Assert.Equal(new[] { 0, 1, 2, 3 }, probe.Finished);
        Assert.True(summary.AllSucceeded);
        Assert.Equal(4, summary.Succeeded);
    }

    [Fact]
    public async Task RunAsync_WhenParallelDisabled_BehavesLikeSequentialEvenWithHighConcurrency()
    {
        var probe   = new ConcurrencyProbe();
        var items   = Enumerable.Range(0, 4).Select(index => SuccessfulItem(probe, index)).ToList();
        var options = new DownloadQueueOptions
        {
            ParallelEnabled = false,
            MaxConcurrency  = 4
        };

        Assert.Equal(1, options.EffectiveConcurrency);

        var summary = await new DownloadQueueService()
                            .RunAsync(items, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, probe.Peak);
        Assert.Equal(new[] { 0, 1, 2, 3 }, probe.Finished);
        Assert.Equal(4, summary.Succeeded);
    }

    [Fact]
    public async Task RunAsync_Parallel_DefaultDegreeTwoRunsTwoAtATime()
    {
        var probe   = new ConcurrencyProbe();
        var items   = Enumerable.Range(0, 4).Select(index => SuccessfulItem(probe, index)).ToList();
        var options = DownloadQueueOptions.FromConfig(new CliConfig());

        Assert.True(options.ParallelEnabled);
        Assert.Equal(2, options.EffectiveConcurrency);

        var summary = await new DownloadQueueService()
                            .RunAsync(items, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, probe.Peak);
        Assert.Equal(4, summary.Succeeded);
        Assert.Equal(new[] { 0, 1, 2, 3 }, summary.Results.Select(result => result.Index).ToArray());
    }

    [Fact]
    public async Task RunAsync_Parallel_RaisedConcurrencyUsesMoreWorkersButStaysInsideTheLimit()
    {
        var probe   = new ConcurrencyProbe();
        var items   = Enumerable.Range(0, 6).Select(index => SuccessfulItem(probe, index)).ToList();
        var options = new DownloadQueueOptions
        {
            ParallelEnabled = true,
            MaxConcurrency  = 4
        };

        var summary = await new DownloadQueueService()
                            .RunAsync(items, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, probe.Peak);
        Assert.Equal(6, summary.Succeeded);
    }

    [Fact]
    public async Task RunAsync_Parallel_AppliesTheConnectionBudgetAndResetsItAfterwards()
    {
        var seen  = new List<int>();
        var items = Enumerable.Range(0, 8)
                              .Select(index => new DownloadQueueItem($"item-{index}", _ =>
                              {
                                  lock (seen)
                                  {
                                      seen.Add(DownloadConnectionBudget.SegmentsPerFile);
                                  }

                                  return Task.FromResult(DownloadResult.Successful($"/tmp/item-{index}.mp4"));
                              }))
                              .ToList();
        var options = new DownloadQueueOptions
        {
            ParallelEnabled = true,
            MaxConcurrency  = 8
        };

        var summary = await new DownloadQueueService()
                            .RunAsync(items, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(8, summary.Succeeded);
        Assert.Equal(8, options.EffectiveConcurrency);

        // 8 dosya paralel: 8 × 4 = 32 bağlantı yerine dosya başına 1 parça (toplam 8).
        Assert.Equal(new[] { 1 }, seen.Distinct());

        // Kuyruk bitti: tek indirme akışı yine varsayılan 4 parçayı kullanmalı.
        Assert.Equal(DownloadConnectionBudget.DefaultSegmentsPerFile,
                     DownloadConnectionBudget.SegmentsPerFile);
    }

    [Fact]
    public async Task RunAsync_OneFailingItem_DoesNotStopTheOthers()
    {
        var probe = new ConcurrencyProbe();
        var items = new List<DownloadQueueItem>
        {
            SuccessfulItem(probe, 0),
            new("item-1", _ => Task.FromResult(DownloadResult.Failed("patladı"))),
            SuccessfulItem(probe, 2),
            new("item-3", _ => throw new InvalidOperationException("beklenmeyen hata"))
        };
        var options = DownloadQueueOptions.FromConfig(new CliConfig());

        var summary = await new DownloadQueueService()
                            .RunAsync(items, options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(4, summary.Total);
        Assert.Equal(2, summary.Succeeded);
        Assert.Equal(2, summary.Failed);
        Assert.False(summary.AllSucceeded);
        Assert.Equal("patladı", summary.Results[1].Error);
        Assert.Equal("beklenmeyen hata", summary.Results[3].Error);
        Assert.Equal(DownloadQueueItemState.Completed, summary.Results[2].State);
    }

    [Fact]
    public async Task RunAsync_VideoDoneButSubtitleCancelled_CountsAsCompletedAndFlagsSubtitleCancel()
    {
        var items = new List<DownloadQueueItem>
        {
            new("item-0", _ => Task.FromResult(new DownloadResult
            {
                Success     = true,
                IsCancelled = true,
                MediaPath   = "/tmp/item-0.mp4"
            }))
        };

        var summary = await new DownloadQueueService()
                            .RunAsync(items,
                                      DownloadQueueOptions.FromConfig(new CliConfig()),
                                      cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(0, summary.Failed);
        Assert.True(summary.AnySubtitleCancelled);
        Assert.True(summary.AllSucceeded);
    }

    [Fact]
    public async Task RunAsync_PreCancelledToken_MarksEveryItemCancelledWithoutRunningIt()
    {
        var probe = new ConcurrencyProbe();
        var items = Enumerable.Range(0, 3).Select(index => SuccessfulItem(probe, index)).ToList();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var summary = await new DownloadQueueService()
                            .RunAsync(items,
                                      DownloadQueueOptions.FromConfig(new CliConfig()),
                                      cancellationToken: cts.Token);

        Assert.Equal(3, summary.Cancelled);
        Assert.Equal(0, summary.Succeeded);
        Assert.Empty(probe.Started);
    }

    [Fact]
    public async Task RunAsync_CancelledMidway_KeepsFinishedItemsAndCancelsTheRest()
    {
        using var cts = new CancellationTokenSource();
        var items = new List<DownloadQueueItem>
        {
            new("item-0", async cancellationToken =>
            {
                await cts.CancelAsync();
                return DownloadResult.Successful("/tmp/item-0.mp4");
            }),
            new("item-1", _ => Task.FromResult(DownloadResult.Successful("/tmp/item-1.mp4"))),
            new("item-2", _ => Task.FromResult(DownloadResult.Successful("/tmp/item-2.mp4")))
        };
        var options = new DownloadQueueOptions
        {
            ParallelEnabled = false,
            MaxConcurrency  = 1
        };

        var summary = await new DownloadQueueService()
                            .RunAsync(items, options, cancellationToken: cts.Token);

        Assert.Equal(DownloadQueueItemState.Completed, summary.Results[0].State);
        Assert.Equal(DownloadQueueItemState.Cancelled, summary.Results[1].State);
        Assert.Equal(DownloadQueueItemState.Cancelled, summary.Results[2].State);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(2, summary.Cancelled);
    }

    [Fact]
    public async Task RunAsync_EmptyQueue_ReturnsEmptySummary()
    {
        var summary = await new DownloadQueueService()
                            .RunAsync([],
                                      DownloadQueueOptions.FromConfig(new CliConfig()),
                                      cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(summary.Results);
        Assert.False(summary.AllSucceeded);
    }

    [Fact]
    public async Task RunAsync_ReportsRunningAndTerminalStatePerItem()
    {
        var reports = new List<DownloadQueueProgress>();
        var reporter = new SynchronousReporter(reports);
        var items    = new List<DownloadQueueItem>
        {
            new("item-0", _ => Task.FromResult(DownloadResult.Successful("/tmp/item-0.mp4"))),
            new("item-1", _ => Task.FromResult(DownloadResult.Failed("yok")))
        };

        await new DownloadQueueService()
              .RunAsync(items,
                        DownloadQueueOptions.FromConfig(new CliConfig()),
                        reporter,
                        TestContext.Current.CancellationToken);

        Assert.Equal(2, reports.Count(report => report.State == DownloadQueueItemState.Running));
        Assert.Contains(reports,
                        report => report is { Index: 0, State: DownloadQueueItemState.Completed });
        Assert.Contains(reports, report => report is { Index: 1, State: DownloadQueueItemState.Failed });
    }

    [Fact]
    public void Options_ClampOutOfRangeConcurrencyAtTheStoredValue()
    {
        // Saklanan değer ile kullanılan değer ayrışmamalı: sıkıştırma nesne kurulurken yapılır.
        Assert.Equal(1, new DownloadQueueOptions { MaxConcurrency = 0 }.MaxConcurrency);
        Assert.Equal(1, new DownloadQueueOptions { MaxConcurrency = -5 }.MaxConcurrency);
        Assert.Equal(8, new DownloadQueueOptions { MaxConcurrency = 99 }.MaxConcurrency);

        Assert.Equal(1, new DownloadQueueOptions { MaxConcurrency = 0 }.EffectiveConcurrency);
        Assert.Equal(8, new DownloadQueueOptions { MaxConcurrency = 99 }.EffectiveConcurrency);
        Assert.Equal(1, new DownloadQueueOptions { MaxConcurrency = 99, ParallelEnabled = false }.EffectiveConcurrency);
        Assert.Equal(CliConfig.DefaultDownloadConcurrency, new DownloadQueueOptions().MaxConcurrency);
    }

    [Fact]
    public void Options_FromConfigKeepsTheUserSuppliedNumber()
    {
        var config = new CliConfig
        {
            DownloadParallelEnabled = true,
            DownloadConcurrency     = 6
        };

        var options = DownloadQueueOptions.FromConfig(config);

        Assert.Equal(6, options.MaxConcurrency);
        Assert.Equal(6, options.EffectiveConcurrency);

        config.DownloadConcurrency = 99;
        var raised = DownloadQueueOptions.FromConfig(config);
        Assert.Equal(CliConfig.MaxDownloadConcurrency, raised.MaxConcurrency);
        Assert.Equal(CliConfig.MaxDownloadConcurrency, raised.EffectiveConcurrency);
    }

    [Fact]
    public void Options_FromConfigUsesTheSettingsSurface()
    {
        var config = new CliConfig
        {
            DownloadParallelEnabled = true,
            DownloadConcurrency     = 5
        };
        var options = DownloadQueueOptions.FromConfig(config);
        Assert.True(options.ParallelEnabled);
        Assert.Equal(5, options.EffectiveConcurrency);

        config.DownloadParallelEnabled = false;
        Assert.Equal(1, DownloadQueueOptions.FromConfig(config).EffectiveConcurrency);
    }

    [Fact]
    public void Item_RequiresADisplayNameAndWork()
    {
        Assert.Throws<ArgumentException>(() => new DownloadQueueItem("  ", _ => Task.FromResult(DownloadResult
                                                   .Failed("x"))));
        Assert.Throws<ArgumentNullException>(() => new DownloadQueueItem("item", null!));
    }

    [Fact]
    public async Task CandidateChain_TriesNextSourceOnlyWhenThePreviousOneFailed()
    {
        var service = new RecordingDownloadService(new Dictionary<string, DownloadResult>(StringComparer.Ordinal)
        {
            ["https://origin.example/broken.mp4"] = DownloadResult.Failed("kaynak bozuk"),
            ["https://origin.example/ok.mp4"]     = DownloadResult.Successful("/tmp/ok.mp4")
        });
        var candidates = new List<VideoSource>
        {
            Source("https://origin.example/broken.mp4"),
            Source("https://origin.example/ok.mp4"),
            Source("https://origin.example/never-tried.mp4")
        };

        var chain = DownloadQueueWork.ForCandidateChain(service,
                                                        candidates,
                                                        videoSource => new DownloadRequest
                                                        {
                                                            Source = videoSource,
                                                            OutputDirectory = "/tmp"
                                                        });

        var result = await chain(TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("/tmp/ok.mp4", result.MediaPath);
        Assert.Equal(new[] { "https://origin.example/broken.mp4", "https://origin.example/ok.mp4" },
                     service.Attempted);
    }

    [Fact]
    public async Task CandidateChain_AllSourcesFailing_ReturnsTheLastFailure()
    {
        var service = new RecordingDownloadService(new Dictionary<string, DownloadResult>(StringComparer.Ordinal)
        {
            ["https://origin.example/a.mp4"] = DownloadResult.Failed("a hatası"),
            ["https://origin.example/b.mp4"] = DownloadResult.Failed("b hatası")
        });
        var candidates = new List<VideoSource>
        {
            Source("https://origin.example/a.mp4"),
            Source("https://origin.example/b.mp4")
        };
        var chain = DownloadQueueWork.ForCandidateChain(service,
                                                        candidates,
                                                        videoSource => new DownloadRequest
                                                        {
                                                            Source = videoSource,
                                                            OutputDirectory = "/tmp"
                                                        });

        var result = await chain(TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("b hatası", result.Error);
        Assert.Equal(2, service.Attempted.Count);
    }

    [Fact]
    public void AddDownloadServices_RegistersTheQueueService()
    {
        var services = new ServiceCollection();
        services.AddDownloadServices();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDownloadQueueService>());
    }

    private static DownloadQueueItem SuccessfulItem(ConcurrencyProbe probe, int index)
    {
        return new DownloadQueueItem($"item-{index}", async cancellationToken =>
        {
            await probe.RunAsync(index, cancellationToken);
            return DownloadResult.Successful($"/tmp/item-{index}.mp4");
        });
    }

    private static VideoSource Source(string url)
    {
        return new VideoSource
        {
            Url  = url,
            Type = VideoType.Mp4
        };
    }

    /// <summary>Eşzamanlı kaç işin çalıştığını ve hangi sırayla bittiğini kaydeder.</summary>
    private sealed class ConcurrencyProbe
    {
        private readonly Lock _sync = new();
        private          int  _active;
        private          int  _peak;

        public List<int> Started  { get; } = [];
        public List<int> Finished { get; } = [];

        public int Peak
        {
            get
            {
                lock (_sync)
                {
                    return _peak;
                }
            }
        }

        public async Task RunAsync(int index, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Started.Add(index);
                _active++;
                if (_active > _peak)
                {
                    _peak = _active;
                }
            }

            await Task.Delay(60, cancellationToken);

            lock (_sync)
            {
                _active--;
                Finished.Add(index);
            }
        }
    }

    private sealed class SynchronousReporter : IProgress<DownloadQueueProgress>
    {
        private readonly List<DownloadQueueProgress> _reports;

        public SynchronousReporter(List<DownloadQueueProgress> reports)
        {
            _reports = reports;
        }

        public void Report(DownloadQueueProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }
        }
    }

    private sealed class RecordingDownloadService : IDownloadService
    {
        private readonly Dictionary<string, DownloadResult> _results;

        public RecordingDownloadService(Dictionary<string, DownloadResult> results)
        {
            _results = results;
        }

        public List<string> Attempted { get; } = [];

        public Task<DownloadResult> DownloadAsync(
            DownloadRequest   request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempted.Add(request.Source.Url);
            return Task.FromResult(_results.TryGetValue(request.Source.Url, out var result)
                                       ? result
                                       : DownloadResult.Failed("kayıt yok"));
        }

        public Task<DownloadResult> DownloadAsync(
            VideoSource                  source,
            AnimeDetails                 details,
            Episode                      episode,
            string                       outputDirectory,
            bool                         overwrite           = false,
            bool                         resume              = true,
            IProgress<DownloadProgress>? progress           = null,
            CancellationToken             cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
