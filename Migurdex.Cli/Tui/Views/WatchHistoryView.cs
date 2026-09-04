using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Models;
using Spectre.Console;
using System.Text.RegularExpressions;

namespace Migurdex.Cli.Tui.Views;

public class WatchHistoryView : BaseView
{
    private readonly IConfigurationService _configService;
    private readonly IHistoryService       _historyService;
    private readonly IServiceProvider      _serviceProvider;
    private          string?               _lastSelectedSearchable;

    public WatchHistoryView(
        IHistoryService       historyService,
        IConfigurationService configService,
        IServiceProvider      serviceProvider)
    {
        _historyService  = historyService;
        _configService   = configService;
        _serviceProvider = serviceProvider;
    }

    private static string GetProgressBar(double percentage)
    {
        const int totalBlocks  = 10;
        var       filledBlocks = (int) Math.Round(percentage / 100.0 * totalBlocks);
        filledBlocks = Math.Clamp(filledBlocks, 0, totalBlocks);

        var filled = new string('━', filledBlocks);
        var empty  = new string('━', totalBlocks - filledBlocks);

        if (filledBlocks == totalBlocks)
        {
            return $"[green]{filled}[/]";
        }

        if (filledBlocks > 0)
        {
            return $"[pink1]{filled[..^1]}╸[/][grey]{empty}[/]";
        }

        return $"[grey]{empty}[/]";
    }

    private static string GetFormattedEpisodeText(WatchHistoryEntry h)
    {
        var seasonText = h.Season > 0 ? $"S{h.Season}" : "S1";
        var epNum      = h.EpisodeNumber;
        if (epNum == 0)
        {
            var match = Regex.Match(h.EpisodeTitle, @"\d+");
            if (match.Success && double.TryParse(match.Value, out var parsed))
            {
                epNum = parsed;
            }
        }

        var epText = epNum > 0 ? $"B{epNum:0.#}" : "B1";

        var epTitle    = h.EpisodeTitle.Trim();
        var animeTitle = h.AnimeTitle.Trim();

        var isDuplicate = string.Equals(epTitle, animeTitle, StringComparison.OrdinalIgnoreCase)
                          || epTitle.ToLowerInvariant().Contains($"bölüm {epNum}")
                          || epTitle.ToLowerInvariant().Contains($"episode {epNum}");

        if (isDuplicate || string.IsNullOrWhiteSpace(epTitle))
        {
            return $"{seasonText}{epText}";
        }

        return $"{seasonText}{epText} - {epTitle}";
    }

