using Migurdex.Cli.Services;

namespace Migurdex.Cli.Tui.Views;

public class MainMenuView : BaseView
{
    private readonly IServiceProvider _serviceProvider;
    private          string?          _lastSelectedSearchable;

    public MainMenuView(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public override string GetRpcState()
    {
        return "Ana Menü";
    }

    public override void Render(ITuiNavigator navigator)
    {
        var menuChoices = new List<FuzzyChoice>
        {
            new()
            {
                Display       = "[silver]Arama[/]",
                DisplayActive = "[bold white]Arama[/]",
                Searchable    = "Arama"
            },
            new()
            {
                Display       = "[silver]Favoriler[/]",
                DisplayActive = "[bold white]Favoriler[/]",
                Searchable    = "Favoriler"
            },
            new()
            {
                Display       = "[silver]Geçmiş[/]",
                DisplayActive = "[bold white]Geçmiş[/]",
                Searchable    = "Geçmiş"
            },
            new()
            {
                Display       = "[silver]Ayarlar[/]",
                DisplayActive = "[bold white]Ayarlar[/]",
                Searchable    = "Ayarlar"
            },
            new()
            {
                Display       = "[red]Çıkış[/]",
                DisplayActive = "[bold red]Çıkış[/]",
                Searchable    = "Çıkış"
            }
        };

        var choice = FuzzyPrompt.Show("Migurdex Terminal", menuChoices, initialSelection: _lastSelectedSearchable);

        if (choice == null || choice.Searchable == "Çıkış")
        {
            navigator.Exit();
            return;
        }

        _lastSelectedSearchable = choice.Searchable;

        switch (choice.Searchable)
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
        }
    }
}
