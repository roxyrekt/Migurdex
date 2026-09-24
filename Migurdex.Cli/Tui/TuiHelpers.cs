using Migurdex.Cli.Tui.Views;
using Migurdex.Shared.Models;
using Spectre.Console;

namespace Migurdex.Cli.Tui;

public static class TuiHelpers
{
    public static FuzzyChoice Back()          => Theme.BackChoice("Geri");
    public static FuzzyChoice ManageHistory() => Theme.ActionChoice("Geçmişi Yönet...");
    public static FuzzyChoice ClearAll()      => Theme.BackChoice("Tümünü Temizle");
    public static FuzzyChoice Rescan()        => Theme.ActionChoice("Yeniden Tara");
    public static FuzzyChoice NewSearch()     => Theme.ActionChoice("Yeni Arama");

    public static FuzzyChoice DirectSearchRow(string query)
    {
        var q = query.Trim();
        return new FuzzyChoice
        {
            Display         = $"[cyan]Ara:[/] {Markup.Escape($"\"{q}\"")}",
            DisplayActive   = $"[bold white]Ara: \"{Markup.Escape(q)}\"[/]",
            Searchable      = $"Ara: {q}",
            AssociatedValue = q,
            IsAction        = true
        };
    }

    public static string MetaLine(params string[] parts)
    {
        return string.Join("  [grey]•[/]  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    public static string ProviderLine(string provider)
    {
        return $"[grey]Sağlayıcı:[/] [cyan]{Markup.Escape(provider)}[/]";
    }

    public static string DisabledBadge(bool disabled)
    {
        return disabled ? $" [red]{Markup.Escape("[!]")}[/]" : string.Empty;
    }

    public static void PushDetails(IServiceProvider services,
        ITuiNavigator                               navigator,
        string                                      provider,
        string                                      animeId,
        string?                                     posterUrl,
        string?                                     title)
    {
        var detailsView = (AnimeDetailsView) services.GetService(typeof(AnimeDetailsView))!;
        detailsView.SetTarget(provider, animeId, posterUrl, title);
        navigator.Push(detailsView);
    }

    public static void PushSources(IServiceProvider services,
        ITuiNavigator                               navigator,
        string                                      provider,
        string                                      animeId,
        string                                      animeTitle,
        Episode                                     episode,
        List<Episode>                               allEpisodes,
        string?                                     posterUrl,
        IReadOnlyList<VideoSource>?                 adoptedSources = null)
    {
        var sourcesView = (EpisodeSourcesView) services.GetService(typeof(EpisodeSourcesView))!;
        sourcesView.SetTarget(provider, animeId, animeTitle, episode, allEpisodes, posterUrl, adoptedSources);
        navigator.Push(sourcesView);
    }

    public static string FormatEpisodeRef(Episode ep)
    {
        var season = Math.Max(1, ep.Season ?? 1);
        var num = ep.Number % 1 == 0
                      ? ((int) ep.Number).ToString()
                      : ep.Number.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return $"S{season}E{num}";
    }

    public static string EllipsizedTitle(string title, int max = Theme.MaxTitleLength)
    {
        return Theme.Ellipsize(title.Trim(), max);
    }
}
