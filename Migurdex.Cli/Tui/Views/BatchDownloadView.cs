using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Globalization;

namespace Migurdex.Cli.Tui.Views;

/// <summary>
/// Toplu indirme ekranı: bölümleri çoklu seçtirir, ayarlardaki paralel indirme ayarına göre
/// kuyruğu çalıştırır ve sonuçları tek tek raporlar.
///
/// Karar mantığı <see cref="BatchDownloadPlan"/> içindedir (sıralama, seçim, kuyruk işi, özet);
/// bu sınıf yalnızca çizim ve akışı yönetir. Hedef dizin <see cref="CliConfig.DownloadDirectory"/>'dir;
/// bu değer config.json'a yazıldığı için sonraki çalıştırmalarda da aynı kalır. Tek bölüm indirme
/// akışı (<see cref="EpisodeSourcesView"/>) bu ekrandan etkilenmez.
/// </summary>
public class BatchDownloadView : BaseView
{
    private const int MaxRows = 12;

    /// <summary>
    /// Liste dışındaki çerçeve satırları: ekran başlığı, kuyruk başlığı, hız satırı, ipuçları.
    /// <see cref="ListWindow.Budget"/> bunun üstüne taşma payı ekler; liste her zaman pencereye sığar.
    /// </summary>
    internal const int GridChromeRows = 9;

    /// <summary>Satır başındaki işaret + boşluğun görünür genişliği ("▶ ").</summary>
    private const int RowPrefixWidth = 2;

    private readonly IApiClientService     _apiClient;
    private readonly IConfigurationService _configService;
    private readonly IDownloadService      _downloadService;
    private readonly IDownloadQueueService _queueService;

    private string?       _provider;
    private string?       _animeId;
    private string?       _animeTitle;
    private int?          _season;
    private List<Episode> _episodes      = [];
    private List<int>?    _lastSelection;

    public BatchDownloadView(
        IApiClientService     apiClient,
        IConfigurationService configService,
        IDownloadService      downloadService,
        IDownloadQueueService queueService)
    {
        _apiClient       = apiClient;
        _configService   = configService;
        _downloadService = downloadService;
        _queueService    = queueService;
    }

    /// <summary>
    /// <paramref name="season"/> verilirse liste yalnızca o sezonun bölümlerini gösterir; sezon
    /// seçilmemişse (tek sezonlu anlameler) tüm bölümler listelenir.
    /// </summary>
    public void SetTarget(
        string        provider,
        string        animeId,
        string        animeTitle,
        List<Episode> episodes,
        int?          season = null)
    {
        if (_animeId != animeId || _provider != provider || _season != season)
        {
            _lastSelection = null;
        }

        _provider    = provider;
        _animeId     = animeId;
        _animeTitle  = animeTitle;
        _episodes    = episodes;
        _season      = season;
    }

    public override string GetRpcState()
    {
        return string.IsNullOrWhiteSpace(_animeTitle) ? "Toplu indirme" : _animeTitle;
    }

