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

    public override void Render(ITuiNavigator navigator)
    {
        if (string.IsNullOrEmpty(_provider) || string.IsNullOrEmpty(_animeId))
        {
            AnsiConsole.MarkupLine("[red]Hata: Sağlayıcı veya anime belirtilmemiş.[/]");
            Console.ReadKey(true);
            navigator.Pop();
            return;
        }

        var     details   = _cachedDetails;
        string? loadError = null;
        if (details == null)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[grey]~~[/] [bold cyan]{Markup.Escape(_animeTitle ?? "Detaylar")}[/] [grey]~~[/]");
            AnsiConsole.MarkupLine($"[grey]Sağlayıcı:[/] [bold mediumpurple1]{Markup.Escape(_provider)}[/]");
            AnsiConsole.WriteLine();

            AnsiConsole.Status()
                       .Spinner(Spinner.Known.Dots)
                       .Start("Yükleniyor...",
                              ctx =>
                              {
                                  try
                                  {
                                      var result = _apiClient.GetAnimeDetailsAsync(_provider, _animeId)
                                                             .GetAwaiter()
                                                             .GetResult();

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

                Console.ReadKey(true);
                navigator.Pop();
                return;
            }

            if (string.IsNullOrWhiteSpace(details.PosterUrl) && !string.IsNullOrWhiteSpace(_initialPosterUrl))
            {
                details.PosterUrl = _initialPosterUrl;
            }

            details.Normalize();

            _cachedDetails = details;
        }

        var isFav = _historyService.IsFavorite(_animeId, _provider);

        var metaParts = new List<string>
        {
            $"[grey]Sağlayıcı:[/] [bold mediumpurple1]{Markup.Escape(_provider)}[/]",
            $"[grey]Format:[/] [bold green]{details.Format}[/]"
        };

        if (isFav)
        {
            metaParts.Add("[bold pink1]♥ Favorilerde[/]");
        }

        var headerLines = new List<string>
        {
            string.Join("  [grey]•[/]  ", metaParts)
        };

        if (!string.IsNullOrWhiteSpace(details.Summary) && details.Summary != "-")
        {
            headerLines.Add($"[grey]Açıklama:[/] [silver]{Markup.Escape(TruncateSummary(details.Summary, 120))}[/]");
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
                    Display       = isFav ? "[pink1]Favorilerden Çıkar[/]" : "[pink1]Favorilere Ekle[/]",
                    DisplayActive = isFav ? "[bold pink1]Favorilerden Çıkar[/]" : "[bold pink1]Favorilere Ekle[/]",
                    Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle"
                }
            };

            foreach (var group in groupedEpisodes)
            {
                var seasonNum = group.Key;
                var epCount   = group.Count();
                var label     = $"{seasonNum}. Sezon";
                seasonChoices.Add(new FuzzyChoice
                {
                    Display       = $"[silver]{seasonNum}. Sezon[/] [grey]({epCount} Bölüm)[/]",
                    DisplayActive = $"[bold gold1]{seasonNum}. Sezon[/] [bold white]({epCount} Bölüm)[/]",
                    Searchable    = label
                });
            }

            seasonChoices.Add(new FuzzyChoice
            {
                Display       = "[red]Geri[/]",
                DisplayActive = "[bold red]Geri[/]",
                Searchable    = "Geri"
            });

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
            headerLines.Add($"[grey]Sezon:[/] [bold gold1]{activeSeason}. Sezon[/]");
        }

        var choices = new List<FuzzyChoice>();

        var isSingleContent = details.Episodes.Count == 1;
        var episodeMap      = new Dictionary<string, Episode>();

        if (isSingleContent)
        {
            var singleEp = details.Episodes[0];
            var isMovie = details.Format == ContentFormat.Movie
                          || AnimeDetails.IsMovieTitle(details.Title)
                          || AnimeDetails.IsMovieSummary(details.Summary);

            var playSearchable = isMovie ? "Filmi Oynat" : "Bölümü Oynat";
            var playDisplay    = isMovie ? "[bold chartreuse2]▶ Filmi Oynat[/]" : "[bold chartreuse2]▶ Bölümü Oynat[/]";
            var playActiveDisplay =
                isMovie
                    ? "[bold white on darkgreen] ▶ Filmi Oynat [/]"
                    : "[bold white on darkgreen] ▶ Bölümü Oynat [/]";

            choices.Add(new FuzzyChoice
            {
                Display       = playDisplay,
                DisplayActive = playActiveDisplay,
                Searchable    = playSearchable
            });

            episodeMap[playSearchable] = singleEp;

            choices.Add(new FuzzyChoice
            {
                Display       = isFav ? "[pink1]Favorilerden Çıkar[/]" : "[pink1]Favorilere Ekle[/]",
                DisplayActive = isFav ? "[bold pink1]Favorilerden Çıkar[/]" : "[bold pink1]Favorilere Ekle[/]",
                Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle"
            });
        }
        else
        {
            if (!isMultiSeason)
            {
                choices.Add(new FuzzyChoice
                {
                    Display       = isFav ? "[pink1]Favorilerden Çıkar[/]" : "[pink1]Favorilere Ekle[/]",
                    DisplayActive = isFav ? "[bold pink1]Favorilerden Çıkar[/]" : "[bold pink1]Favorilere Ekle[/]",
                    Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle"
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

                    var display = hasCustomTitle
                                      ? $"[silver]{ep.Number}. {unitPrefix}[/] [grey]│[/] [grey]{Markup.Escape(ep.Title!)}[/]"
                                      : $"[silver]{ep.Number}. {unitPrefix}[/]";

                    var displayActive = hasCustomTitle
                                            ? $"[bold gold1]{ep.Number}. {unitPrefix}[/] [grey]│[/] [bold white]{Markup.Escape(ep.Title!)}[/]"
                                            : $"[bold gold1]{ep.Number}. {unitPrefix}[/]";

                    var searchable = hasCustomTitle
                                         ? $"{ep.Number}. {unitPrefix} {ep.Title}"
                                         : $"{ep.Number}. {unitPrefix}";

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

        choices.Add(new FuzzyChoice
        {
            Display       = "[red]Geri[/]",
            DisplayActive = "[bold red]Geri[/]",
            Searchable    = "Geri"
        });

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
            var sourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
            sourcesView.SetTarget(_provider, _animeId, details.Title, selectedEp, details.Episodes, details.PosterUrl);
            navigator.Push(sourcesView);
        }
    }

    private static string TruncateSummary(string? summary, int maxLength = 100)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "-";
        }

        var clean = summary.Replace("\r", "").Replace("\n", " ").Trim();
        return clean.Length <= maxLength ? clean : clean[..(maxLength - 3)] + "...";
    }
}
