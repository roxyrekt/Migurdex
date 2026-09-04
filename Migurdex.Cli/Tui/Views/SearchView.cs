using Migurdex.Cli.Services;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class SearchView : BaseView
{
    private readonly IHistoryService  _historyService;
    private readonly IServiceProvider _serviceProvider;

    private string? _lastSelectedSearchable;

    public SearchView(IHistoryService historyService, IServiceProvider serviceProvider)
    {
        _historyService  = historyService;
        _serviceProvider = serviceProvider;
    }

    public override void Render(ITuiNavigator navigator)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[grey]~~[/] [yellow]Arama[/] [grey]~~[/]");
        AnsiConsole.WriteLine();

        var searchHistory = _historyService.GetSearchHistory();
        var choices = new List<FuzzyChoice>
        {
            new()
            {
                Display       = "[silver]Arama yap...[/]",
                DisplayActive = "[bold white]Arama yap...[/]",
                Searchable    = "Arama yap..."
            }
        };
        choices.AddRange(searchHistory.Select(q => new FuzzyChoice
        {
            Display       = $"[silver]{Markup.Escape(q)}[/]",
            DisplayActive = $"[bold white]{Markup.Escape(q)}[/]",
            Searchable    = q
        }));

        if (searchHistory.Count > 0)
        {
            choices.Add(new FuzzyChoice
            {
                Display       = "[grey]Geçmişi Yönet...[/]",
                DisplayActive = "[bold yellow]Geçmişi Yönet...[/]",
                Searchable    = "Geçmişi Yönet..."
            });
        }

        var preChoice = FuzzyPrompt.Show("Arama:", choices, initialSelection: _lastSelectedSearchable);

        if (preChoice == null)
        {
            navigator.Pop();
            return;
        }

        if (preChoice.Searchable == "Geçmişi Yönet...")
        {
            ShowManageHistory();
            return;
        }

        _lastSelectedSearchable = preChoice.Searchable;

        string query;
        if (preChoice.Searchable == "Arama yap...")
        {
            query = AnsiConsole.Ask<string>("[bold cyan]Ad: [/]").Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                navigator.Pop();
                return;
            }

            _historyService.AddSearchQuery(query);
        }
        else
        {
            query = preChoice.Searchable;
        }

        var resultsView = (SearchResultsView) _serviceProvider.GetService(typeof(SearchResultsView))!;
        resultsView.SetTarget(query);
        navigator.Push(resultsView);
    }

    private void ShowManageHistory()
    {
        while (true)
        {
            var searchHistory = _historyService.GetSearchHistory();
            if (searchHistory.Count == 0)
            {
                return;
            }

            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[grey]~~[/] [yellow]Geçmişi Yönet[/] [grey]~~[/]");
            AnsiConsole.WriteLine();

            var choices = searchHistory.Select(q => new FuzzyChoice
                                       {
                                           Display       = $"[silver]{Markup.Escape(q)}[/]",
                                           DisplayActive = $"[bold red]Sil:[/] [bold white]{Markup.Escape(q)}[/]",
                                           Searchable    = q
                                       })
                                       .ToList();

            choices.Add(new FuzzyChoice
            {
                Display       = "[red]Tümünü Temizle[/]",
                DisplayActive = "[bold red]Tümünü Temizle[/]",
                Searchable    = "Tümünü Temizle"
            });

            choices.Add(new FuzzyChoice
            {
                Display       = "[red]Geri[/]",
                DisplayActive = "[bold red]Geri[/]",
                Searchable    = "Geri"
            });

            var choice = FuzzyPrompt.Show("Silinecek kayıt:", choices);

            if (choice == null || choice.Searchable == "Geri")
            {
                return;
            }

            if (choice.Searchable == "Tümünü Temizle")
            {
                if (AnsiConsole.Confirm("[bold red]Arama geçmişi silinsin mi?[/]"))
                {
                    _historyService.ClearSearchHistory();
                    _lastSelectedSearchable = null;
                    Toast.Show("[green]Temizlendi.[/]");
                }

                return;
            }

            _historyService.DeleteSearchQuery(choice.Searchable);
            if (_lastSelectedSearchable == choice.Searchable)
            {
                _lastSelectedSearchable = null;
            }

            Toast.Show("[green]Silindi.[/]");
        }
    }
}
