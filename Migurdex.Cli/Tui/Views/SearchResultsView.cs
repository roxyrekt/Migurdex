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

    public override Task RenderAsync(ITuiNavigator navigator)
    {
        if (string.IsNullOrWhiteSpace(_query))
        {
            navigator.Pop();
            return Task.CompletedTask;
        }

        if (!_scanned)
        {
            ShowLiveResults(navigator);
            return Task.CompletedTask;
        }

        ShowStaticResults(navigator);
        return Task.CompletedTask;
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

            var isMovie  = r.Format == ContentFormat.Movie || AnimeDetails.IsMovieTitle(r.Title);
            var isSeries = !isMovie && r.Format == ContentFormat.Tv;
            var formatBadge = isMovie
                                  ? $" {Theme.Badge("Film", "yellow")}"
                                  : isSeries
                                      ? $" {Theme.Badge("Dizi", "grey")}"
                                      : "";
            var movieSearchable = isMovie ? " [Film]" : "";
            var scoreBadge      = r.Score is > 0 ? $" [yellow]★ {r.Score.Value:F1}[/]" : "";

            var title = Theme.Ellipsize(r.Title, 52);
            selectList.Add(new FuzzyChoice
            {
                Display =
                    $"[grey]{idxText}[/] {Markup.Escape(title)}{scoreBadge} [grey]({Markup.Escape(r.Year ?? "-")} • {Markup.Escape(r.ProviderName)})[/]{formatBadge}",
                DisplayActive =
                    $"[bold white]{Markup.Escape(title)}[/]{scoreBadge} [grey]({Markup.Escape(r.Year ?? "-")} • {Markup.Escape(r.ProviderName)})[/]{formatBadge}",
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

        var cancelChoice = TuiHelpers.Back();

        var promptResult =
            FuzzyPrompt.ShowDynamic("Sonuçlar",
                                    mappedStream,
                                    FormatSearchResults,
                                    cancelChoice,
                                    stats: scanStats,
                                    headerLines: [$"[grey]{Markup.Escape(_query!.Trim())}[/]"]);
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
        cachedChoices.Insert(0, TuiHelpers.NewSearch());

        var cachedSelection = FuzzyPrompt.Show("Sonuçlar",
                                               cachedChoices,
                                               initialSelection: _lastSelectedSearchable,
                                               headerLines: [$"[grey]{Markup.Escape(_query!.Trim())}[/]"],
                                               pinnedRowProvider: TuiHelpers.DirectSearchRow);

        if (cachedSelection?.AssociatedValue is string newQuery)
        {
            SetTarget(newQuery);
            navigator.Replace(this);
            return;
        }

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
