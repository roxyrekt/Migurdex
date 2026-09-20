using Migurdex.Cli.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class SearchResultsView : BaseView
{
    private readonly IApiClientService _apiClient;
    private readonly IServiceProvider  _serviceProvider;

    private List<SearchResult?> _items = [];
    private string?             _lastSelectedSearchable;
    private string?             _query;
    private bool                _scanned;

    public SearchResultsView(IApiClientService apiClient, IServiceProvider serviceProvider)
    {
        _apiClient       = apiClient;
        _serviceProvider = serviceProvider;
    }

    public void SetTarget(string query)
    {
        _query                  = query;
        _items                  = [];
        _scanned                = false;
        _lastSelectedSearchable = null;
    }

    public override string GetRpcState()
    {
        var q = _query?.Trim();
        return string.IsNullOrEmpty(q) ? "Arama" : $"Arama: {q}";
    }

    public override void Render(ITuiNavigator navigator)
    {
        if (string.IsNullOrWhiteSpace(_query))
        {
            navigator.Pop();
            return;
        }

        if (!_scanned)
        {
            ShowLiveResults(navigator);
            return;
        }

        ShowStaticResults(navigator);
    }

    private List<FuzzyChoice> FormatSearchResults(List<SearchResult?> rawList)
    {
        var q = (_query ?? string.Empty).Trim();

        var sorted = rawList.Where(r => r != null)
                            .Select(r => r!)
                            .OrderBy(r => RankSearchMatch(r, q))
                            .ThenBy(r => r.ProviderName)
                            .ThenByDescending(r => r.Year ?? "")
                            .ThenBy(r => r.Title)
                            .ToList();

        var maxIdxWidth = sorted.Count > 0 ? sorted.Count.ToString().Length : 1;
        var selectList  = new List<FuzzyChoice>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var r       = sorted[i];
            var idx     = i + 1;
            var idxText = $"#{idx}".PadRight(maxIdxWidth + 1);

            var isMovie          = r.Format == ContentFormat.Movie || AnimeDetails.IsMovieTitle(r.Title);
            var movieBadge       = isMovie ? " [darkorange3][[Film]][/]" : "";
            var activeMovieBadge = isMovie ? " [bold darkorange][[Film]][/]" : "";
            var movieSearchable  = isMovie ? " [Film]" : "";

            selectList.Add(new FuzzyChoice
            {
                Display =
                    $"[grey]{idxText}[/] [silver]{Markup.Escape(r.Title)} ({r.Year ?? "-"}) ({Markup.Escape(r.ProviderName)})[/]{movieBadge}",
                DisplayActive =
                    $"[bold pink1]{idxText}[/] [bold white]{Markup.Escape(r.Title)}[/] [bold gold1]({Markup.Escape(r.Year ?? "-")})[/] [bold mediumpurple1]({Markup.Escape(r.ProviderName)})[/]{activeMovieBadge}",
                Searchable      = $"{idxText} - {r.Title} ({r.Year ?? "-"}) ({r.ProviderName}){movieSearchable}",
                AssociatedValue = r
            });
        }

        return selectList;
    }

    private static int RankSearchMatch(SearchResult r, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 3;
        }

        var title = (r.Title ?? string.Empty).Trim();
        if (title.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (title.StartsWith(query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private bool ShowLiveResults(ITuiNavigator navigator)
    {
        AnsiConsole.Clear();

        var scanStats    = new StreamScanStats();
        var stream       = _apiClient.SearchAnimeStreamAsync(_query!, stats: scanStats);
        var mappedStream = stream.Select(item => item.Data).Where(data => data != null);

        var cancelChoice = new FuzzyChoice
        {
            Display       = "[red]Geri[/]",
            DisplayActive = "[bold red]Geri[/]",
            Searchable    = "Geri"
        };

        var promptResult =
            FuzzyPrompt.ShowDynamic($"Sonuçlar: {_query}",
                                    mappedStream,
                                    FormatSearchResults,
                                    cancelChoice,
                                    stats: scanStats);
        var selection = promptResult?.Selection;

        _scanned = true;
        _items   = promptResult?.AccumulatedItems ?? [];

        if (selection == null
            || selection.Searchable == "Geri"
            || selection.AssociatedValue is not SearchResult selectedAnime)
        {
            navigator.Pop();
            return false;
        }

        _lastSelectedSearchable = selection.Searchable;
        PushDetails(navigator, selectedAnime);
        return true;
    }

    private void ShowStaticResults(ITuiNavigator navigator)
    {
        var cachedChoices = FormatSearchResults(_items);
        cachedChoices.Insert(0,
                             new FuzzyChoice
                             {
                                 Display       = "[yellow]Yeni Arama[/]",
                                 DisplayActive = "[bold yellow]Yeni Arama[/]",
                                 Searchable    = "Yeni Arama"
                             });

        var cachedSelection = FuzzyPrompt.Show($"Sonuçlar: {_query}",
                                               cachedChoices,
                                               initialSelection: _lastSelectedSearchable);

        if (cachedSelection == null || cachedSelection.Searchable == "Yeni Arama")
        {
            navigator.Pop();
            return;
        }

        _lastSelectedSearchable = cachedSelection.Searchable;

        if (cachedSelection.AssociatedValue is not SearchResult anime)
        {
            navigator.Pop();
            return;
        }

        PushDetails(navigator, anime);
    }

    private void PushDetails(ITuiNavigator navigator, SearchResult selectedAnime)
    {
        var detailsView = (AnimeDetailsView) _serviceProvider.GetService(typeof(AnimeDetailsView))!;
        detailsView.SetTarget(selectedAnime.ProviderName,
                              selectedAnime.Id,
                              selectedAnime.PosterUrl,
                              selectedAnime.Title);
        navigator.Push(detailsView);
    }
}
