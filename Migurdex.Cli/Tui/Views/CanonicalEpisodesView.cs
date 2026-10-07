using Migurdex.Cli.Services;
using Migurdex.Shared.Models;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class CanonicalEpisodesView : BaseView
{
    private readonly IApiClientService _apiClient;
    private readonly IServiceProvider  _serviceProvider;

    private string? _query;
    private string? _displayTitle;
    private List<CanonicalAnime>?          _candidates;
    private CanonicalAnime?                _picked;
    private CanonicalEpisodeResult?        _episodesResult;
    private string?                        _lastSelectedSearchable;

    public CanonicalEpisodesView(IApiClientService apiClient, IServiceProvider serviceProvider)
    {
        _apiClient       = apiClient;
        _serviceProvider = serviceProvider;
    }

    public void SetTarget(string query, string? displayTitle = null)
    {
        if (_query != query)
        {
            _query                 = query;
            _displayTitle          = displayTitle ?? query;
            _candidates            = null;
            _picked                = null;
            _episodesResult        = null;
            _lastSelectedSearchable = null;
        }
    }

    public override string GetRpcState()
    {
        return _picked?.Title ?? _displayTitle ?? "Birleştirilmiş Bölümler";
    }

    public override async Task RenderAsync(ITuiNavigator navigator)
    {
        if (string.IsNullOrWhiteSpace(_query))
        {
            navigator.Pop();
            return;
        }

        if (_picked is null && !await ResolveCanonicalAsync(navigator))
        {
            return;
        }

        if (_episodesResult is null && !await LoadEpisodesAsync(navigator))
        {
            return;
        }

        ShowEpisodeList(navigator);
    }

    private async Task<bool> ResolveCanonicalAsync(ITuiNavigator navigator)
    {
        if (_candidates is null)
        {
            List<CanonicalAnime>? found = null;
            await AnsiConsole.Status()
                             .Spinner(Spinner.Known.Dots)
                             .StartAsync("Birleştirilmiş sonuç aranıyor...",
                                         async _ =>
                                         {
                                             var result = await _apiClient.SearchCanonicalAsync(_query!);
                                             if (result.IsSuccess)
                                             {
                                                 found = result.Data.ToList();
                                             }
                                         });

            if (found is null || found.Count == 0)
            {
                Toast.Show("[yellow]Birleştirilmiş sonuç yok, klasik listedeyim.[/]");
                navigator.Pop();
                return false;
            }

            if (found.Count == 1)
            {
                _picked = found[0];
                return true;
            }

            _candidates = found;
        }

        var choices = _candidates.Select(c => new FuzzyChoice
        {
            Display =
                $"{Markup.Escape(c.Title)} [grey]({c.Year?.ToString() ?? "?"} • {c.Format} • ★{c.Score?.ToString("0.0") ?? "?"})[/]",
            DisplayActive = $"[bold white]{Markup.Escape(c.Title)}[/]",
            Searchable    = c.CanonicalId,
            AssociatedValue = c
        }).ToList();
        choices.Add(TuiHelpers.Back());

        var choice = FuzzyPrompt.Show(_displayTitle ?? "Birleştirilmiş Sonuç",
                                      choices,
                                      headerLines: ["[grey]Hangi kayıt? (tümü sağlayıcılarda aranır)[/]"]);
        if (choice is null || choice.Searchable == "Geri")
        {
            navigator.Pop();
            return false;
        }

        _picked = choice.AssociatedValue as CanonicalAnime;
        if (_picked is null)
        {
            navigator.Pop();
            return false;
        }

        return true;
    }

    private async Task<bool> LoadEpisodesAsync(ITuiNavigator navigator)
    {
        CanonicalEpisodeResult? result = null;
        await AnsiConsole.Status()
                         .Spinner(Spinner.Known.Dots)
                         .StartAsync("Sağlayıcılar taranıyor, uzun sürebilir...",
                                     async _ =>
                                     {
                                         var response =
                                             await _apiClient.GetCanonicalEpisodesAsync(_picked!.CanonicalId);
                                         if (response.IsSuccess)
                                         {
                                             result = response.Data;
                                         }
                                     });

        if (result is null || result.Episodes.Count == 0)
        {
            Toast.Show("[yellow]Birleştirilmiş bölüm bulunamadı, klasik listedeyim.[/]");
            navigator.Pop();
            return false;
        }

        _episodesResult = result;
        return true;
    }

    private void ShowEpisodeList(ITuiNavigator navigator)
    {
        var result = _episodesResult!;
        var headerLines = new List<string>
        {
            $"[grey]{result.ProvidersMatched} sağlayıcı • {result.Episodes.Count} bölüm[/]"
        };
        foreach (var warning in result.Warnings.Take(3))
        {
            headerLines.Add($"[grey]{Markup.Escape(Theme.Ellipsize(warning, 100))}[/]");
        }

        var choices = new List<FuzzyChoice>();
        foreach (var ep in result.Episodes.OrderBy(e => e.Number))
        {
            var num = ep.Number % 1 == 0
                          ? ((int) ep.Number).ToString("00")
                          : ep.Number.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            var title = string.IsNullOrWhiteSpace(ep.Title) ? "" : $" │ {Markup.Escape(Theme.Ellipsize(ep.Title, 40))}";
            choices.Add(new FuzzyChoice
            {
                Display       = $"[grey]{num}[/] Bölüm{title} [grey][[{ep.Sources.Count} kaynak]][/]",
                DisplayActive = $"[bold white]{num} Bölüm[/]{title} [grey][[{ep.Sources.Count} kaynak]][/]",
                Searchable    = $"{num} Bölüm",
                AssociatedValue = ep
            });
        }

        choices.Add(TuiHelpers.Back());

        var choice = FuzzyPrompt.Show(result.Anime.Title,
                                      choices,
                                      initialSelection: _lastSelectedSearchable,
                                      headerLines: headerLines);
        if (choice is null || choice.Searchable == "Geri")
        {
            navigator.Pop();
            return;
        }

        _lastSelectedSearchable = choice.Searchable;

        if (choice.AssociatedValue is not CanonicalEpisodeEntry entry)
        {
            return;
        }

        var sourcesView =
            (CanonicalSourcesView) _serviceProvider.GetService(typeof(CanonicalSourcesView))!;
        sourcesView.SetTarget(result.Anime.CanonicalId, result.Anime.Title, entry, entry.Sources);
        navigator.Push(sourcesView);
    }
}
