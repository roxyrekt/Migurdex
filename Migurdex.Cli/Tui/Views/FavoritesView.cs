using Migurdex.Cli.Services;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class FavoritesView : BaseView
{
    private readonly IConfigurationService _configService;
    private readonly IHistoryService       _historyService;
    private readonly IServiceProvider      _serviceProvider;
    private          string?               _lastSelectedSearchable;

    public FavoritesView(
        IHistoryService       historyService,
        IConfigurationService configService,
        IServiceProvider      serviceProvider)
    {
        _historyService  = historyService;
        _configService   = configService;
        _serviceProvider = serviceProvider;
    }

    public override void Render(ITuiNavigator navigator)
    {
        var viewRunning = true;
        while (viewRunning)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[grey]~~[/] [pink1]Favoriler[/] [grey]~~[/]");
            AnsiConsole.WriteLine();

            var favorites = _historyService.GetFavorites();

            if (favorites.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Henüz favori yok.[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Geri dönmek için bir tuşa basın...[/]");
                Console.ReadKey(true);
                navigator.Pop();
                return;
            }

            var choices = favorites.Select((f, idx) =>
                                   {
                                       var isDisabled =
                                           _configService.Config.DisabledProviders.Contains(
                                               f.ProviderName,
                                               StringComparer.OrdinalIgnoreCase);
                                       var providerSuffix = isDisabled ? " [red][[!]][/]" : "";

                                       return new FuzzyChoice
                                       {
                                           Display =
                                               $"[grey]#{idx + 1}[/] [silver]{Markup.Escape(f.AnimeTitle)} ({Markup.Escape(f.ProviderName)}){providerSuffix}[/]",
                                           DisplayActive =
                                               $"[bold pink1]#{idx + 1}[/] [bold white]{Markup.Escape(f.AnimeTitle)}[/] [bold mediumpurple1]({Markup.Escape(f.ProviderName)})[/]{providerSuffix}",
                                           Searchable = $"#{idx + 1} - {f.AnimeTitle} ({f.ProviderName})"
                                       };
                                   })
                                   .ToList();

            choices.Add(new FuzzyChoice
            {
                Display       = "[red]Tümünü Temizle[/]",
                DisplayActive = "[bold red reverse]Tümünü Temizle[/]",
                Searchable    = "Tümünü Temizle"
            });

            choices.Add(new FuzzyChoice
            {
                Display       = "[red]Geri[/]",
                DisplayActive = "[bold red]Geri[/]",
                Searchable    = "Geri"
            });

            var choice = FuzzyPrompt.Show("Anime seçin:", choices, initialSelection: _lastSelectedSearchable);

            if (choice == null || choice.Searchable == "Geri")
            {
                navigator.Pop();
                return;
            }

            _lastSelectedSearchable = choice.Searchable;

            if (choice.Searchable == "Tümünü Temizle")
            {
                if (AnsiConsole.Confirm("[bold red]Hepsi silinsin mi?[/]"))
                {
                    _historyService.ClearFavorites();
                    Toast.Show("[green]Temizlendi.[/]");
                }

                continue;
            }

            var selectedIndex = int.Parse(choice.Searchable.Split(' ')[0][1..]);
            var selectedFav   = favorites[selectedIndex - 1];

            var actionRunning = true;
            while (actionRunning)
            {
                var actionChoices = new List<FuzzyChoice>
                {
                    new()
                    {
                        Display       = "[silver]Detaylara Git[/]",
                        DisplayActive = "[bold white]Detaylara Git[/]",
                        Searchable    = "Detaylar"
                    },
                    new()
                    {
                        Display       = "[red]Kaldır[/]",
                        DisplayActive = "[bold red]Kaldır[/]",
                        Searchable    = "Sil"
                    },
                    new()
                    {
                        Display       = "[silver]Geri[/]",
                        DisplayActive = "[bold white]Geri[/]",
                        Searchable    = "Geri"
                    }
                };

                var actionChoice = FuzzyPrompt.Show($"İşlem Seç ({selectedFav.AnimeTitle}):", actionChoices);

                if (actionChoice == null || actionChoice.Searchable == "Geri")
                {
                    actionRunning = false;
                }
                else if (actionChoice.Searchable == "Detaylar")
                {
                    actionRunning = false;
                    viewRunning   = false;
                    var detailsView = (AnimeDetailsView) _serviceProvider.GetService(typeof(AnimeDetailsView))!;
                    detailsView.SetTarget(selectedFav.ProviderName, selectedFav.AnimeId, selectedFav.PosterUrl);
                    navigator.Push(detailsView);
                }
                else if (actionChoice.Searchable == "Sil")
                {
                    _historyService.ToggleFavorite(selectedFav);
                    Toast.Show("[green]Kaldırıldı.[/]");
                    actionRunning = false;
                }
            }
        }
    }
}
