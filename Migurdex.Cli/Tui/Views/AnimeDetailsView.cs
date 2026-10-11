using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Core.Database;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class AnimeDetailsView : BaseView
{
    private readonly IApiClientService _apiClient;
    private readonly IHistoryService   _historyService;
    private readonly IServiceProvider  _serviceProvider;
    private          string?           _animeId;
    private          string?           _animeTitle;
    private          AnimeDetails?     _cachedDetails;
    private          string?           _initialPosterUrl;
    private          string?           _lastSelectedSearchable;
    private          string?           _lastSelectedSeasonSearchable;
    private          string?           _provider;
    private          int?              _selectedSeason;

    public AnimeDetailsView(IApiClientService apiClient,
        IHistoryService                       historyService,
        IServiceProvider                      serviceProvider)
    {
        _apiClient       = apiClient;
        _historyService  = historyService;
        _serviceProvider = serviceProvider;
    }

    public void SetTarget(string provider, string animeId, string? initialPosterUrl = null, string? animeTitle = null)
    {
        if (_provider != provider || _animeId != animeId)
        {
            _cachedDetails                = null;
            _animeTitle                   = animeTitle;
            _selectedSeason               = null;
            _lastSelectedSeasonSearchable = null;
            _lastSelectedSearchable       = null;
        }

        _provider         = provider;
        _animeId          = animeId;
        _initialPosterUrl = initialPosterUrl;
    }

    public override string GetRpcState()
    {
        var title = _cachedDetails?.Title ?? _animeTitle;
        return string.IsNullOrWhiteSpace(title) ? "Detaylar" : title;
    }

    public override async Task RenderAsync(ITuiNavigator navigator)
    {
        if (string.IsNullOrEmpty(_provider) || string.IsNullOrEmpty(_animeId))
        {
            AnsiConsole.MarkupLine("[red]Hata: Sağlayıcı veya anime belirtilmemiş.[/]");
            if (!TuiConsole.WaitForKey())
            {
                return;
            }

            navigator.Pop();
            return;
        }

        var     details   = _cachedDetails;
        string? loadError = null;
        if (details == null)
        {
            AnsiConsole.Clear();
            Theme.WriteHeader(_animeTitle ?? "Detaylar");
            AnsiConsole.MarkupLine(TuiHelpers.ProviderLine(_provider!));
            AnsiConsole.WriteLine();

            await AnsiConsole.Status()
                             .Spinner(Spinner.Known.Dots)
                             .StartAsync("Yükleniyor...",
                                         async ctx =>
                                         {
                                             try
                                             {
                                                 var result =
                                                     await _apiClient.GetAnimeDetailsAsync(_provider, _animeId);

                                                 details   = result.Data;
                                                 loadError = result.Error;
                                             }
                                             catch (Exception ex)
                                             {
                                                 loadError = ex.Message;
                                                 AnsiConsole.WriteException(ex);
                                             }
                                         });

            if (details == null)
            {
                AnsiConsole.MarkupLine("[red]Veri yok.[/]");
                if (loadError is not null)
                {
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(loadError)}[/]");
                }

                if (!TuiConsole.WaitForKey())
                {
                    return;
                }

                navigator.Pop();
                return;
            }

            if (string.IsNullOrWhiteSpace(details.PosterUrl) && !string.IsNullOrWhiteSpace(_initialPosterUrl))
            {
                details.PosterUrl = _initialPosterUrl;
            }

            details.Normalize();

            _cachedDetails = details;
            AnsiConsole.Clear();
        }

        var isFav = _historyService.IsFavorite(_animeId, _provider);

        var metaParts = new List<string>
        {
            TuiHelpers.ProviderLine(_provider!),
            $"[grey]Format:[/] {details.Format}",
            $"[grey]{details.Episodes.Count} bölüm[/]"
        };

        if (isFav)
        {
            metaParts.Add("[yellow]♥ Favorilerde[/]");
        }

        var headerLines = new List<string>
        {
            string.Join("  [grey]•[/]  ", metaParts)
        };

        if (!string.IsNullOrWhiteSpace(details.Summary) && details.Summary != "-")
        {
            foreach (var line in Theme.WrapText(details.Summary))
            {
                headerLines.Add($"[grey]{Markup.Escape(line)}[/]");
            }
        }

        var groupedEpisodes = details.Episodes
                                     .GroupBy(e => e.Season ?? 1)
                                     .OrderBy(g => g.Key)
                                     .ToList();

        var isMultiSeason = groupedEpisodes.Count > 1;

        if (isMultiSeason && _selectedSeason == null)
        {
            var seasonChoices = new List<FuzzyChoice>
            {
                new()
                {
                    Display       = isFav ? "[yellow]♥ Favorilerden çıkar[/]" : "[yellow]♡ Favorilere ekle[/]",
                    DisplayActive = isFav ? "[bold white]♥ Favorilerden çıkar[/]" : "[bold white]♡ Favorilere ekle[/]",
                    Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle",
                    IsAction      = true
                }
            };

            foreach (var group in groupedEpisodes)
            {
                var seasonNum = group.Key;
                var epCount   = group.Count();
                var label     = $"{seasonNum}. Sezon";
                seasonChoices.Add(new FuzzyChoice
                {
                    Display       = $"{seasonNum}. Sezon [grey]({epCount})[/]",
                    DisplayActive = $"[bold white]{seasonNum}. Sezon ({epCount})[/]",
                    Searchable    = label
                });
            }

            seasonChoices.Add(TuiHelpers.Back());

            var seasonChoice = FuzzyPrompt.Show(details.Title,
                                                seasonChoices,
                                                initialSelection: _lastSelectedSeasonSearchable,
                                                headerLines: headerLines);

            if (seasonChoice == null || seasonChoice.Searchable == "Geri")
            {
                navigator.Pop();
                return;
            }

            _lastSelectedSeasonSearchable = seasonChoice.Searchable;

            if (seasonChoice.Searchable is "Favorilere Ekle" or "Favorilerden Çıkar")
            {
                _historyService.ToggleFavorite(new FavoriteEntry
                {
                    AnimeId      = _animeId,
                    AnimeTitle   = details.Title,
                    ProviderName = _provider
                });
                return;
            }

            var matchedGroup = groupedEpisodes.FirstOrDefault(g => $"{g.Key}. Sezon" == seasonChoice.Searchable);
            if (matchedGroup != null)
            {
                _selectedSeason         = matchedGroup.Key;
                _lastSelectedSearchable = null;
            }

            return;
        }

        var activeSeason = _selectedSeason ?? (groupedEpisodes.Count > 0 ? groupedEpisodes[0].Key : 1);
        var currentSeasonGroup = groupedEpisodes.FirstOrDefault(g => g.Key == activeSeason)
                                 ?? groupedEpisodes.FirstOrDefault();

        if (isMultiSeason)
        {
            headerLines.Add($"[grey]Sezon:[/] {activeSeason}. Sezon");
        }

        var choices = new List<FuzzyChoice>();

        var isSingleContent = details.Episodes.Count == 1;
        var episodeMap      = new Dictionary<string, Episode>();

        var resumeEpisodeId = _historyService.GetWatchHistory()
                                             .Where(h => h.ProviderName == _provider
                                                         && h.AnimeId == _animeId
                                                         && h is { IsCompleted: false, LastPositionSeconds: > 0 })
                                             .OrderByDescending(h => h.LastWatchedAt)
                                             .FirstOrDefault()
                                             ?.EpisodeId;

        if (isSingleContent)
        {
            var singleEp = details.Episodes[0];
            var isMovie = details.Format == ContentFormat.Movie
                          || AnimeDetails.IsMovieTitle(details.Title)
                          || AnimeDetails.IsMovieSummary(details.Summary);

            var playSearchable = isMovie ? "Filmi Oynat" : "Bölümü Oynat";
            var playDisplay    = isMovie ? "[bold green]▶ Filmi oynat[/]" : "[bold green]▶ Bölümü oynat[/]";
            var playActiveDisplay =
                isMovie
                    ? "[bold white on darkgreen] ▶ Filmi oynat [/]"
                    : "[bold white on darkgreen] ▶ Bölümü oynat [/]";

            choices.Add(new FuzzyChoice
            {
                Display       = playDisplay,
                DisplayActive = playActiveDisplay,
                Searchable    = playSearchable
            });

            episodeMap[playSearchable] = singleEp;

            var downloadSearchable = isMovie ? "Filmi İndir" : "Bölümü İndir";
            var downloadDisplay    = isMovie ? "[cyan]⤓ Filmi indir[/]" : "[cyan]⤓ Bölümü indir[/]";
            var downloadActiveDisplay =
                isMovie
                    ? "[bold white on grey23] ⤓ Filmi indir [/]"
                    : "[bold white on grey23] ⤓ Bölümü indir [/]";

            choices.Add(new FuzzyChoice
            {
                Display       = downloadDisplay,
                DisplayActive = downloadActiveDisplay,
                Searchable    = downloadSearchable
            });

            episodeMap[downloadSearchable] = singleEp;

            choices.Add(new FuzzyChoice
            {
                Display       = isFav ? "[yellow]♥ Favorilerden çıkar[/]" : "[yellow]♡ Favorilere ekle[/]",
                DisplayActive = isFav ? "[bold white]♥ Favorilerden çıkar[/]" : "[bold white]♡ Favorilere ekle[/]",
                Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle",
                IsAction      = true
            });
        }
        else
        {
            if (!isMultiSeason)
            {
                choices.Add(new FuzzyChoice
                {
                    Display       = isFav ? "[yellow]♥ Favorilerden çıkar[/]" : "[yellow]♡ Favorilere ekle[/]",
                    DisplayActive = isFav ? "[bold white]♥ Favorilerden çıkar[/]" : "[bold white]♡ Favorilere ekle[/]",
                    Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle",
                    IsAction      = true
                });
            }

            if (currentSeasonGroup != null)
            {
                choices.Add(new FuzzyChoice
                {
                    Display       = "[cyan]⤓ Tüm Sezonu İndir[/]",
                    DisplayActive = "[bold white on grey23] ⤓ Tüm Sezonu İndir [/]",
                    Searchable    = "Tüm Sezonu İndir",
                    IsAction      = true
                });

                choices.Add(new FuzzyChoice
                {
                    Display       = "[cyan]⤓ Özel Aralık İndir...[/]",
                    DisplayActive = "[bold white on grey23] ⤓ Özel Aralık İndir... [/]",
                    Searchable    = "Özel Aralık İndir",
                    IsAction      = true
                });

                var isMovie    = details.Format == ContentFormat.Movie;
                var unitPrefix = isMovie ? "Film" : "Bölüm";

                foreach (var ep in currentSeasonGroup.OrderBy(e => e.Number))
                {
                    var titleTrimmed = ep.Title?.Trim() ?? "";
                    var isGenericTitle = string.IsNullOrWhiteSpace(titleTrimmed)
                                         || titleTrimmed.Equals($"{ep.Number}", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}.", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"Bölüm {ep.Number}",
                                                                StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}. Bölüm",
                                                                StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}.Bölüm",
                                                                StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"Film {ep.Number}", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}. Film",
                                                                StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals($"{ep.Number}.Film", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals("Film", StringComparison.OrdinalIgnoreCase)
                                         || titleTrimmed.Equals("Bölüm", StringComparison.OrdinalIgnoreCase);

                    var hasCustomTitle = !isGenericTitle;

                    var shortTitle       = hasCustomTitle ? Theme.Ellipsize(ep.Title!, 44) : string.Empty;
                    var isResume         = ep.Id == resumeEpisodeId;
                    var resumeBadge      = isResume ? $" {Theme.Badge("devam")}" : "";
                    var resumeSearchable = isResume ? " devam" : "";
                    var display = hasCustomTitle
                                      ? $"[grey]{ep.Number:00}[/] {unitPrefix} [grey]│ {Markup.Escape(shortTitle)}[/]{resumeBadge}"
                                      : $"[grey]{ep.Number:00}[/] {unitPrefix}{resumeBadge}";

                    var displayActive = hasCustomTitle
                                            ? $"[bold white]{ep.Number:00} {unitPrefix} │ {Markup.Escape(shortTitle)}[/]{resumeBadge}"
                                            : $"[bold white]{ep.Number:00} {unitPrefix}[/]{resumeBadge}";

                    var searchable = (hasCustomTitle
                                          ? $"{ep.Number}. {unitPrefix} {ep.Title}"
                                          : $"{ep.Number}. {unitPrefix}")
                                     + resumeSearchable;

                    choices.Add(new FuzzyChoice
                    {
                        Display       = display,
                        DisplayActive = displayActive,
                        Searchable    = searchable
                    });

                    episodeMap[searchable] = ep;
                }
            }
        }

        choices.Add(TuiHelpers.Back());

        var choice = FuzzyPrompt.Show(details.Title,
                                      choices,
                                      initialSelection: _lastSelectedSearchable,
                                      headerLines: headerLines);

        if (choice == null || choice.Searchable == "Geri")
        {
            if (isMultiSeason)
            {
                _selectedSeason         = null;
                _lastSelectedSearchable = null;
                return;
            }

            navigator.Pop();
            return;
        }

        _lastSelectedSearchable = choice.Searchable;

        if (choice.Searchable is "Favorilere Ekle" or "Favorilerden Çıkar")
        {
            _historyService.ToggleFavorite(new FavoriteEntry
            {
                AnimeId      = _animeId,
                AnimeTitle   = details.Title,
                ProviderName = _provider
            });
            return;
        }

        if (choice.Searchable is "Tüm Sezonu İndir")
        {
            var numbers = currentSeasonGroup!.OrderBy(e => e.Number).Select(e => e.Number).Distinct().ToList();
            await RunBatchDownloadAsync(navigator, details, numbers, activeSeason).ConfigureAwait(false);
            return;
        }

        if (choice.Searchable is "Özel Aralık İndir")
        {
            var input = Theme.Ask("Bölümler (örn. 1-12, 1,3,5, all):", string.Empty)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            if (!EpisodeRangeParser.TryParse(input, out var parsed, out var selectAll))
            {
                Toast.Show("[red]Geçersiz aralık. Örn. 1-12 veya 1,3,5.[/]");
                return;
            }

            List<double> numbers;
            if (selectAll)
            {
                numbers = currentSeasonGroup!.OrderBy(e => e.Number).Select(e => e.Number).Distinct().ToList();
            }
            else
            {
                var available = currentSeasonGroup!.Select(e => e.Number).ToHashSet();
                numbers = parsed.Where(available.Contains).OrderBy(n => n).ToList();
                if (numbers.Count == 0)
                {
                    Toast.Show("[red]Bu sezonda eşleşen bölüm yok.[/]");
                    return;
                }

                if (numbers.Count < parsed.Count)
                {
                    Toast.Show("[yellow]Bazı bölümler bu sezonda yok; eşleşenler kuyruğa alındı.[/]");
                }
            }

            await RunBatchDownloadAsync(navigator, details, numbers, activeSeason).ConfigureAwait(false);
            return;
        }

        if (episodeMap.TryGetValue(choice.Searchable, out var selectedEp))
        {
            if (isSingleContent)
            {
                var singleMode = choice.Searchable.Contains("İndir", StringComparison.OrdinalIgnoreCase)
                                     ? SourceViewMode.Download
                                     : SourceViewMode.Play;
                var singleSourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
                singleSourcesView.SetTarget(_provider,
                                            _animeId,
                                            details.Title,
                                            selectedEp,
                                            details.Episodes,
                                            details.PosterUrl,
                                            mode: singleMode);
                navigator.Push(singleSourcesView);
                return;
            }

            var defaultAction = NormalizeEpisodeAction(GetConfig()?.DefaultEpisodeAction);
            if (defaultAction == "Play")
            {
                var autoPlayView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
                autoPlayView.SetTarget(_provider,
                                       _animeId,
                                       details.Title,
                                       selectedEp,
                                       details.Episodes,
                                       details.PosterUrl,
                                       mode: SourceViewMode.Play);
                navigator.Push(autoPlayView);
                return;
            }

            if (defaultAction == "Download")
            {
                var autoDownloadView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
                autoDownloadView.SetTarget(_provider,
                                           _animeId,
                                           details.Title,
                                           selectedEp,
                                           details.Episodes,
                                           details.PosterUrl,
                                           mode: SourceViewMode.Download);
                navigator.Push(autoDownloadView);
                return;
            }

            var actions = new List<FuzzyChoice>
            {
                Theme.PlayChoice("Oynat"),
                Theme.ActionChoice("İndir", Theme.Primary),
                TuiHelpers.Back()
            };
            var action = FuzzyPrompt.Show(details.Title,
                                          actions,
                                          searchable: false,
                                          headerLines:
                                          [
                                              $"[grey]{Markup.Escape(TuiHelpers.EllipsizedTitle(details.Title))} › {Markup.Escape(TuiHelpers.FormatEpisodeRef(selectedEp))}[/]"
                                          ],
                                          footerHelp: "↑↓ gez • Enter seç • Esc geri");
            if (action is null || action.Searchable == "Geri")
            {
                return;
            }

            var mode        = action.Searchable == "İndir" ? SourceViewMode.Download : SourceViewMode.Play;
            var sourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
            sourcesView.SetTarget(_provider,
                                  _animeId,
                                  details.Title,
                                  selectedEp,
                                  details.Episodes,
                                  details.PosterUrl,
                                  mode: mode);
            navigator.Push(sourcesView);
        }
    }

    private CliConfig? GetConfig()
    {
        try
        {
            return (_serviceProvider.GetService(typeof(IConfigurationService)) as IConfigurationService)?.Config;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeEpisodeAction(string? value)
    {
        if (string.Equals(value, "Play", StringComparison.OrdinalIgnoreCase))
        {
            return "Play";
        }

        if (string.Equals(value, "Download", StringComparison.OrdinalIgnoreCase))
        {
            return "Download";
        }

        return "Ask";
    }

    private async Task RunBatchDownloadAsync(
        ITuiNavigator navigator,
        AnimeDetails  details,
        List<double>  episodeNumbers,
        int           season)
    {
        var config = GetConfig();
        var api = _serviceProvider.GetService(typeof(IApiClientService)) as IApiClientService;
        var downloadService = _serviceProvider.GetService(typeof(IDownloadService)) as IDownloadService;
        var tracker = _serviceProvider.GetService(typeof(BackgroundDownloadTracker)) as BackgroundDownloadTracker;
        if (api is null || downloadService is null || tracker is null)
        {
            Toast.Show("[red]İndirme servisi kullanılamıyor.[/]");
            return;
        }

        var filter   = new BatchFilter([], [], [], []);
        var strategy = config?.BatchSourceSelection ?? "Ask";
        if (!string.Equals(strategy, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            filter = await AskBatchFilterAsync(details, season, episodeNumbers).ConfigureAwait(false);
            if (filter is null)
            {
                return;
            }
        }

        var connections = Math.Clamp(config?.ConnectionsPerFile ?? 4, 1, 8);
        var outputDirectory = string.IsNullOrWhiteSpace(config?.DownloadDirectory)
                                  ? CliConfig.DefaultDownloadDirectory
                                  : config!.DownloadDirectory;
        var downloadSubtitles = config?.DownloadSubtitles ?? true;
        var overwrite         = config?.DownloadOverwrite ?? false;
        var resume            = config?.DownloadResume ?? true;
        var capturedFilter    = filter;
        var capturedConfig    = config;

        var queued = 0;
        foreach (var number in episodeNumbers)
        {
            var episode = details.Episodes.FirstOrDefault(e =>
                                                              (e.Season ?? 1) == season && e.Number.Equals(number));
            if (episode is null)
            {
                continue;
            }

            var epCopy = episode;
            var spec = new DownloadFilterSpec(
                [.. capturedFilter.Groups],
                [.. capturedFilter.Hosters],
                [.. capturedFilter.Qualities],
                [.. capturedFilter.Formats]);
            var providerName = _provider ?? string.Empty;
            var animeId      = _animeId ?? string.Empty;
            tracker.Enqueue(details.Title,
                            $"Bölüm {number:0.##}",
                            control => DownloadJobRunner.RunEpisodeAsync(
                                api,
                                downloadService,
                                tracker,
                                capturedConfig ?? new CliConfig(),
                                providerName,
                                details.Title,
                                epCopy,
                                epCopy.Season ?? season,
                                spec,
                                outputDirectory,
                                downloadSubtitles,
                                overwrite,
                                resume,
                                connections,
                                control),
                            id => new StoredDownloadJob(
                                id,
                                providerName,
                                animeId,
                                details.Title,
                                epCopy.Id,
                                epCopy.Title,
                                epCopy.Season ?? season,
                                epCopy.Number,
                                [.. spec.Groups],
                                [.. spec.Hosters],
                                [.. spec.Qualities],
                                [.. spec.Formats],
                                outputDirectory,
                                downloadSubtitles,
                                "queued",
                                null,
                                null,
                                DateTimeOffset.UtcNow,
                                DateTimeOffset.UtcNow));
            queued++;
        }

        if (queued == 0)
        {
            Toast.Show("[red]Kuyruğa alınacak bölüm bulunamadı.[/]");
            return;
        }

        Toast.Show($"[green]✓ {queued} bölüm kuyruğa alındı[/] - İndirilenler'den takip et.");
    }

    private async Task<List<VideoSource>> StreamProbeAsync(string episodeId, TimeSpan budget)
    {
        var       found = new List<VideoSource>();
        using var cts   = new CancellationTokenSource(budget);
        try
        {
            await foreach (var src in _apiClient
                                      .GetVideoSourcesStreamAsync(_provider!, episodeId, stats: null)
                                      .WithCancellation(cts.Token))
            {
                if (!string.IsNullOrWhiteSpace(src.Url))
                {
                    found.Add(src);
                }

                if (found.Count >= 60)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch
        {
            // ignored
        }

        return found;
    }

    private async Task<BatchFilter?> AskBatchFilterAsync(
        AnimeDetails details,
        int          season,
        List<double> episodeNumbers)
    {
        var config = GetConfig() ?? new CliConfig();
        var choices = new List<string>
        {
            "★ TAM OTOMATİK (filtresiz)"
        };
        var displayMap = new Dictionary<string, (string Kind, string Value)>(StringComparer.Ordinal);

        void AddChoice(string display, string kind, string value)
        {
            if (displayMap.ContainsKey(display))
            {
                return;
            }

            displayMap[display] = (kind, value);
            choices.Add(display);
        }

        // Seçili bölümlerin tamamı paralel taranır (en fazla 12); duvar süresi ~tek tarama kadardır.
        var episodesToProbe = details.Episodes
                                     .Where(e => (e.Season ?? 1) == season && episodeNumbers.Contains(e.Number))
                                     .OrderBy(e => e.Number)
                                     .Take(12)
                                     .ToList();
        var probedAll   = episodesToProbe.Count == episodeNumbers.Count;
        var probeSuffix = probedAll ? "bölüm" : "örnek";

        var collectGate    = new Lock();
        var hosterEpisodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var qualitySet     = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var formatSet      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var probed         = 0;

        await AnsiConsole.Status()
                         .Spinner(Spinner.Known.Dots)
                         .StartAsync("Kaynaklar taranıyor...",
                                     async ctx =>
                                     {
                                         using var probeGate = new SemaphoreSlim(3, 3);
                                         var       doneCount = 0;

                                         async Task ProbeOneAsync(Episode ep)
                                         {
                                             await probeGate.WaitAsync().ConfigureAwait(false);
                                             try
                                             {
                                                 try
                                                 {
                                                     var gr = await _apiClient.GetEpisodeGroupsAsync(_provider!, ep.Id);
                                                     if (gr.IsSuccess)
                                                     {
                                                         lock (collectGate)
                                                         {
                                                             foreach (var g in gr.Data
                                                                          .Where(g => !string.IsNullOrWhiteSpace(g))
                                                                          .Distinct(StringComparer.OrdinalIgnoreCase))
                                                             {
                                                                 AddChoice($"Grup: {Markup.Escape(g)}", "Grup", g);
                                                             }
                                                         }
                                                     }
                                                 }
                                                 catch
                                                 {
                                                 }

                                                 // İzleme akışıyla aynı streaming endpoint: API buldukça
                                                 // kaynakları piste sürüyor, tek atımlık istek gibi 5sn'de ölmüyor.
                                                 var streamed = await StreamProbeAsync(ep.Id, TimeSpan.FromSeconds(12));
                                                 if (streamed.Count == 0)
                                                 {
                                                     return;
                                                 }

                                                 lock (collectGate)
                                                 {
                                                     probed++;
                                                     foreach (var h in streamed
                                                                       .Select(s => s.Hoster)
                                                                       .Where(h => !string.IsNullOrWhiteSpace(h))
                                                                       .Distinct(StringComparer.OrdinalIgnoreCase))
                                                     {
                                                         hosterEpisodes[h!] =
                                                             hosterEpisodes.TryGetValue(h!, out var c) ? c + 1 : 1;
                                                     }

                                                     foreach (var q in streamed
                                                                       .Select(s => s.Quality)
                                                                       .Where(q => !string.IsNullOrWhiteSpace(q))
                                                                       .Distinct(StringComparer.OrdinalIgnoreCase))
                                                     {
                                                         qualitySet.Add(q!);
                                                     }

                                                     foreach (var f in streamed
                                                                       .Select(s => s.Type.ToString())
                                                                       .Distinct(StringComparer.OrdinalIgnoreCase))
                                                     {
                                                         formatSet.Add(f);
                                                     }
                                                 }
                                             }
                                             finally
                                             {
                                                 probeGate.Release();
                                                 var done = Interlocked.Increment(ref doneCount);
                                                 ctx.Status($"Kaynaklar taranıyor ({done}/{episodesToProbe.Count})...");
                                             }
                                         }

                                         await Task.WhenAll(episodesToProbe.Select(ProbeOneAsync))
                                                   .ConfigureAwait(false);
                                     });

        if (probed > 0)
        {
            foreach (var h in hosterEpisodes.Keys
                                            .OrderBy(h => RankIn(h, config.PreferredHosterOrder))
                                            .ThenBy(h => h, StringComparer.OrdinalIgnoreCase))
            {
                AddChoice($"Hoster: {Markup.Escape(h)} ({hosterEpisodes[h]}/{probed} {probeSuffix})", "Hoster", h);
            }

            foreach (var q in qualitySet.OrderBy(q => RankIn(q, config.PreferredQualityOrder))
                                        .ThenBy(q => q, StringComparer.OrdinalIgnoreCase))
            {
                AddChoice($"Kalite: {Markup.Escape(q)}", "Kalite", q);
            }

            foreach (var f in formatSet.OrderBy(f => RankIn(f, config.PreferredFormatOrder))
                                       .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                AddChoice($"Format: {Markup.Escape(f)}", "Format", f);
            }
        }
        else
        {
            foreach (var h in config.PreferredHosterOrder.Take(20))
            {
                AddChoice($"Hoster: {Markup.Escape(h)}", "Hoster", h);
            }

            foreach (var q in config.PreferredQualityOrder.Take(8))
            {
                AddChoice($"Kalite: {Markup.Escape(q)}", "Kalite", q);
            }

            AddChoice("Format: Mp4", "Format", "Mp4");
            AddChoice("Format: M3U8", "Format", "M3U8");
        }

        List<string> selected;
        try
        {
            selected = AnsiConsole.Prompt(new MultiSelectionPrompt<string>()
                                          .Title(
                                              "Toplu indirme filtresi [grey](Space seç • Enter başlat • Esc iptal)[/]")
                                          .Required(false)
                                          .PageSize(14)
                                          .AddChoices(choices));
        }
        catch
        {
            return null;
        }

        if (selected.Count == 0 || selected.Contains("★ TAM OTOMATİK (filtresiz)"))
        {
            return new BatchFilter([], [], [], []);
        }

        var groups    = new List<string>();
        var hosters   = new List<string>();
        var qualities = new List<string>();
        var formats   = new List<string>();
        foreach (var s in selected)
        {
            if (!displayMap.TryGetValue(s, out var mapped))
            {
                continue;
            }

            switch (mapped.Kind)
            {
                case "Grup":
                    groups.Add(mapped.Value);
                    break;
                case "Hoster":
                    hosters.Add(mapped.Value);
                    break;
                case "Kalite":
                    qualities.Add(mapped.Value);
                    break;
                case "Format":
                    formats.Add(mapped.Value);
                    break;
            }
        }

        return new BatchFilter(groups, hosters, qualities, formats);
    }

    private static int RankIn(string? value, List<string> orderList)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return int.MaxValue;
        }

        for (var i = 0; i < orderList.Count; i++)
        {
            if (value.Contains(orderList[i], StringComparison.OrdinalIgnoreCase)
                || orderList[i].Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private sealed record BatchFilter(
        List<string> Groups,
        List<string> Hosters,
        List<string> Qualities,
        List<string> Formats);
}
