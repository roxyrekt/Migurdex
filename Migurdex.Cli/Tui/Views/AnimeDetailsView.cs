using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
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
    private          int?              _selectedSeason;
    private          string?           _provider;

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
            AnsiConsole.MarkupLine($"[grey]~~[/] [yellow]Detaylar ({Markup.Escape(_provider)})...[/] [grey]~~[/]");
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

            _cachedDetails = details;
        }

        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[grey]~~[/] [bold cyan]{Markup.Escape(details.Title)}[/] [grey]~~[/]");
        AnsiConsole.WriteLine();

        var isFav = _historyService.IsFavorite(_animeId, _provider);

        var grid = new Grid();
        grid.AddColumn(new GridColumn().Width(20));
        grid.AddColumn(new GridColumn());

        grid.AddRow("[bold grey]Sağlayıcı:[/]", $"[purple]{Markup.Escape(_provider)}[/]");
        grid.AddRow("[bold grey]Format:[/]", $"[green]{details.Format}[/]");
        grid.AddRow("[bold grey]Açıklama:[/]", $"[white]{Markup.Escape(details.Summary ?? "-")}[/]");

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();

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

            var seasonChoice = FuzzyPrompt.Show("Sezon Seçin:",
                                                seasonChoices,
                                                initialSelection: _lastSelectedSeasonSearchable);

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

        var choices = new List<FuzzyChoice>();

        if (!isMultiSeason)
        {
            choices.Add(new FuzzyChoice
            {
                Display       = isFav ? "[pink1]Favorilerden Çıkar[/]" : "[pink1]Favorilere Ekle[/]",
                DisplayActive = isFav ? "[bold pink1]Favorilerden Çıkar[/]" : "[bold pink1]Favorilere Ekle[/]",
                Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle"
            });
        }

        var episodeMap = new Dictionary<string, Episode>();
        if (currentSeasonGroup != null)
        {
            foreach (var ep in currentSeasonGroup.OrderBy(e => e.Number))
            {
                var hasCustomTitle = !string.IsNullOrWhiteSpace(ep.Title)
                                     && !ep.Title.Trim()
                                           .Equals($"Bölüm {ep.Number}", StringComparison.OrdinalIgnoreCase)
                                     && !ep.Title.Trim().Equals($"{ep.Number}", StringComparison.OrdinalIgnoreCase);

                var display = hasCustomTitle
                                  ? $"[silver]{ep.Number}. Bölüm[/] [grey]│[/] [grey]{Markup.Escape(ep.Title!)}[/]"
                                  : $"[silver]{ep.Number}. Bölüm[/]";

                var displayActive = hasCustomTitle
                                        ? $"[bold gold1]{ep.Number}. Bölüm[/] [grey]│[/] [bold white]{Markup.Escape(ep.Title!)}[/]"
                                        : $"[bold gold1]{ep.Number}. Bölüm[/]";

                var searchable = hasCustomTitle
                                     ? $"{ep.Number}. Bölüm {ep.Title}"
                                     : $"{ep.Number}. Bölüm";

                choices.Add(new FuzzyChoice
                {
                    Display       = display,
                    DisplayActive = displayActive,
                    Searchable    = searchable
                });

                episodeMap[searchable] = ep;
            }
        }

        choices.Add(new FuzzyChoice
        {
            Display       = "[red]Geri[/]",
            DisplayActive = "[bold red]Geri[/]",
            Searchable    = "Geri"
        });

        var promptTitle = isMultiSeason ? $"{activeSeason}. Sezon Bölümleri:" : "Bölümler:";
        var choice      = FuzzyPrompt.Show(promptTitle, choices, initialSelection: _lastSelectedSearchable);

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
}
