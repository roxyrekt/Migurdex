using Migurdex.Cli.Services;
using Migurdex.Core.Services;
using Migurdex.Shared.Models;
using Spectre.Console;

namespace Migurdex.Cli.Tui;

public static class SyncAmbiguityPrompt
{
    public static void HandleAfterPlayback(IServiceProvider serviceProvider,
        SyncOutcome                                         outcome)
    {
        if (outcome.Kind is not SyncOutcomeKind.Ambiguous
            || outcome.Entry is null
            || outcome.Candidates.Count == 0)
        {
            return;
        }

        var api = (IApiClientService) serviceProvider.GetService(typeof(IApiClientService))!;

        var entry        = outcome.Entry;
        var bySearchable = new Dictionary<string, TrackerCandidate>(StringComparer.Ordinal);
        var choices      = new List<FuzzyChoice>();
        foreach (var candidate in outcome.Candidates)
        {
            var title      = DisplayTitle(candidate.Metadata);
            var searchable = $"{title} {candidate.Metadata.AniListId}";
            bySearchable[searchable] = candidate;
            choices.Add(new FuzzyChoice
            {
                Display       = $"[cyan]{Markup.Escape(title)}[/] [grey]{Detail(candidate.Metadata)}[/]",
                DisplayActive = $"[bold cyan]{Markup.Escape(title)}[/] [grey]{Detail(candidate.Metadata)}[/]",
                Searchable    = searchable
            });
        }

        while (Console.KeyAvailable)
        {
            Console.ReadKey(true);
        }

        var picked = FuzzyPrompt.Show($"Tracker eşleşmesi: {entry.AnimeTitle}", choices);
        if (picked is null || !bySearchable.TryGetValue(picked.Searchable, out var selected))
        {
            return;
        }

        var anilistId = selected.Metadata.AniListId;
        if (string.IsNullOrWhiteSpace(anilistId))
        {
            return;
        }

        var malId = selected.Metadata.MyAnimeListId;
        var saved = api.SaveTrackerMappingAsync(entry.Provider, entry.AnimeId, anilistId, malId, entry.AnimeTitle)
                       .GetAwaiter()
                       .GetResult();
        if (!saved)
        {
            Toast.Show("[red]Eşleşme kaydedilemedi; sonra tekrar sorulacak.[/]");
            return;
        }

        var sync   = (WatchSyncService) serviceProvider.GetService(typeof(WatchSyncService))!;
        var pushed = sync.FlushQueueAsync().GetAwaiter().GetResult();
        if (pushed > 0)
        {
            outcome.Kind = SyncOutcomeKind.Pushed;
            Toast.Show("[green]✓[/] Eşleşti, izleme senkronize edildi.", 1200);
        }
        else
        {
            Toast.Show("[yellow]![/] Eşleşti, gönderim kuyrukta denenecek.", 1200);
        }
    }

    public static void ShowPlaybackNotification(SyncOutcome outcome)
    {
        if (outcome.Kind == SyncOutcomeKind.Pushed && outcome.SyncedTo.Count > 0)
        {
            var trackers = string.Join(" & ", outcome.SyncedTo);
            var epText   = outcome.Progress.HasValue ? $" [grey](Bölüm {outcome.Progress.Value})[/]" : "";
            Toast.Show($"[green]✓[/] Senkronize edildi: [bold cyan]{trackers}[/]{epText}", 1200);
        }
        else if (outcome.Kind == SyncOutcomeKind.Queued)
        {
            Toast.Show("[yellow]![/] İzleme kuyruğa alındı (bağlantı kurulunca iletilecek)", 1200);
        }
    }

    private static string DisplayTitle(MediaMetadata meta)
    {
        return !string.IsNullOrWhiteSpace(meta.EnglishTitle)
                   ? meta.EnglishTitle
                   : meta.Title;
    }

    private static string Detail(MediaMetadata meta)
    {
        var parts = new List<string>();
        if (meta.Year.HasValue)
        {
            parts.Add(meta.Year.Value.ToString());
        }

        if (meta.TotalEpisodes.HasValue)
        {
            parts.Add($"{meta.TotalEpisodes.Value} bl");
        }

        if (meta.Score.HasValue)
        {
            parts.Add($"%{(int) Math.Round(meta.Score.Value * 10)}");
        }

        parts.Add($"id:{meta.AniListId}");
        return string.Join(" • ", parts);
    }
}
