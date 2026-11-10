using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
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
            BuildDownloadsMenuItem(),
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
            case "İndirmeler":
                navigator.Push((BaseView) _serviceProvider.GetService(typeof(DownloadsView))!);
                break;
            case "Ayarlar":
                navigator.Push((BaseView) _serviceProvider.GetService(typeof(SettingsView))!);
                break;
        }
    }

    private FuzzyChoice BuildDownloadsMenuItem()
    {
        var queue = _serviceProvider.GetService(typeof(IDownloadQueueService)) as IDownloadQueueService;
        if (queue is null)
        {
            return Theme.MenuItem("İndirmeler");
        }

        var summary = queue.GetSummary();
        if (summary.ActiveCount > 0 || summary.QueuedCount > 0)
        {
            var activeText = $"{summary.ActiveCount} aktif";
            if (summary.QueuedCount > 0)
            {
                activeText += $", {summary.QueuedCount} sırada";
            }

            if (summary.TotalSpeedBytesPerSecond > 0)
            {
                activeText += $" • {DownloadsView.FormatBytes((long) summary.TotalSpeedBytesPerSecond)}/s";
            }

            return Theme.MenuItemMarkup(
                "İndirmeler",
                $"İndirmeler [cyan]({Markup.Escape(activeText)})[/]",
                $"İndirmeler ({Markup.Escape(activeText)})");
        }

        if (summary.FailedCount > 0)
        {
            return Theme.MenuItemMarkup(
                "İndirmeler",
                $"İndirmeler [red]({summary.FailedCount} başarısız)[/]",
                $"İndirmeler ({summary.FailedCount} başarısız)");
        }

        if (summary.PausedCount > 0)
        {
            return Theme.MenuItemMarkup(
                "İndirmeler",
                $"İndirmeler [yellow]({summary.PausedCount} duraklatıldı)[/]",
                $"İndirmeler ({summary.PausedCount} duraklatıldı)");
        }

        return Theme.MenuItem("İndirmeler");
    }

    private async Task<List<string>> BuildStatusLinesAsync()
    {
        var lines = new List<string>
        {
            $"[grey]{Markup.Escape(AppInfo.GetDisplayVersion())}[/]"
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
