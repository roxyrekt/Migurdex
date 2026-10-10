using Migurdex.Cli.Configuration;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services.Downloads;

/// <summary>Toplu indirmede tek bir işin yaşam döngüsü.</summary>
public enum DownloadQueueItemState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Toplu indirme kuyruğundaki tek bir iş. İşin kendisi bir <see cref="DownloadResult"/> döndüren
/// temsilcidir; böylece kuyruk neyin indirildiğini bilmez, yalnızca kaç işin paralel çalışacağını
/// ve hataların birbirini nasıl etkilemeyeceğini yönetir.
/// </summary>
public sealed class DownloadQueueItem
{
    public DownloadQueueItem(string displayName, Func<CancellationToken, Task<DownloadResult>> work)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Kuyruk işi için görünen ad gerekli.", nameof(displayName));
        }

        DisplayName = displayName;
        Work        = work ?? throw new ArgumentNullException(nameof(work));
    }

    public string DisplayName { get; }

    public Func<CancellationToken, Task<DownloadResult>> Work { get; }
}

/// <summary>Kuyruk işinin sonucu; başarısız işler diğerlerini durdurmaz.</summary>
public sealed class DownloadQueueItemResult
{
    public required DownloadQueueItem       Item  { get; init; }
    public required int                     Index { get; init; }
    public required DownloadQueueItemState  State { get; init; }
    public          DownloadResult?         Result { get; init; }
    public          string?                 Error  { get; init; }

    public string DisplayName => Item.DisplayName;

    public bool Succeeded => State == DownloadQueueItemState.Completed;

    /// <summary>
    /// Video tamamlandı ancak altyazı aşaması iptal edildi. Tek indirmede bu durum çıkış kodu 3'tür.
    /// </summary>
    public bool SubtitleCancelled => State == DownloadQueueItemState.Completed
                                     && Result?.IsCancelled == true;
}

public sealed class DownloadQueueProgress
{
    public required int                    Index       { get; init; }
    public required int                    Total       { get; init; }
    public required string                 DisplayName { get; init; }
    public required DownloadQueueItemState State       { get; init; }
    public          DownloadResult?        Result      { get; init; }
}

public sealed class DownloadQueueSummary
{
    public required IReadOnlyList<DownloadQueueItemResult> Results { get; init; }

    public int  Total     => Results.Count;
    public int  Succeeded => Results.Count(result => result.State == DownloadQueueItemState.Completed);
    public int  Failed    => Results.Count(result => result.State == DownloadQueueItemState.Failed);
    public int  Cancelled => Results.Count(result => result.State == DownloadQueueItemState.Cancelled);
    public bool AllSucceeded => Total > 0 && Succeeded == Total;

    public bool AnySubtitleCancelled => Results.Any(result => result.SubtitleCancelled);
}

/// <summary>
/// Toplu indirmenin eşzamanlılık ayarı.
///
/// Sıkıştırma tek yerde ve <b>nesne kurulurken</b> yapılır: <see cref="MaxConcurrency"/> her zaman
/// <see cref="CliConfig.MinDownloadConcurrency"/>-<see cref="CliConfig.MaxDownloadConcurrency"/> aralığındadır,
/// böylece saklanan değer ile kullanılan değer asla ayrışmaz (gizli/geçersiz bir değer kalmaz).
/// Config tarafı da kendi değerini yazarken aynı kuralı uygular (<c>CliConfig.DownloadConcurrency</c>).
/// </summary>
public sealed class DownloadQueueOptions
{
    public const int DefaultConcurrency = CliConfig.DefaultDownloadConcurrency;

    private int _maxConcurrency = DefaultConcurrency;

    public bool ParallelEnabled { get; init; } = true;

    /// <summary>Paralel modda aynı anda çalışabilecek en fazla iş sayısı; her zaman geçerli aralıktadır.</summary>
    public int MaxConcurrency
    {
        get => _maxConcurrency;
        init => _maxConcurrency = CliConfig.ClampConcurrency(value);
    }

