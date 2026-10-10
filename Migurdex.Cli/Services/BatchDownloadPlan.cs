using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Models;
using System.Globalization;

namespace Migurdex.Cli.Services;

/// <summary>Sonuç ekranında tek bir satır.</summary>
public sealed record BatchSummaryRow(string Label, DownloadQueueItemState State, string Detail);

/// <summary>
/// Toplu indirmenin karar mantığı: hangi bölümler listelenir, hangileri seçilir, her bölüm için
/// hangi kuyruk işi kurulur ve sonuçlar nasıl özetlenir. Ekran (Spectre) kodu burada değildir;
/// TUI ve CLI aynı planı kullanır, böylece "hangi kaynaklar denenir / hata nasıl raporlanır"
/// kuralı tek yerde kalır.
/// </summary>
public static class BatchDownloadPlan
{
    /// <summary>
    /// Bölümleri sezon → numara → id sırasına dizer ve <paramref name="season"/> verilmişse
    /// yalnızca o sezonu döndürür. Sezon verilmezse tüm sezonlar gelir.
    /// </summary>
    public static List<Episode> OrderEpisodes(IEnumerable<Episode> episodes, int? season)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        return episodes.Where(episode => !season.HasValue || (episode.Season ?? 1) == season.Value)
                       .OrderBy(episode => episode.Season ?? 1)
                       .ThenBy(episode => episode.Number)
                       .ThenBy(episode => episode.Id, StringComparer.Ordinal)
                       .ToList();
    }

    public static List<string> BuildLabels(IReadOnlyList<Episode> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        return ordered.Select(DescribeEpisode).ToList();
    }

    /// <summary>Çoklu seçim indekslerini seçilen bölümlere çevirir (sıra korunur, tekrar atılır).</summary>
    public static List<Episode> FromSelection(IReadOnlyList<Episode> ordered, IReadOnlyList<int> selectedIndices)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(selectedIndices);

        return selectedIndices.Where(index => index >= 0 && index < ordered.Count)
                              .Distinct()
                              .OrderBy(index => index)
                              .Select(index => ordered[index])
                              .ToList();
    }

    public static string DescribeEpisode(Episode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var season = Math.Max(1, episode.Season ?? 1);
        var number = episode.Number % 1 == 0
                         ? ((int) episode.Number).ToString("00", CultureInfo.InvariantCulture)
                         : episode.Number.ToString("00.#", CultureInfo.InvariantCulture);
        var prefix = $"S{season.ToString("00", CultureInfo.InvariantCulture)}E{number}";
        var title  = episode.Title?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(title) ? prefix : $"{prefix} - {title}";
    }

    public static string DescribeConcurrency(bool parallelEnabled, int effectiveConcurrency)
    {
        if (!parallelEnabled)
        {
            return "sıralı (paralel kapalı)";
        }

        // Parçalar da eşzamanlıdır: kullanıcı "8 eşzamanlı" derken kaç bağlantı açıldığını
        // görebilsin (DownloadConnectionBudget toplamı 8 bağlantıda tutar).
        var segments = DownloadConnectionBudget.SegmentsFor(effectiveConcurrency);
        var detail   = segments > 1
                           ? $" • dosya başına {segments} parça"
                           : " • dosya başına 1 bağlantı";

        return $"{effectiveConcurrency} eşzamanlı (paralel){detail}";
    }

    /// <summary>
    /// Tek bir bölümün kuyruk işini kurar: kaynağı çözer, adayları sırayla dener.
    /// <paramref name="requestFactory"/> hedef dizin/ilerleme gibi çağırana özel alanları bağlar.
    /// </summary>
    public static DownloadQueueItem BuildItem(
        IApiClientService                  api,
        IDownloadService                   downloadService,
        string                             provider,
        DownloadSourceFormat               format,
        string?                            group,
        Episode                            episode,
        CliConfig                          config,
        Func<VideoSource, DownloadRequest> requestFactory,
        AnimeciXBatchSourceResolver?       animeciXResolver = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(downloadService);
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(requestFactory);

        return new DownloadQueueItem(DescribeEpisode(episode), async cancellationToken =>
        {
            Task<DownloadCandidateResult> Resolve(CancellationToken token)
            {
                return DownloadCandidateResolver.ResolveAsync(api,
                                                              provider,
                                                              episode.Id,
                                                              group,
                                                              format,
                                                              config,
                                                              config.DownloadAutoSelectTimeoutSeconds,
                                                              token);
            }

            var resolved = provider.Equals("AnimeciX", StringComparison.OrdinalIgnoreCase)
                               ? await (animeciXResolver ?? AnimeciXBatchSourceResolver.Shared)
                                     .ResolveAsync(Resolve, cancellationToken)
                                     .ConfigureAwait(false)
                               : await Resolve(cancellationToken).ConfigureAwait(false);
            if (resolved.TimedOut)
            {
                return DownloadResult.Failed(
                    $"Kaynak taraması zaman aşımına uğradı ({config.DownloadAutoSelectTimeoutSeconds:F1} sn).");
            }

            if (resolved.Error is not null)
            {
                return DownloadResult.Failed(resolved.Error);
            }

            if (resolved.Candidates.Count == 0)
            {
                return DownloadResult.Failed(NoCandidateMessage(format));
            }

            var chain = DownloadQueueWork.ForCandidateChain(downloadService,
                                                            resolved.Candidates,
                                                            requestFactory);
            return await chain(cancellationToken).ConfigureAwait(false);
        });
    }

    private static string NoCandidateMessage(DownloadSourceFormat format)
    {
        return format == DownloadSourceFormat.Auto
                   ? "API'den indirilebilir doğrudan MP4/HLS kaynağı bulunamadı."
                   : $"'{format.ToString().ToLowerInvariant()}' biçiminde uygun kaynak bulunamadı.";
    }

    public static string SummaryTitle(DownloadQueueSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (summary.Total == 0)
        {
            return "Toplu indirme boş";
        }

        return summary.AllSucceeded ? "Toplu indirme tamamlandı" : "Toplu indirme kısmen tamamlandı";
    }

    public static string DescribeCounts(DownloadQueueSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return $"{summary.Total} bölümden {summary.Succeeded} indirildi, "
               + $"{summary.Failed} hata, {summary.Cancelled} iptal";
    }

    public static IReadOnlyList<BatchSummaryRow> BuildSummaryRows(
        DownloadQueueSummary  summary,
        IReadOnlyList<Episode> chosen)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(chosen);

        var rows = new List<BatchSummaryRow>(summary.Results.Count);
        foreach (var result in summary.Results)
        {
            var episode = result.Index >= 0 && result.Index < chosen.Count ? chosen[result.Index] : null;
            var label   = episode is null ? result.DisplayName : DescribeEpisode(episode);
            var detail = result.State switch
            {
                DownloadQueueItemState.Completed => result.Result?.MediaPath ?? "tamamlandı",
                DownloadQueueItemState.Failed    => result.Error ?? result.Result?.Error ?? "indirilemedi",
                DownloadQueueItemState.Cancelled => "iptal edildi",
                _                                => string.Empty
            };
            rows.Add(new BatchSummaryRow(label, result.State, detail));
        }

        return rows;
    }
}
