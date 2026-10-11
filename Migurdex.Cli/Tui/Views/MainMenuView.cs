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
        var tracker = _serviceProvider.GetService(typeof(BackgroundDownloadTracker))
                          as BackgroundDownloadTracker;

        ReportUnseenFinished(tracker);

        var restored = tracker?.TakePendingRestore() ?? 0;
        if (restored > 0
            && Theme.Confirm($"[yellow]{restored} yarım indirme bulundu. Devam edilsin mi?[/]"))
        {
            tracker?.ResumeAllPaused();
        }

        var activeCount = tracker?.ActiveCount ?? 0;
        var menuChoices = new List<FuzzyChoice>
        {
            Theme.MenuItem("Arama"),
            Theme.MenuItem("Favoriler"),
            Theme.MenuItem("Geçmiş"),
            Theme.MenuItem("İndirilenler",
                           activeCount > 0 ? $"İndirilenler ({activeCount} aktif)" : "İndirilenler"),
            Theme.MenuItem("Ayarlar"),
            Theme.BackChoice("Çıkış")
        };

        var choice = FuzzyPrompt.Show("Migurdex",
                                      menuChoices,
                                      initialSelection: _lastSelectedSearchable,
                                      headerLines: await BuildStatusLinesAsync(tracker));

        if (choice == null || choice.Searchable == "Çıkış")
        {
            if (choice?.Searchable == "Çıkış"
                && activeCount > 0
                && !Theme.Confirm($"[yellow]{activeCount} indirme arka planda sürüyor. Çıkılsın mı?[/]", false))
            {
                return;
            }

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
            case "İndirilenler":
                navigator.Push((BaseView) _serviceProvider.GetService(typeof(DownloadsView))!);
                break;
            case "Ayarlar":
                navigator.Push((BaseView) _serviceProvider.GetService(typeof(SettingsView))!);
                break;
        }
    }

    private static void ReportUnseenFinished(
        BackgroundDownloadTracker? tracker)
    {
        if (tracker is null)
        {
            return;
        }

        var unseen = tracker.TakeUnseenFinished(out var finished);
        if (unseen == 0)
        {
            return;
        }

        var done   = finished.Count(j => j.State == BackgroundJobState.Done);
        var failed = finished.Count - done;
        if (failed == 0)
        {
            Toast.Show($"[green]✓ {done} indirme tamamlandı.[/]");
        }
        else
        {
            Toast.Show($"[green]✓ {done}[/] [grey]/[/] [red]{failed} hata[/] - detay İndirilenler'de.");
        }
    }

    private async Task<List<string>> BuildStatusLinesAsync(
        BackgroundDownloadTracker? tracker)
    {
        var lines = new List<string>
        {
            $"[grey]{Markup.Escape(AppInfo.GetDisplayVersion())}[/]"
        };

        if (tracker is not null && tracker.ActiveCount > 0)
        {
            lines.Add($"[cyan]⬇ {tracker.ActiveCount} aktif indirme[/] [grey](İndirilenler)[/]");
        }

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