    public override void Render(ITuiNavigator navigator)
    {
        var historyRunning = true;

        while (historyRunning)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[grey]~~[/] [yellow]Geçmiş[/] [grey]~~[/]");
            AnsiConsole.WriteLine();

            var fullHistory = _historyService.GetWatchHistory();

            if (fullHistory.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]Geçmiş boş.[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Geri dönmek için bir tuşa basın...[/]");
                Console.ReadKey(true);
                navigator.Pop();
                return;
            }

            var groupedHistory = GetGroupedHistory(fullHistory);
            var choices        = BuildHistoryChoices(groupedHistory);

            choices.Add(new FuzzyChoice
            {
                Display       = "[grey]Geçmişi Yönet...[/]",
                DisplayActive = "[bold yellow]Geçmişi Yönet...[/]",
                Searchable    = "Geçmişi Yönet..."
            });

            choices.Add(new FuzzyChoice
            {
                Display       = "[red]Geri[/]",
                DisplayActive = "[bold red]Geri[/]",
                Searchable    = "Geri"
            });

            var choice = FuzzyPrompt.Show("Devam et:", choices, initialSelection: _lastSelectedSearchable);

            if (choice == null || choice.Searchable == "Geri")
            {
                historyRunning = false;
                navigator.Pop();
                return;
            }

            if (choice.Searchable == "Geçmişi Yönet...")
            {
                if (ShowManageHistory(navigator))
                {
                    historyRunning = false;
                    return;
                }

                continue;
            }

            if (choice.AssociatedValue is not WatchHistoryEntry selectedHistory)
            {
                historyRunning = false;
                navigator.Pop();
                return;
            }

            _lastSelectedSearchable = choice.Searchable;
            ResumePlayback(navigator, selectedHistory);
            historyRunning = false;
            return;
        }
    }

    private static List<WatchHistoryEntry> GetGroupedHistory(IReadOnlyList<WatchHistoryEntry> fullHistory)
    {
        return fullHistory
               .GroupBy(h => new
               {
                   h.AnimeId,
                   h.ProviderName
               })
               .Select(g => g.OrderByDescending(h => h.LastWatchedAt).First())
               .OrderByDescending(h => h.LastWatchedAt)
               .ToList();
    }

    private List<FuzzyChoice> BuildHistoryChoices(List<WatchHistoryEntry> groupedHistory)
    {
        var config      = _configService.Config;
        var maxIdxWidth = groupedHistory.Count.ToString().Length;
        var maxTitleLen = groupedHistory.Count > 0 ? groupedHistory.Max(h => h.AnimeTitle.Length) : 0;
        if (maxTitleLen > 45)
        {
            maxTitleLen = 45;
        }

        return groupedHistory.Select((h, idx) =>
                             {
                                 var idxText = $"#{idx + 1}".PadRight(maxIdxWidth + 1);
                                 var epText  = GetFormattedEpisodeText(h);
                                 var bar     = GetProgressBar(h.ProgressPercentage);
                                 var progressPercent =
                                     h.IsCompleted ? "OK" : $"{h.ProgressPercentage:F0}%";

                                 var rawTitle = h.AnimeTitle;
                                 if (rawTitle.Length > maxTitleLen)
                                 {
                                     rawTitle = rawTitle[..(maxTitleLen - 3)] + "...";
                                 }

                                 var paddedTitle = rawTitle.PadRight(maxTitleLen);
                                 var isDisabled =
                                     config.DisabledProviders.Contains(
                                         h.ProviderName,
                                         StringComparer.OrdinalIgnoreCase);
                                 var providerSuffix = isDisabled ? " [red][[!]][/]" : "";

                                 return new FuzzyChoice
                                 {
                                     Display =
                                         $"[grey]{idxText}[/] [silver]{Markup.Escape(paddedTitle)}[/]  {bar}  [grey]{Markup.Escape(epText)} ({progressPercent})  |  {Markup.Escape(h.ProviderName)}{providerSuffix}[/]",
                                     DisplayActive =
                                         $"[bold pink1]{idxText}[/] [bold white]{Markup.Escape(paddedTitle)}[/]  {bar}  [bold gold1]{Markup.Escape(epText)}[/] [grey]({progressPercent})[/]  [bold mediumpurple1]|  {Markup.Escape(h.ProviderName)}[/]{providerSuffix}",
                                     Searchable      = $"{idxText} - {h.AnimeTitle} ({epText})",
                                     AssociatedValue = h
                                 };
                             })
                             .ToList();
    }

    private void ResumePlayback(ITuiNavigator navigator, WatchHistoryEntry selectedHistory)
    {
        var epNumParsed = selectedHistory.EpisodeNumber;
        if (epNumParsed == 0)
        {
            var match = Regex.Match(selectedHistory.EpisodeTitle, @"\d+");
            if (match.Success && double.TryParse(match.Value, out var parsed))
            {
                epNumParsed = parsed;
            }
        }

        var episode = new Episode
        {
            Id     = selectedHistory.EpisodeId,
            Title  = selectedHistory.EpisodeTitle,
            Number = epNumParsed,
            Season = selectedHistory.Season
        };

        List<Episode> allEpisodes = [episode];
        AnimeDetails? details     = null;
        try
        {
            var apiClient = (IApiClientService) _serviceProvider.GetService(typeof(IApiClientService))!;
            details = apiClient
                      .GetAnimeDetailsAsync(selectedHistory.ProviderName, selectedHistory.AnimeId)
                      .GetAwaiter()
                      .GetResult()
                      .Data;

            if (details is { Episodes.Count: > 0 })
            {
                allEpisodes = details.Episodes;
                var matchedEp = allEpisodes.FirstOrDefault(e => e.Id == episode.Id);
                if (matchedEp != null)
                {
                    episode = matchedEp;
                }
            }
        }
        catch
        {
            // ignored
        }

        var detailsView = (AnimeDetailsView) _serviceProvider.GetService(typeof(AnimeDetailsView))!;
        detailsView.SetTarget(selectedHistory.ProviderName,
                              selectedHistory.AnimeId,
                              selectedHistory.PosterUrl);
        navigator.Push(detailsView, true);

        var sourcesView = (EpisodeSourcesView) _serviceProvider.GetService(typeof(EpisodeSourcesView))!;
        sourcesView.SetTarget(selectedHistory.ProviderName,
                              selectedHistory.AnimeId,
                              selectedHistory.AnimeTitle,
                              episode,
                              allEpisodes,
                              selectedHistory.PosterUrl);
        navigator.Push(sourcesView);
    }

    private bool ShowManageHistory(ITuiNavigator navigator)
    {
        string? manageSelected = null;

        while (true)
        {
            var groupedHistory = GetGroupedHistory(_historyService.GetWatchHistory());
            if (groupedHistory.Count == 0)
            {
                return false;
            }

            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[grey]~~[/] [yellow]Geçmişi Yönet[/] [grey]~~[/]");
            AnsiConsole.WriteLine();

            var choices = BuildHistoryChoices(groupedHistory);
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

            var choice = FuzzyPrompt.Show("Kayıt:", choices, initialSelection: manageSelected);

            if (choice == null || choice.Searchable == "Geri")
            {
                return false;
            }

            if (choice.Searchable == "Tümünü Temizle")
            {
                if (AnsiConsole.Confirm("[bold red]Hepsi silinsin mi?[/]"))
                {
                    _historyService.ClearWatchHistory();
                    _lastSelectedSearchable = null;
                    manageSelected          = null;
                    Toast.Show("[green]Temizlendi.[/]");
                }

                continue;
            }

            if (choice.AssociatedValue is not WatchHistoryEntry selectedHistory)
            {
                return false;
            }

            manageSelected = choice.Searchable;

            var actionChoice = FuzzyPrompt.Show($"İşlem ({selectedHistory.AnimeTitle}):",
            [
                new FuzzyChoice
                {
                    Display       = "[silver]Detaylar[/]",
                    DisplayActive = "[bold white]Detaylar[/]",
                    Searchable    = "Detaylar"
                },
                new FuzzyChoice
                {
                    Display       = "[red]Sil[/]",
                    DisplayActive = "[bold red]Sil[/]",
                    Searchable    = "Sil"
                },
                new FuzzyChoice
                {
                    Display       = "[silver]Geri[/]",
                    DisplayActive = "[bold white]Geri[/]",
                    Searchable    = "Geri"
                }
            ]);

            if (actionChoice == null || actionChoice.Searchable == "Geri")
            {
                continue;
            }

            if (actionChoice.Searchable == "Sil")
            {
                _historyService.DeleteWatchHistory(selectedHistory.AnimeId, selectedHistory.ProviderName);
                if (_lastSelectedSearchable == choice.Searchable)
                {
                    _lastSelectedSearchable = null;
                }

                manageSelected = null;
                Toast.Show("[green]Silindi.[/]");
                continue;
            }

            var detailsView = (AnimeDetailsView) _serviceProvider.GetService(typeof(AnimeDetailsView))!;
            detailsView.SetTarget(selectedHistory.ProviderName,
                                  selectedHistory.AnimeId,
                                  selectedHistory.PosterUrl);
            navigator.Push(detailsView);
            return true;
        }
    }
}