    public override async Task RenderAsync(ITuiNavigator navigator)
    {
        if (string.IsNullOrWhiteSpace(_provider)
            || string.IsNullOrWhiteSpace(_animeTitle)
            || _episodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Hata: Gerekli parametreler eksik.[/]");
            if (!TuiConsole.WaitForKey())
            {
                return;
            }

            navigator.Pop();
            return;
        }

        var provider   = _provider!;
        var animeTitle = _animeTitle!;
        var config     = _configService.Config;
        var ordered    = BatchDownloadPlan.OrderEpisodes(_episodes, _season);

        if (ordered.Count == 0)
        {
            Toast.Show("[yellow]Bu sezon için bölüm bulunamadı.[/]");
            navigator.Pop();
            return;
        }

        var options = DownloadQueueOptions.FromConfig(config);
        var seasonLine = _season.HasValue
                             ? $"  [grey]•[/]  [grey]Sezon:[/] {_season.Value}"
                             : string.Empty;

        var selected = EpisodeMultiSelectPrompt.Show(
            $"Toplu indirme • {animeTitle}",
            BatchDownloadPlan.BuildLabels(ordered),
            headerLines:
            [
                $"[grey]Hedef:[/] {Markup.Escape(config.DownloadDirectory)}{seasonLine}",
                $"[grey]Eşzamanlılık:[/] {Markup.Escape(BatchDownloadPlan.DescribeConcurrency(options.ParallelEnabled, options.EffectiveConcurrency))}"
            ],
            preselected: _lastSelection);

        if (selected is null || selected.Count == 0)
        {
            navigator.Pop();
            return;
        }

        _lastSelection = selected;

        var chosen = BatchDownloadPlan.FromSelection(ordered, selected);

        AnsiConsole.Clear();
        Theme.WriteHeader("Toplu indirme", animeTitle);
        AnsiConsole.MarkupLine($"[grey]Bölüm:[/] {chosen.Count} / {ordered.Count}"
                               + (_season.HasValue ? $"  [grey]•[/]  [grey]Sezon:[/] {_season.Value}" : string.Empty));
        AnsiConsole.MarkupLine($"[grey]Hedef:[/] {Markup.Escape(config.DownloadDirectory)}");
        AnsiConsole.MarkupLine(
            $"[grey]Eşzamanlılık:[/] {Markup.Escape(BatchDownloadPlan.DescribeConcurrency(options.ParallelEnabled, options.EffectiveConcurrency))}");
        AnsiConsole.MarkupLine(
            $"[grey]Altyazı:[/] {(config.DownloadSubtitles ? "açık" : "kapalı")}  [grey]•[/]  "
            + $"[grey]Üzerine yaz:[/] {(config.DownloadOverwrite ? "açık" : "kapalı")}  [grey]•[/]  "
            + $"[grey]Kaldığı yerden:[/] {(config.DownloadResume ? "açık" : "kapalı")}");
        AnsiConsole.WriteLine();

        if (!Theme.Confirm("[cyan]İndirme başlatılsın mı?[/]"))
        {
            navigator.Pop();
            return;
        }

        var trackers = new DownloadProgressTracker[chosen.Count];
        var states   = new DownloadQueueItemState[chosen.Count];
        var paths    = new string?[chosen.Count];
        var items    = new DownloadQueueItem[chosen.Count];
        for (var index = 0; index < chosen.Count; index++)
        {
            var episode   = chosen[index];
            var tracker   = new DownloadProgressTracker();
            trackers[index] = tracker;
            items[index]    = BatchDownloadPlan.BuildItem(_apiClient,
                                                          _downloadService,
                                                          provider,
                                                          DownloadSourceFormat.Auto,
                                                          null,
                                                          episode,
                                                          config,
                                                          source => new DownloadRequest
                                                          {
                                                              Source            = source,
                                                              OutputDirectory   = config.DownloadDirectory,
                                                              AnimeTitle        = animeTitle,
                                                              EpisodeTitle      = episode.Title,
                                                              Season            = episode.Season ?? 1,
                                                              EpisodeNumber     = episode.Number,
                                                              Overwrite         = config.DownloadOverwrite,
                                                              Resume            = config.DownloadResume,
                                                              DownloadSubtitles = config.DownloadSubtitles,
                                                              Progress          = tracker
                                                          });
        }

        var summary = await RunQueueAsync(items, options, trackers, states, paths);

        ShowSummary(summary, chosen, config.DownloadDirectory);
        navigator.Pop();
    }

    private async Task<DownloadQueueSummary> RunQueueAsync(
        DownloadQueueItem[]       items,
        DownloadQueueOptions      options,
        DownloadProgressTracker[] trackers,
        DownloadQueueItemState[]  states,
        string?[]                 paths)
    {
        // Kuyruk işi kendi ilerlemesini tracker'a yazar; burada iş durumu ve sonuç yolu tutulur.
        var reporter = new QueueStateReporter(states, paths);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TuiApplicationCancellation.Token);
        using var modal = TuiApplicationCancellation.BeginModal(cancellation);

        AnsiConsole.Clear();
        Theme.WriteHeader("Toplu indirme", _animeTitle);
        AnsiConsole.MarkupLine(
            $"[grey]{Markup.Escape(BatchDownloadPlan.DescribeConcurrency(options.ParallelEnabled, options.EffectiveConcurrency))} • Esc: iptal[/]");
        AnsiConsole.WriteLine();

