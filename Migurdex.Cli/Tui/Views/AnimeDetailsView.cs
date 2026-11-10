using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
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
                    Display       = "[cyan]⤓ Toplu indir (sezon / aralık)...[/]",
                    DisplayActive = "[bold white on grey23] ⤓ Toplu indir (sezon / aralık)... [/]",
                    Searchable    = "Toplu İndir",
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

        if (choice.Searchable == "Toplu İndir" && currentSeasonGroup != null)
        {
            ShowBatchDownloadMenu(navigator,
                                  details,
                                  currentSeasonGroup.OrderBy(e => e.Number).ToList(),
                                  activeSeason);
            return;
        }

        if (episodeMap.TryGetValue(choice.Searchable, out var selectedEp))
        {
            var queueService = _serviceProvider.GetService(typeof(IDownloadQueueService)) as IDownloadQueueService;

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

            var configService = _serviceProvider.GetService(typeof(IConfigurationService)) as IConfigurationService;
            var actionSetting = configService?.Config.DefaultEpisodeAction?.ToLowerInvariant() ?? "ask";

            if (actionSetting == "play")
            {
                var playView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
                playView.SetTarget(_provider,
                                   _animeId,
                                   details.Title,
                                   selectedEp,
                                   details.Episodes,
                                   details.PosterUrl,
                                   mode: SourceViewMode.Play);
                navigator.Push(playView);
                return;
            }

            if (actionSetting is "auto_download" or "download")
            {
                if (queueService is not null)
                {
                    EnqueueSingleEpisode(queueService, details, selectedEp);
                    return;
                }
            }

            if (actionSetting is "select_download" or "selectsource")
            {
                var selectView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
                selectView.SetTarget(_provider,
                                     _animeId,
                                     details.Title,
                                     selectedEp,
                                     details.Episodes,
                                     details.PosterUrl,
                                     mode: SourceViewMode.Download);
                navigator.Push(selectView);
                return;
            }

            var actions = new List<FuzzyChoice>
            {
                Theme.PlayChoice("Oynat")
            };

            if (queueService is not null)
            {
                actions.Add(Theme.ActionChoice("İndir (Kuyruğa Ekle)", Theme.Primary));
                actions.Add(Theme.ActionChoice("İndir (Kaynak Seç)", Theme.Text));
            }
            else
            {
                actions.Add(Theme.ActionChoice("İndir", Theme.Primary));
            }

            actions.Add(TuiHelpers.Back());

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

            if (action.Searchable == "İndir (Kuyruğa Ekle)" && queueService is not null)
            {
                EnqueueSingleEpisode(queueService, details, selectedEp);
                return;
            }

            var mode = action.Searchable.StartsWith("İndir", StringComparison.Ordinal)
                           ? SourceViewMode.Download
                           : SourceViewMode.Play;
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

    private void EnqueueSingleEpisode(IDownloadQueueService queueService, AnimeDetails details, Episode episode)
    {
        queueService.Enqueue(new DownloadJobRequest
                             {
                                 ProviderName  = _provider!,
                                 AnimeId       = _animeId!,
                                 AnimeTitle    = details.Title,
                                 EpisodeId     = episode.Id,
                                 EpisodeTitle  = episode.Title,
                                 Season        = episode.Season ?? 1,
                                 EpisodeNumber = episode.Number
                             },
                             out var alreadyExisted);

        var epRef = TuiHelpers.FormatEpisodeRef(episode);
        if (alreadyExisted)
        {
            Toast.Show($"[yellow]! {Markup.Escape(epRef)} zaten indirme kuyruğunda.[/]", 2000);
        }
        else
        {
            Toast.Show($"[green]✓[/] {Markup.Escape(epRef)} indirme kuyruğuna eklendi.", 2000);
        }
    }

    private void ShowBatchDownloadMenu(
        ITuiNavigator          navigator,
        AnimeDetails           details,
        IReadOnlyList<Episode> seasonEpisodes,
        int                    activeSeason)
    {
        var queueService = _serviceProvider.GetService(typeof(IDownloadQueueService)) as IDownloadQueueService;
        if (queueService is null)
        {
            Toast.Show("[red]İndirme kuyruğu servisi kullanılamıyor.[/]");
            return;
        }

        var watchedEpisodeIds = _historyService.GetWatchHistory()
                                               .Where(h => h.ProviderName == _provider
                                                           && h.AnimeId == _animeId
                                                           && h.IsCompleted)
                                               .Select(h => h.EpisodeId)
                                               .ToHashSet(StringComparer.Ordinal);
        var unwatchedEpisodes = seasonEpisodes.Where(e => !watchedEpisodeIds.Contains(e.Id)).ToList();

        var batchChoices = new List<FuzzyChoice>
        {
            Theme.ActionChoice($"Tüm Sezonu Kuyruğa Ekle ({seasonEpisodes.Count} bölüm)", Theme.Primary),
            Theme.ActionChoice("Bölüm Aralığı Seç (örn. 1-12 veya 1,3,5)", Theme.Text)
        };

        if (unwatchedEpisodes.Count > 0 && unwatchedEpisodes.Count < seasonEpisodes.Count)
        {
            batchChoices.Add(Theme.ActionChoice($"İzlenmeyenleri Kuyruğa Ekle ({unwatchedEpisodes.Count} bölüm)",
                                                Theme.Warning));
        }

        batchChoices.Add(Theme.ActionChoice("İndirmeler Ekranını Aç", Theme.Success));
        batchChoices.Add(TuiHelpers.Back());

        var selected = FuzzyPrompt.Show($"{details.Title} • {activeSeason}. Sezon",
                                        batchChoices,
                                        searchable: false,
                                        headerLines:
                                        [
                                            $"[grey]Sezon {activeSeason} • Toplam {seasonEpisodes.Count} bölüm[/]"
                                        ],
                                        footerHelp: "↑↓ gez • Enter seç • Esc geri");

        if (selected is null || selected.Searchable == "Geri")
        {
            return;
        }

        if (selected.Searchable == "İndirmeler Ekranını Aç")
        {
            var downloadsView = _serviceProvider.GetService(typeof(DownloadsView)) as DownloadsView;
            if (downloadsView is not null)
            {
                navigator.Push(downloadsView);
            }

            return;
        }

        IReadOnlyList<Episode> targetEpisodes;
        if (selected.Searchable.StartsWith("Tüm Sezonu", StringComparison.Ordinal))
        {
            targetEpisodes = seasonEpisodes;
        }
        else if (selected.Searchable.StartsWith("İzlenmeyenleri", StringComparison.Ordinal))
        {
            targetEpisodes = unwatchedEpisodes;
        }
        else
        {
            var minEp = seasonEpisodes.Count > 0 ? seasonEpisodes[0].Number : 1;
            var maxEp = seasonEpisodes.Count > 0 ? seasonEpisodes[^1].Number : 1;
            var input = Theme.Ask($"Bölüm aralığı ({minEp}-{maxEp}):", $"{minEp}-{maxEp}");
            if (!MediaSelection.TryParseEpisodeSelector(input, out var selector, out var parseError)
                || selector is null)
            {
                Toast.Show($"[red]{Markup.Escape(parseError ?? "Geçersiz bölüm aralığı.")}[/]", 2200);
                return;
            }

            targetEpisodes = seasonEpisodes.Where(e => selector.Matches(e.Number)).ToList();
            if (targetEpisodes.Count == 0)
            {
                Toast.Show("[yellow]Seçilen aralıkta bölüm bulunamadı.[/]", 2000);
                return;
            }
        }

        var requests = targetEpisodes.Select(ep => new DownloadJobRequest
        {
            ProviderName  = _provider!,
            AnimeId       = _animeId!,
            AnimeTitle    = details.Title,
            EpisodeId     = ep.Id,
            EpisodeTitle  = ep.Title,
            Season        = ep.Season ?? activeSeason,
            EpisodeNumber = ep.Number
        });

        var added = queueService.EnqueueBatch(requests, out var skipped);
        if (added.Count > 0)
        {
            var suffix = skipped > 0 ? $" ({skipped} bölüm zaten kuyruktaydı)" : string.Empty;
            Toast.Show($"[green]✓[/] {added.Count} bölüm indirme kuyruğuna eklendi{Markup.Escape(suffix)}.", 2500);
        }
        else
        {
            Toast.Show("[yellow]Seçilen tüm bölümler zaten indirme kuyruğunda.[/]", 2200);
        }
    }
}
