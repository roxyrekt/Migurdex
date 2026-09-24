using Migurdex.Cli.Services;
using Migurdex.Shared.Update;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class MainMenuView : BaseView
{
    private readonly IServiceProvider _serviceProvider;

    private string? _lastSelectedSearchable;

    public MainMenuView(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public override string GetRpcState()
    {
        return "Ana Menü";
    }

    public override async Task RenderAsync(ITuiNavigator navigator)
    {
        var menuChoices = new List<FuzzyChoice>
        {
            Theme.MenuItem("Arama"),
            Theme.MenuItem("Favoriler"),
            Theme.MenuItem("Geçmiş"),
            Theme.MenuItem("Ayarlar"),
            Theme.BackChoice("Çıkış")
        };

        var choice = FuzzyPrompt.Show("Migurdex",
                                      menuChoices,
                                      initialSelection: _lastSelectedSearchable,
                                      headerLines: await BuildStatusLinesAsync());

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

    private async Task<List<string>> BuildStatusLinesAsync()
    {
        var lines = new List<string>
        {
            AppInfo.IsDevBuild ? "[grey]dev[/]" : $"[grey]v{Markup.Escape(AppInfo.GetVersion())}[/]"
        };

        try
        {
            var api = _serviceProvider.GetService(typeof(IApiClientService)) as IApiClientService;
            if (api is not null)
            {
                var providers = await api.GetProvidersAsync();
                if (providers.IsSuccess)
                {
                    lines.Add($"[grey]{providers.Data.Count} sağlayıcı[/]");
                }
            }
        }
        catch
        {
            // ignored
        }

        return lines;
    }
}