        DownloadQueueSummary? summary = null;
        var userCancelled = false;

        await AnsiConsole.Live(BuildProgressGrid(items, trackers, states, paths))
                         .StartAsync(async context =>
                         {
                             var runTask = _queueService.RunAsync(items,
                                                                  options,
                                                                  reporter,
                                                                  cancellation.Token);
                             var last    = string.Empty;
                             while (!runTask.IsCompleted)
                             {
                                 if (Console.KeyAvailable
                                     && Console.ReadKey(true).Key == ConsoleKey.Escape)
                                 {
                                     userCancelled = true;
                                     await cancellation.CancelAsync().ConfigureAwait(false);
                                     break;
                                 }

                                 var fingerprint = BuildFingerprint(trackers, states);
                                 if (!fingerprint.Equals(last, StringComparison.Ordinal))
                                 {
                                     context.UpdateTarget(BuildProgressGrid(items,
                                                                            trackers,
                                                                            states,
                                                                            paths));
                                     last = fingerprint;
                                 }

                                 await Task.Delay(100).ConfigureAwait(false);
                             }

                             summary = await runTask.ConfigureAwait(false);
                             context.UpdateTarget(BuildProgressGrid(items, trackers, states, paths));
                         })
                         .ConfigureAwait(false);

        summary ??= new DownloadQueueSummary
        {
            Results = []
        };

        if (userCancelled)
        {
            Toast.Show("[yellow]İptal edildi; tamamlanan dosyalar diskte kaldı.[/]");
        }