    /// <summary>
    /// Paralel kapalıysa 1 (mevcut sıralı davranış), açıksa <see cref="MaxConcurrency"/>.
    /// </summary>
    public int EffectiveConcurrency => ParallelEnabled ? _maxConcurrency : 1;

    public static DownloadQueueOptions FromConfig(CliConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new DownloadQueueOptions
        {
            ParallelEnabled = config.DownloadParallelEnabled,
            MaxConcurrency  = config.DownloadConcurrency
        };
    }
}

public interface IDownloadQueueService
{
    Task<DownloadQueueSummary> RunAsync(
        IReadOnlyList<DownloadQueueItem> items,
        DownloadQueueOptions             options,
        IProgress<DownloadQueueProgress>? progress          = null,
        CancellationToken                 cancellationToken = default);
}

/// <summary>
/// Toplu indirme motoru.
///
/// Sözleşme:
/// 1. Sıralı mod (paralel kapalı veya eşzamanlılık 1) girdi sırasını korur; bu davranış mevcut
///    tek tek indirme akışıyla aynıdır.
/// 2. Paralel modda en fazla <see cref="DownloadQueueOptions.EffectiveConcurrency"/> iş aynı anda çalışır,
///    sonuçlar her zaman girdi sırasında raporlanır.
/// 3. Bir iş başarısız olursa diğerleri etkilenmez; kısmi başarı bilinçli olarak korunur.
/// 4. İptal yalnızca kalan işleri durdurur; o ana kadar tamamlanan dosyalar silinmez.
/// </summary>
public sealed class DownloadQueueService : IDownloadQueueService
{
    public async Task<DownloadQueueSummary> RunAsync(
        IReadOnlyList<DownloadQueueItem> items,
        DownloadQueueOptions             options,
        IProgress<DownloadQueueProgress>? progress          = null,
        CancellationToken                 cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        if (items.Count == 0)
        {
            return new DownloadQueueSummary
            {
                Results = []
            };
        }

        var results = new DownloadQueueItemResult[items.Count];

        // Parça (range) bağlantı bütçesi: eşzamanlı dosya sayısına bölünür ki
        // 8 dosya × 4 parça = 32 bağlantıyla sunucu boğulmasın.
        DownloadConnectionBudget.Apply(options.EffectiveConcurrency);
        try
        {
            if (options.EffectiveConcurrency <= 1)
            {
                for (var index = 0; index < items.Count; index++)
                {
                    results[index] = cancellationToken.IsCancellationRequested
                                         ? CancelledResult(items[index], index, "İndirme iptal edildi.")
                                         : await RunItemAsync(items[index], index, items.Count, progress, cancellationToken)
                                             .ConfigureAwait(false);
                }
            }
            else
            {
                using var gate = new SemaphoreSlim(options.EffectiveConcurrency, options.EffectiveConcurrency);
                var tasks = new Task[items.Count];
                for (var index = 0; index < items.Count; index++)
                {
                    var capturedIndex = index;
                    tasks[capturedIndex] = RunGatedAsync(capturedIndex);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                async Task RunGatedAsync(int index)
                {
                    var started = false;
                    try
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        started = true;
                    }
                    catch (OperationCanceledException)
                    {
                        results[index] = CancelledResult(items[index], index, "İndirme iptal edildi.");
                        return;
                    }

                    try
                    {
                        results[index] = await RunItemAsync(items[index],
                                                            index,
                                                            items.Count,
                                                            progress,
                                                            cancellationToken)
                                               .ConfigureAwait(false);
                    }
                    finally
                    {
                        if (started)
                        {
                            gate.Release();
                        }
                    }
                }
            }
        }
        finally
        {
            DownloadConnectionBudget.Reset();
        }

        return new DownloadQueueSummary
        {
            Results = results
        };
    }

