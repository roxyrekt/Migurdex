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

    public override string GetRpcState()
    {
        return "Arama";
    }

    public override Task RenderAsync(ITuiNavigator navigator)
    {
        var searchHistory = _historyService.GetSearchHistory();
        var choices       = searchHistory.Select(q => Theme.MenuItem(q)).ToList();

        if (searchHistory.Count > 0)
        {
            choices.Add(TuiHelpers.ManageHistory());
        }

        choices.Add(TuiHelpers.Back());

        var historyEmpty = searchHistory.Count == 0;
        var preChoice = FuzzyPrompt.Show("Arama",
                                         choices,
                                         initialSelection: _lastSelectedSearchable,
                                         headerLines: historyEmpty
                                                          ? ["[grey]Aramak için yazmaya başla, Enter ile ara[/]"]
                                                          : null,
                                         pinnedRowProvider: TuiHelpers.DirectSearchRow,
                                         footerHelp: historyEmpty ? "Yaz + Enter: ara • Esc: geri" : null);

        if (preChoice?.AssociatedValue is string directQuery)
        {
            _historyService.AddSearchQuery(directQuery);
            PushResults(navigator, directQuery);
            return Task.CompletedTask;
        }

        if (preChoice == null || preChoice.Searchable == "Geri")
        {
            navigator.Pop();
            return Task.CompletedTask;
        }

        if (preChoice.Searchable == "Geçmişi Yönet...")
        {
            ShowManageHistory();
            return Task.CompletedTask;
        }

        _lastSelectedSearchable = preChoice.Searchable;

        PushResults(navigator, preChoice.Searchable);
        return Task.CompletedTask;
    }

    private void PushResults(ITuiNavigator navigator, string query)
    {
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

            var choices = searchHistory
                          .Select(q => Theme.MenuItemMarkup(q,
                                                            $"{Markup.Escape(q)}",
                                                            $"[red]Sil:[/] {Markup.Escape(q)}"))
                          .ToList();

            choices.Add(TuiHelpers.ClearAll());
            choices.Add(TuiHelpers.Back());

            var choice = FuzzyPrompt.Show("Arama Geçmişi", choices);

            if (choice == null || choice.Searchable == "Geri")
            {
                return;
            }

            if (choice.Searchable == "Tümünü Temizle")
            {
                if (Theme.Confirm("[red]Tüm arama geçmişi silinsin mi?[/]"))
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