        return summary;
    }

    private static Grid BuildProgressGrid(
        DownloadQueueItem[]       items,
        DownloadProgressTracker[] trackers,
        DownloadQueueItemState[]  states,
        string?[]                 paths)
    {
        var grid = new Grid();
        grid.AddColumn();

        var done       = 0;
        var failed     = 0;
        var running    = 0;
        var totalSpeed = 0d;
        for (var index = 0; index < states.Length; index++)
        {
            switch (states[index])
            {
                case DownloadQueueItemState.Completed:
                    done++;
                    break;
                case DownloadQueueItemState.Failed:
                    failed++;
                    break;
                case DownloadQueueItemState.Running:
                    running++;
                    totalSpeed += trackers[index].CurrentSpeed;
                    break;
            }
        }

        var suffix = failed > 0 ? $"  [red]{failed} hata[/]" : string.Empty;
        grid.AddRow(new Markup($"[bold cyan]Toplu indirme[/]  [grey]{done} / {items.Length} tamam[/]{suffix}"));

        // Tek bir toplam hız: tek tek satırlara bakınca sona kalan yavaş dosya bütün
        // indirmeyi yavaş gösteriyordu.
        var speedText = running == 0
                            ? "[grey]toplam hız: —[/]"
                            : totalSpeed > 0
                                ? $"[grey]{FormatBytes((long) totalSpeed)}/s toplam[/]  [grey]•[/]  [grey]{running} etkin[/]"
                                : $"[grey]{running} etkin[/]";
        grid.AddRow(new Markup(speedText));
        grid.AddRow(new Text(string.Empty));

        // Kısa terminalde liste + çerçeve ekrandan taşarsa terminal kayar (başlık yukarı kaçar,
        // kalıntı kalır). Satır sayısı pencere yüksekliğinden, satır genişliği sütun
        // sayısından türetilir; her kayıt tek fiziksel satır kalır.
        var rowBudget = ListWindow.Budget(TuiConsole.WindowHeight, GridChromeRows, MaxRows);
        var width     = Math.Max(40, TuiConsole.WindowWidth - 1);

        var firstUnfinished = Array.FindIndex(states,
                                              state => state is not (DownloadQueueItemState.Completed
                                                                     or DownloadQueueItemState.Failed
                                                                     or DownloadQueueItemState.Cancelled));
        if (firstUnfinished < 0)
        {
            firstUnfinished = Math.Max(0, items.Length - rowBudget);
        }

        var startIndex = Math.Max(0, firstUnfinished - 1);
        var endIndex   = Math.Min(items.Length, startIndex + rowBudget);
        if (endIndex - startIndex < rowBudget && startIndex > 0)
        {
            startIndex = Math.Max(0, endIndex - rowBudget);
        }

        for (var index = startIndex; index < endIndex; index++)
        {
            var name = items[index].DisplayName;
            switch (states[index])
            {
                case DownloadQueueItemState.Completed:
                {
                    var fit = BatchProgressLine.FitArrow(name,
                                                         paths[index] ?? "tamamlandı",
                                                         RowPrefixWidth,
                                                         width);
                    grid.AddRow(new Markup(
                                    $"[green]✓[/] {Markup.Escape(fit.Label)} [grey]→ {Markup.Escape(fit.Detail)}[/]"));
                    break;
                }
                case DownloadQueueItemState.Failed:
                {
                    var fit = BatchProgressLine.Fit(name, "başarısız", RowPrefixWidth, width);
                    grid.AddRow(new Markup(
                                    $"[red]✗[/] {Markup.Escape(fit.Label)} [red]{Markup.Escape(fit.Detail)}[/]"));
                    break;
                }
                case DownloadQueueItemState.Cancelled:
                {
                    var fit = BatchProgressLine.Fit(name, "iptal edildi", RowPrefixWidth, width);
                    grid.AddRow(new Markup(
                                    $"[yellow]•[/] {Markup.Escape(fit.Label)} [grey]{Markup.Escape(fit.Detail)}[/]"));
                    break;
                }
                case DownloadQueueItemState.Running:
                {
                    var fit = BatchProgressLine.Fit(name,
                                                    DescribeProgress(trackers[index]),
                                                    RowPrefixWidth,
                                                    width);
                    grid.AddRow(new Markup(
                                    $"[cyan]▶[/] {Markup.Escape(fit.Label)} [grey]{Markup.Escape(fit.Detail)}[/]"));
                    break;
                }
                default:
                {
                    var fit = BatchProgressLine.Fit(name, "sırada", RowPrefixWidth, width);
                    grid.AddRow(new Markup(
                                    $"[grey]·[/] [grey]{Markup.Escape(fit.Label)} • {Markup.Escape(fit.Detail)}[/]"));
                    break;
                }
            }
        }

        grid.AddRow(new Text(string.Empty));
        grid.AddRow(new Markup("[grey]Esc: kalan indirmeleri iptal et[/]"));
        return grid;
    }

    private static string BuildFingerprint(DownloadProgressTracker[] trackers, DownloadQueueItemState[] states)
    {
        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < states.Length; index++)
        {
            builder.Append((int) states[index]).Append(':').Append(trackers[index].Version).Append('|');
        }

        return builder.ToString();
    }

    private static string DescribeProgress(DownloadProgressTracker tracker)
    {
        var progress = tracker.Current;
        if (progress is null)
        {
            return "hazırlanıyor";
        }

        var detail = DownloadStageLabel.For(progress.Stage, progress.Track, progress.IsAudioTrack);
        if (!string.IsNullOrWhiteSpace(progress.Track))
        {
            detail += $" • {progress.Track}";
        }

        if (progress is { Stage: DownloadStage.Downloading, TotalBytes: > 0 })
        {
            var percent = progress.Percent
                          ?? Math.Min(100, (double) progress.BytesDownloaded * 100 / progress.TotalBytes.Value);
            detail += $" • %{percent.ToString("0.#", CultureInfo.InvariantCulture)}";
        }

        // Duraklamada uydurma bir hız yerine durum yazılır: bayt akmıyorsa hız da yoktur.
        var idle    = tracker.IdleFor(DateTimeOffset.UtcNow);
        var stalled = idle is { TotalSeconds: >= 3 };
        var speed   = tracker.CurrentSpeed;
        if (progress.Stage == DownloadStage.Downloading)
        {
            if (stalled)
            {
                detail += $" • duraklı {Math.Max(0, (int) idle!.Value.TotalSeconds)} sn";
            }
            else if (speed > 0)
            {
                detail += $" • {FormatBytes((long) speed)}/s";
            }
        }

        return detail;
    }

    private static void ShowSummary(
        DownloadQueueSummary summary,
        List<Episode>        chosen,
        string               outputDirectory)
    {
        var headerLines = new List<string>
        {
            $"[grey]Hedef:[/] {Markup.Escape(outputDirectory)}",
            $"[grey]Bölüm:[/] {summary.Total}  [grey]•[/]  [green]tamam: {summary.Succeeded}[/]  "
            + $"[grey]•[/]  [red]hata: {summary.Failed}[/]  [grey]•[/]  [yellow]iptal: {summary.Cancelled}[/]"
        };

        var choices = BatchDownloadPlan.BuildSummaryRows(summary, chosen)
                                       .Select(row => new FuzzyChoice
                                       {
                                           Display = row.State switch
                                           {
                                               DownloadQueueItemState.Completed =>
                                                   $"[green]✓[/] {Markup.Escape(row.Label)} [grey]→ {Markup.Escape(Theme.Ellipsize(row.Detail, 70))}[/]",
                                               DownloadQueueItemState.Failed =>
                                                   $"[red]✗[/] {Markup.Escape(row.Label)} [grey]{Markup.Escape(row.Detail)}[/]",
                                               _ =>
                                                   $"[yellow]•[/] {Markup.Escape(row.Label)} [grey]{Markup.Escape(row.Detail)}[/]"
                                           },
                                           DisplayActive = row.State switch
                                           {
                                               DownloadQueueItemState.Completed => $"[bold white]✓ {Markup.Escape(row.Label)}[/]",
                                               DownloadQueueItemState.Failed    => $"[bold white]✗ {Markup.Escape(row.Label)}[/]",
                                               _                                => $"[bold white]• {Markup.Escape(row.Label)}[/]"
                                           },
                                           Searchable = row.Label
                                       })
                                       .ToList();

        choices.Add(TuiHelpers.Back());

        FuzzyPrompt.Show(BatchDownloadPlan.SummaryTitle(summary),
                         choices,
                         headerLines: headerLines,
                         footerHelp: "↑↓ gez • Enter/Esc geri",
                         searchable: choices.Count > 6);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value   = Math.Max(0, bytes);
        var    unit    = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
                   ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[unit]}"
                   : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    /// <summary>Kuyruk işi durumunu doğrudan (senkron) yazan ilerleme alıcısı.</summary>
    private sealed class QueueStateReporter : IProgress<DownloadQueueProgress>
    {
        private readonly DownloadQueueItemState[] _states;
        private readonly string?[]                _paths;

        public QueueStateReporter(DownloadQueueItemState[] states, string?[] paths)
        {
            _states = states;
            _paths  = paths;
        }

        public void Report(DownloadQueueProgress value)
        {
            if (value.Index < 0 || value.Index >= _states.Length)
            {
                return;
            }

            _states[value.Index] = value.State;
            if (!string.IsNullOrWhiteSpace(value.Result?.MediaPath))
            {
                _paths[value.Index] = value.Result!.MediaPath;
            }
        }
    }

    /// <summary>
    /// İndirme aşamasını ve hızını tutan hafif izleyici. <see cref="Version"/> her raporda artar;
    /// ekran yalnızca değişiklik olduğunda yeniden çizilir.
    /// </summary>
    private sealed class DownloadProgressTracker : IProgress<DownloadProgress>
    {
        private readonly Lock                _sync = new();
        private readonly DownloadSpeedometer _speed = new();
        private          DownloadProgress?   _current;
        private          long                _version;

        public DownloadProgress? Current
        {
            get
            {
                lock (_sync)
                {
                    return _current;
                }
            }
        }

        public long Version
        {
            get
            {
                lock (_sync)
                {
                    return _version;
                }
            }
        }

        public double CurrentSpeed
        {
            get
            {
                lock (_sync)
                {
                    return _current?.SpeedBytesPerSecond ?? _speed.BytesPerSecond;
                }
            }
        }

        public TimeSpan? IdleFor(DateTimeOffset now)
        {
            lock (_sync)
            {
                return _speed.IdleFor(now);
            }
        }

        public void Report(DownloadProgress value)
        {
            lock (_sync)
            {
                _speed.Sample(value.BytesDownloaded, DateTimeOffset.UtcNow);
                _current = value;
                _version++;
            }
        }
    }
}
