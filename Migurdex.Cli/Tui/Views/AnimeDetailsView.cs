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
    private          AnimeDetails?     _cachedDetails;
    private          string?           _initialPosterUrl;
    private          string?           _lastSelectedSearchable;
    private          string?           _provider;

    public AnimeDetailsView(IApiClientService apiClient,
        IHistoryService                       historyService,
        IServiceProvider                      serviceProvider)
    {
        _apiClient       = apiClient;
        _historyService  = historyService;
        _serviceProvider = serviceProvider;
    }

    public void SetTarget(string provider, string animeId, string? initialPosterUrl = null)
    {
        if (_provider != provider || _animeId != animeId)
        {
            _cachedDetails = null;
        }

        if (_provider != provider || _animeId != animeId)
        {
            _lastSelectedSearchable = null;
        }

        _provider         = provider;
        _animeId          = animeId;
        _initialPosterUrl = initialPosterUrl;
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

        var choices = new List<FuzzyChoice>
        {
            new()
            {
                Display       = isFav ? "[pink1]Favorilerden Çıkar[/]" : "[pink1]Favorilere Ekle[/]",
                DisplayActive = isFav ? "[bold pink1]Favorilerden Çıkar[/]" : "[bold pink1]Favorilere Ekle[/]",
                Searchable    = isFav ? "Favorilerden Çıkar" : "Favorilere Ekle"
            }
        };

        var episodeMap      = new Dictionary<string, Episode>();
        var groupedEpisodes = details.Episodes.GroupBy(e => e.Season ?? 1).OrderBy(g => g.Key);

        foreach (var group in groupedEpisodes)
        {
            var prefix = $"Sezon {group.Key}: ";
            foreach (var ep in group.OrderBy(e => e.Number))
            {
                var label = $"{prefix}Bölüm {ep.Number} - {ep.Title}";
                choices.Add(new FuzzyChoice
                {
                    Display = $"[silver]Sezon {group.Key} Bölüm {ep.Number}[/] - [grey]{Markup.Escape(ep.Title)}[/]",
                    DisplayActive =
                        $"[bold gold1]Sezon {group.Key} Bölüm {ep.Number}[/] - [bold white]{Markup.Escape(ep.Title)}[/]",
                    Searchable = label
                });
                episodeMap[label] = ep;
            }
        }

        choices.Add(new FuzzyChoice
        {
            Display       = "[red]Geri[/]",
            DisplayActive = "[bold red]Geri[/]",
            Searchable    = "Geri"
        });

        var choice = FuzzyPrompt.Show("İşlem:", choices, initialSelection: _lastSelectedSearchable);

        if (choice == null || choice.Searchable == "Geri")
        {
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

        var selectedEp  = episodeMap[choice.Searchable];
        var sourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
        sourcesView.SetTarget(_provider, _animeId, details.Title, selectedEp, details.Episodes, details.PosterUrl);
        navigator.Push(sourcesView);
    }
}
