using Migurdex.Cli.Services;
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
                        Searchable    = searchable,
                        CanBeChecked  = true
                    });

                    episodeMap[searchable] = ep;
                }
            }
        }

        choices.Add(TuiHelpers.Back());

        // Coklu bolum varsa Space ile isaretleme ekrani acilir; tek bolumlu icerik
        // (film) eskiden oldugu gibi tekli secim ekraniyla devam eder.
        if (!isSingleContent)
        {
            var selectedEpisodes = await SelectEpisodesForBulkDownloadAsync(details,
                                                                            headerLines,
                                                                            currentSeasonGroup);

            if (selectedEpisodes is { Count: > 0 })
            {
                await StartBulkDownloadFlowAsync(navigator, details, selectedEpisodes);
                return;
            }

            if (isMultiSeason)
            {
                _selectedSeason         = null;
                _lastSelectedSearchable = null;
                return;
            }

            navigator.Pop();
            return;
        }

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

        if (episodeMap.TryGetValue(choice.Searchable, out var selectedEp))
        {
            if (isSingleContent)
            {
                var singleMode = choice.Searchable.Contains("İndir", StringComparison.OrdinalIgnoreCase)
                                     ? SourceViewMode.Download
                                     : SourceViewMode.Play;
                var singleSourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
                singleSourcesView.SetTarget(_provider, _animeId, details.Title, selectedEp, details.Episodes, details.PosterUrl, mode: singleMode);
                navigator.Push(singleSourcesView);
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
                                          [$"[grey]{Markup.Escape(TuiHelpers.EllipsizedTitle(details.Title))} › {Markup.Escape(TuiHelpers.FormatEpisodeRef(selectedEp))}[/]"],
                                          footerHelp: "↑↓ gez • Enter seç • Esc geri");
            if (action is null || action.Searchable == "Geri")
            {
                return;
            }

            var mode = action.Searchable == "İndir" ? SourceViewMode.Download : SourceViewMode.Play;
            var sourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
            sourcesView.SetTarget(_provider, _animeId, details.Title, selectedEp, details.Episodes, details.PosterUrl, mode: mode);
            navigator.Push(sourcesView);
        }
    }

    private static Task<List<Episode>?> SelectEpisodesForBulkDownloadAsync(AnimeDetails        details,
        IReadOnlyList<string>     headerLines,
        IGrouping<int, Episode>? currentSeasonGroup)
    {
        var seasonEpisodes = currentSeasonGroup?.OrderBy(e => e.Number).ToList() ?? [];
        if (seasonEpisodes.Count == 0)
        {
            return Task.FromResult<List<Episode>?>(null);
        }

        const string selectAllSearchable = "Tüm Bölümleri Ä°ÅŸaretle";

        var bulkChoices = new List<FuzzyChoice>
        {
            new()
            {
                Display       = "[green]âŒ Tüm bölümleri iÅŸaretle[/]",
                DisplayActive = "[bold white on green] âŒ Tüm bölümleri iÅŸaretle [/]",
                Searchable    = selectAllSearchable,
                IsAction      = true
            }
        };

        var choiceToEpisode = new Dictionary<string, Episode>();
        var isMovie          = details.Format == ContentFormat.Movie;
        var unitPrefix       = isMovie ? "Film" : "Bölüm";

        foreach (var ep in seasonEpisodes)
        {
            var searchable    = $"ep{ep.Number:0000}";
            var titleTrimmed  = ep.Title?.Trim() ?? "";
            var hasCustomName = !string.IsNullOrWhiteSpace(titleTrimmed)
                                && !titleTrimmed.Equals($"{ep.Number}", StringComparison.OrdinalIgnoreCase)
                                && !titleTrimmed.Equals($"Bölüm {ep.Number}", StringComparison.OrdinalIgnoreCase);

            var label = hasCustomName
                            ? $"{unitPrefix} {ep.Number:00} · {Theme.Ellipsize(ep.Title!, 44)}"
                            : $"{unitPrefix} {ep.Number:00}";

            bulkChoices.Add(new FuzzyChoice
            {
                Display       = $"[grey]{Markup.Escape(label)}[/]",
                DisplayActive = $"[bold white]{Markup.Escape(label)}[/]",
                Searchable    = searchable,
                CanBeChecked  = true
            });

            choiceToEpisode[searchable] = ep;
        }

        bulkChoices.Add(TuiHelpers.Back());

        var selected = FuzzyPrompt.ShowMulti(details.Title,
                                            bulkChoices,
                                            initialSelection: selectAllSearchable,
                                            headerLines: headerLines,
                                            footerHelp: "â†‘â†“ gez â€¢ Space iÅŸaretle â€¢ Enter onayla â€¢ "
                                                        + "Ctrl+A tümünü iÅŸaretle â€¢ Esc geri");

        if (selected == null || selected.Count == 0)
        {
            return Task.FromResult<List<Episode>?>(null);
        }

        var selectAllChosen = selected.Count == 1 && selected[0].Searchable == selectAllSearchable;
        if (selectAllChosen)
        {
            return Task.FromResult<List<Episode>?>(seasonEpisodes);
        }

        var chosen = selected.Where(c => choiceToEpisode.ContainsKey(c.Searchable))
                             .Select(c => choiceToEpisode[c.Searchable])
                             .ToList();

        return Task.FromResult(chosen.Count > 0 ? chosen : null);
    }

    /// <summary>
    /// Seçilen bölümler için toplu indirme akışını başlatır.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⭐ <b>Menü yok.</b> Kullanıcı isteği: bölüm listesinde
    /// <c>Space</c> ile bölümleri işaretledikten veya
    /// <c>⌁ Tüm bölümleri işaretle</c> kısayolunu kullandıktan sonra
    /// <b>Enter'a basınca indirme doğrudan başlar</b>. Önceki sürümde
    /// "Tüm Bölümleri İndir / Sadece Seçtiğin Bölümleri İndir" ikili bir menü
    /// vardı; bu menü hem kullanıcı isteği dışıydı hem de
    /// <c>FuzzyPrompt.Show</c> çağrısını bir canlı ekranın içine düşürdüğü
    /// için <c>InvalidOperationException</c> doğuruyordu.
    /// </para>
    /// <para>
    /// <b>Neden "tümünü indir" ayrı bir seçenek değil?</b> Listenin başındaki
    /// <c>⌁ Tüm bölümleri işaretle</c> kısayolu zaten tüm bölümleri işaretler.
    /// Kullanıcı o kısayola basıp Enter'a bastığında sezonun tamamı indirilir;
    /// ayrı bir menü satırına gerek yoktur.
    /// </para>
    /// <para>
    /// <b>Onay ekranı:</b> yalnızca bölüm sayısı
    /// <see cref="BulkDownloadWarningThreshold"/> üzerindeyse çıkar. Küçük
    /// dizilerde ekran hiç gösterilmez — 12 bölümlük bir dizide uyarı
    /// göstermek gürültüdür.
    /// </para>
    /// </remarks>
    private async Task StartBulkDownloadFlowAsync(ITuiNavigator  navigator,
        AnimeDetails                     details,
        List<Episode>                    selectedEpisodes)
    {
        if (selectedEpisodes.Count > BulkDownloadWarningThreshold
            && !await ConfirmBulkDownloadAsync(details.Title, selectedEpisodes))
        {
            return;
        }

        var bulkView = (BulkDownloadView) _serviceProvider.GetService(typeof(BulkDownloadView))!;
        bulkView.SetTarget(_provider!, _animeId!, details.Title, selectedEpisodes);
        navigator.Push(bulkView);
    }

    private static async Task<bool> ConfirmBulkDownloadAsync(string            animeTitle,
        IReadOnlyList<Episode> episodes)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(TuiHelpers.EllipsizedTitle(animeTitle))}[/]");
        AnsiConsole.MarkupLine($"[yellow]{episodes.Count} bölüm[/] sıralı olarak indirilecek.");
        AnsiConsole.MarkupLine("[grey]Bölümler tek tek, sırayla iner. Bu birkaç saat sürebilir "
                               + "ve diskte onlarca GB yer kaplayabilir.[/]");
        AnsiConsole.WriteLine();

        var confirm = new List<FuzzyChoice>
        {
            Theme.ActionChoice("Evet, indirmeye başla", Theme.Primary),
            Theme.ActionChoice("Vazgeç", Theme.Danger),
            TuiHelpers.Back()
        };

        var choice = FuzzyPrompt.Show("Onayla",
                                      confirm,
                                      searchable: false,
                                      footerHelp: "↑↓ gez • Enter seç • Esc geri");

        return choice is not null
               && choice.Searchable != "Geri"
               && choice.Searchable == "Evet, indirmeye başla";
    }

    /// <summary>
    /// Bu sayının üzerindeki bölüm sayılarında toplu indirmeden önce ayrıca onay
    /// istenir. Sıralı indirme uzun sürdüğü ve diskte çok yer kapladığı için
    /// kullanıcı bilinçli karar vermelidir.
    /// </summary>
    /// <remarks>
    /// Küçük dizilerde (<c>12</c> bölüm gibi) onay ekranı <b>hiç gösterilmez</b>;
    /// uyarı göstermek gürültü olurdu. 1000+ bölümlü dizilerde hem ekranın hem
    /// diskin etkisi büyüktür.
    /// </remarks>
    internal const int BulkDownloadWarningThreshold = 100;
}
