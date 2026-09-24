using Migurdex.Cli.Services;
using Migurdex.Shared.Models;
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

    public override string GetRpcState()
    {
        return "Favoriler";
    }

    public override Task RenderAsync(ITuiNavigator navigator)
    {
        var viewRunning = true;
        while (viewRunning)
        {
            var favorites = _historyService.GetFavorites();

            if (favorites.Count == 0)
            {
                FuzzyPrompt.Show("Favoriler",
                                 [
                                     new FuzzyChoice
                                     {
                                         Display       = "[grey]Henüz favori yok.[/]",
                                         DisplayActive = "[grey]Henüz favori yok.[/]",
                                         Searchable    = "Henüz favori yok"
                                     },
                                     TuiHelpers.Back()
                                 ],
                                 headerLines: ["[grey]Arama › detay ekranından ♥ ile ekle[/]"]);
                navigator.Pop();
                return Task.CompletedTask;
            }

            var choices = favorites.Select((f, idx) =>
                                   {
                                       var isDisabled =
                                           _configService.Config.DisabledProviders.Contains(
                                               f.ProviderName,
                                               StringComparer.OrdinalIgnoreCase);
                                       var title = Theme.Ellipsize(f.AnimeTitle, 52);
                                       return new FuzzyChoice
                                       {
                                           Display =
                                               $"[grey]#{idx + 1}[/] {Markup.Escape(title)} [grey]({Markup.Escape(f.ProviderName)})[/]{TuiHelpers.DisabledBadge(isDisabled)}",
                                           DisplayActive =
                                               $"[bold white]{Markup.Escape(title)}[/] [grey]({Markup.Escape(f.ProviderName)})[/]{TuiHelpers.DisabledBadge(isDisabled)}",
                                           Searchable      = $"#{idx + 1} - {f.AnimeTitle} ({f.ProviderName})",
                                           AssociatedValue = f
                                       };
                                   })
                                   .ToList();

            choices.Add(TuiHelpers.ClearAll());
            choices.Add(TuiHelpers.Back());

            var choice = FuzzyPrompt.Show("Favoriler", choices, initialSelection: _lastSelectedSearchable);

            if (choice == null || choice.Searchable == "Geri")
            {
                navigator.Pop();
                return Task.CompletedTask;
            }

            _lastSelectedSearchable = choice.Searchable;

            if (choice.Searchable == "Tümünü Temizle")
            {
                if (Theme.Confirm("[red]Tüm favoriler silinsin mi?[/]"))
                {
                    _historyService.ClearFavorites();
                    Toast.Show("[green]Temizlendi.[/]");
                }

                continue;
            }

            if (choice.AssociatedValue is not FavoriteEntry selectedFav)
            {
                continue;
            }

            var actionRunning = true;
            while (actionRunning)
            {
                var actionChoices = new List<FuzzyChoice>
                {
                    Theme.AsAction(Theme.MenuItem("Detaylar", "Detaylara git")),
                    Theme.AsAction(Theme.MenuItemMarkup("Sil",
                                                        "[red]Favorilerden kaldır[/]",
                                                        "[bold white]Favorilerden kaldır[/]")),
                    TuiHelpers.Back()
                };

                var actionChoice = FuzzyPrompt.Show(selectedFav.AnimeTitle,
                                                    actionChoices,
                                                    headerLines:
                                                    [
                                                        TuiHelpers.ProviderLine(selectedFav.ProviderName)
                                                    ]);

                if (actionChoice == null || actionChoice.Searchable == "Geri")
                {
                    actionRunning = false;
                }
                else if (actionChoice.Searchable == "Detaylar")
                {
                    actionRunning = false;
                    viewRunning   = false;
                    var detailsView = (AnimeDetailsView) _serviceProvider.GetService(typeof(AnimeDetailsView))!;
                    detailsView.SetTarget(selectedFav.ProviderName,
                                          selectedFav.AnimeId,
                                          selectedFav.PosterUrl,
                                          selectedFav.AnimeTitle);
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

        return Task.CompletedTask;
    }
}