    private static async Task<DownloadQueueItemResult> RunItemAsync(
        DownloadQueueItem                 item,
        int                               index,
        int                               total,
        IProgress<DownloadQueueProgress>? progress,
        CancellationToken                 cancellationToken)
    {
        progress?.Report(new DownloadQueueProgress
        {
            Index       = index,
            Total       = total,
            DisplayName = item.DisplayName,
            State       = DownloadQueueItemState.Running
        });

        DownloadResult? result = null;
        DownloadQueueItemResult itemResult;
        try
        {
            result = await item.Work(cancellationToken).ConfigureAwait(false);
            var state = ResolveState(result);
            itemResult = new DownloadQueueItemResult
            {
                Item   = item,
                Index  = index,
                State  = state,
                Result = result,
                Error  = state == DownloadQueueItemState.Failed ? result?.Error : null
            };
        }
        catch (OperationCanceledException)
        {
            itemResult = new DownloadQueueItemResult
            {
                Item   = item,
                Index  = index,
                State  = DownloadQueueItemState.Cancelled,
                Result = result,
                Error  = "İndirme iptal edildi."
            };
        }
        catch (Exception exception)
        {
            itemResult = new DownloadQueueItemResult
            {
                Item   = item,
                Index  = index,
                State  = DownloadQueueItemState.Failed,
                Result = result,
                Error  = string.IsNullOrWhiteSpace(exception.Message)
                             ? "İndirme tamamlanamadı."
                             : exception.Message
            };
        }

        progress?.Report(new DownloadQueueProgress
        {
            Index       = index,
            Total       = total,
            DisplayName = item.DisplayName,
            State       = itemResult.State,
            Result      = itemResult.Result
        });

        return itemResult;
    }

    private static DownloadQueueItemState ResolveState(DownloadResult result)
    {
        if (result.IsCancelled)
        {
            // Video tamamlanmışsa iptal yalnızca altyazı aşamasına aittir; iş tamamlanmış sayılır.
            return string.IsNullOrWhiteSpace(result.MediaPath)
                       ? DownloadQueueItemState.Cancelled
                       : DownloadQueueItemState.Completed;
        }

        return result.Success ? DownloadQueueItemState.Completed : DownloadQueueItemState.Failed;
    }

    private static DownloadQueueItemResult CancelledResult(DownloadQueueItem item, int index, string error)
    {
        return new DownloadQueueItemResult
        {
            Item  = item,
            Index = index,
            State = DownloadQueueItemState.Cancelled,
            Error = error
        };
    }
}

/// <summary>
/// Kuyruk işlerini kuran yardımcılar. "Kaynağı çöz, adayları sırayla dene" zinciri TUI ile CLI'de
/// aynı kuralı paylaşsın diye burada tutulur; işin kurulumu (hangi bölüm, hangi hedef dizin,
/// hangi istek) <see cref="BatchDownloadPlan.BuildItem"/> içinde, zamanlama ise
/// <see cref="DownloadQueueService"/> içindedir.
/// </summary>
public static class DownloadQueueWork
{
    /// <summary>
    /// Aday kaynakları sırayla dener (tek indirmedeki "sıradaki kaynağı dene" davranışı).
    /// Video tamamlanıp yalnızca altyazı iptal edildiyse zincir durur ve sonuç korunur.
    /// </summary>
    public static Func<CancellationToken, Task<DownloadResult>> ForCandidateChain(
        IDownloadService                    downloadService,
        IReadOnlyList<VideoSource>          candidates,
        Func<VideoSource, DownloadRequest>  requestFactory)
    {
        ArgumentNullException.ThrowIfNull(downloadService);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(requestFactory);

        return async cancellationToken =>
        {
            DownloadResult? last = null;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await downloadService
                                 .DownloadAsync(requestFactory(candidate), cancellationToken)
                                 .ConfigureAwait(false);
                if (result.Success || !string.IsNullOrWhiteSpace(result.MediaPath))
                {
                    return result;
                }

                last = result;
            }

            return last ?? DownloadResult.Failed("Video indirilemedi.");
        };
    }
}
