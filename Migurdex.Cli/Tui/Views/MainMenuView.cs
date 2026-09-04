using Migurdex.Cli.Services;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class MainMenuView : BaseView
{
    private readonly IHistoryService  _historyService;
    private readonly IServiceProvider _serviceProvider;

    public MainMenuView(IServiceProvider serviceProvider, IHistoryService historyService)
    {
        _serviceProvider = serviceProvider;
        _historyService  = historyService;
    }

    public override void Render(ITuiNavigator navigator)
    {
        AnsiConsole.Clear();

        AnsiConsole.MarkupLine("[grey]~~[/] [dim]Migurdex Terminal[/] [grey]~~[/]");
        AnsiConsole.WriteLine();

        var menuChoices = new List<string>
        {
            "Arama",
            "Favoriler",
            "Geçmiş",
            "Ayarlar",
            "Çıkış"
        };

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold grey]Menü:[/]")
                .PageSize(10)
                .AddChoices(menuChoices));

        switch (choice)
        {
            case "Arama":
                navigator.Push((BaseView) _serviceProvider.GetService(typeof(SearchView))!);
                break;
            case "Favoriler":
                navigator.Push((BaseView) _serviceProvider.GetService(typeof(FavoritesView))!);
                break;
            case "Geçmiş":
                navigator.Push((BaseView) _serviceProvider.GetService(typeof(WatchHistoryView))!);
                break;
            case "Ayarlar":
                navigator.Push((BaseView) _serviceProvider.GetService(typeof(SettingsView))!);
                break;
            case "Çıkış":
                navigator.Exit();
                break;
        }
    }
}
