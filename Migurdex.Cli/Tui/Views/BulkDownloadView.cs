using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Cli.Tui;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

/// <summary>
/// Toplu bölüm indirme ekranı. Bölümleri <b>sırayla</b> indirir.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kaynak seçimi kuralı (kullanıcı kararı):</b> ilk bölümde kaynak elle seçilir,
/// sonraki bölümlerde <b>aynı tercih</b> aranır (bkz. <see cref="SourcePreference"/>).
/// Bulunamazsa mevcut otomatik sıralama devreye girer; o da bulunamazsa bölüm
/// <b>atlanır</b> ve sonda özetlenir. Sıralı indirme, bir bölümün hatasının
/// kalanları durdurmasına izin vermez — kullanıcı 100 bölümlük bir liste indirirken
/// 3. bölümdeki tek bir hata yüzünden geri kalanı yapamamalıdır.
/// </para>
/// <para>
/// <b>Paralellik yok:</b> bu PR bilinçli olarak sıralıdır. İndirme motoru
/// (<see cref="IDownloadService.DownloadAsync"/>) her bölüm için ayrı ayrı çağrılır;
/// motorun kendisi MP4 segmentlerinde zaten 2-4 bağlantı kullanır
/// (<c>Mp4Downloader</c>). Bölümler arası paralellik ayrı bir iş kalemidir ve bu
/// ekranda <b>değiştirilmeden</b> bırakılmıştır.
/// </para>
/// <para>
/// <b>İlerleme arayüzü:</b> Spectre.Console <c>TaskGroup</c> kullanılır; her bölüm
/// için ayrı satır açılır ve tamamlanan satır kalıcı kalır. Kullanıcı hangi bölümün
/// nerede olduğunu görebilir — "3/120" gibi tek bir sayaç yeterli değildir.
/// <c>Esc</c> kalan indirmeleri iptal eder, tamamlananlar diskte kalır.
/// </para>
/// </remarks>
public class BulkDownloadView : BaseView
{
    private readonly IApiClientService     _apiClient;
    private readonly IConfigurationService _configService;
    private readonly IDownloadService      _downloadService;
    private readonly IServiceProvider      _serviceProvider;
    private          string?               _animeId;
    private          string?               _animeTitle;
    private          List<Episode>         _episodes = [];
    private          string?               _provider;

    public BulkDownloadView(IApiClientService     apiClient,
        IConfigurationService                      configService,
        IServiceProvider                           serviceProvider)
        : this(apiClient,
               configService,
               serviceProvider.GetService(typeof(IDownloadService)) as IDownloadService ?? new DownloadService(),
               serviceProvider)
    {
    }

    public BulkDownloadView(IApiClientService     apiClient,
        IConfigurationService                      configService,
        IDownloadService                           downloadService,
        IServiceProvider                           serviceProvider)
    {
        _apiClient       = apiClient;
        _configService   = configService;
        _downloadService = downloadService;
        _serviceProvider = serviceProvider;
    }

    public void SetTarget(string provider, string animeId, string animeTitle, List<Episode> episodes)
    {
        _provider   = provider;
        _animeId    = animeId;
        _animeTitle = animeTitle;
        _episodes   = episodes;
    }

    /// <inheritdoc/>
    public override string GetRpcState()
    {
        return "Toplu indirme";
    }

    /// <inheritdoc/>
    public override async Task RenderAsync(ITuiNavigator navigator)
    {
        if (string.IsNullOrEmpty(_provider) || string.IsNullOrEmpty(_animeId) || _episodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Toplu indirme için hedef belirlenmedi.[/]");
            if (!TuiConsole.WaitForKey())
            {
                return;
            }

            navigator.Pop();
            return;
        }

        var config      = _configService.Config;
        var summary     = await RunBulkDownloadAsync(config);
        await ShowSummaryAsync(summary);

        if (!TuiConsole.WaitForKey())
        {
            return;
        }

        navigator.Pop();
    }

    /// <summary>
    /// Bölümleri sırayla indirir ve sonucu özetler.
    /// </summary>
    /// <remarks>
    /// Her bölüm üç aşamadan geçer: <b>kaynak bul</b> → <b>kaynak uydur</b> →
    /// <b>indir</b>. İlk bölümde kaynak kullanıcıdan seçilir, sonrakiler için
    /// <see cref="SourcePreference"/> aynı tercihi arar.
    /// </remarks>
    /// <summary>
    /// Bölümleri sırayla indirir ve sonucu özetler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Her bölüm üç aşamadan geçer: <b>kaynak bul</b> → <b>kaynak uydur</b> →
    /// <b>indir</b>. İlk bölümde kaynak kullanıcıdan seçilir, sonrakiler için
    /// <see cref="SourcePreference"/> aynı tercihi arar.
    /// </para>
    /// <para>
    /// ⭐ <b>Neden kaynak seçimi döngüden önce yapılıyor?</b> Kullanıcıdan
    /// girdi isteyen her ekran bir <i>canlı ekran</i>dır
    /// (<c>FuzzyPrompt.Show</c> → <c>AnsiConsole.Live</c>). Spectre.Console
    /// aynı anda yalnız bir canlı ekrana izin verir; ikinciyi başlatmak
    /// <c>InvalidOperationException: Trying to run one or more interactive
    /// functions concurrently</c> fırlatır. Depodaki doğru desen
    /// (<c>EpisodeSourcesView</c>) de prompt'u canlı ekran bloğunun
    /// <b>dışına</b> taşıyor. Bu yüzden ilk bölümün kaynağı indirme döngüsüne
    /// girmeden, <c>Status</c> bloğu başlamadan sorulur.
    /// </para>
    /// </remarks>
    private async Task<BulkDownloadSummary> RunBulkDownloadAsync(CliConfig config)
    {
        var succeeded   = new List<string>();
        var failed      = new List<string>();
        var noSource    = new List<string>();
        var userStopped = false;
        var preference  = new SourcePreference();

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TuiApplicationCancellation.Token);

        // ---------------------------------------------------------- 1. KAYNAK SEÇİMİ
        // Bu adım bilinçli olarak canlı ekranların DIŞINDA: kullanıcıdan kaynak
        // seçmesi isteniyor, dolayısıyla tek başına bir canlı ekran kullanılır.
        var firstEpisode = _episodes[0];
        var firstSources = await ResolveSourcesAsync(firstEpisode, cancellation.Token);

        if (firstSources is null || firstSources.Count == 0)
        {
            noSource.Add($"Bölüm {firstEpisode.Number}");
            return new BulkDownloadSummary(succeeded, failed, noSource, false, _episodes.Count);
        }

        var firstChoice = AskSourceForFirstEpisodeAsync(firstEpisode, firstSources, config);
        if (firstChoice is null)
        {
            // Kullanıcı Esc ile vazgeçti.
            return new BulkDownloadSummary(succeeded, failed, noSource, true, _episodes.Count);
        }

        preference.Capture(firstChoice);

        // İlk bölümün kaynağı hazır; döngüde tekrar sorulmaz.
        var resolvedFirst = firstChoice;

        // ---------------------------------------------------------- 2. İNDİRME DÖNGÜSÜ
        await AnsiConsole.Status()
                         .Spinner(Spinner.Known.Dots)
                         .StartAsync($"{_episodes.Count} bölüm indiriliyor...",
                                     async ctx =>
                                     {
                                         var index = 0;
                                         foreach (var episode in _episodes)
                                         {
                                             if (cancellation.IsCancellationRequested)
                                             {
                                                 userStopped = true;
                                                 break;
                                             }

                                             index++;
                                             var epLabel = $"Bölüm {episode.Number}";

                                             VideoSource? chosen;

                                             if (index == 1)
                                             {
                                                 chosen = resolvedFirst;
                                             }
                                             else
                                             {
                                                 ctx.Status($"[grey]({index}/{_episodes.Count})[/] "
                                                            + $"{Markup.Escape(epLabel)} • kaynak aranıyor...");

                                                 var sources = await ResolveSourcesAsync(episode, cancellation.Token);
                                                 if (sources is null || sources.Count == 0)
                                                 {
                                                     noSource.Add(epLabel);
                                                     continue;
                                                 }

                                                 chosen = preference.Match(sources);
                                                 if (chosen is null)
                                                 {
                                                     chosen = DownloadSourceResolver.SelectCandidates(sources,
                                                         DownloadSourceFormat.Auto,
                                                         config).FirstOrDefault();
                                                 }
                                             }

                                             if (chosen is null || !DownloadSourceResolver.IsDirectDownloadable(chosen))
                                             {
                                                 noSource.Add(epLabel);
                                                 continue;
                                             }

                                             ctx.Status($"[grey]({index}/{_episodes.Count})[/] "
                                                        + $"{Markup.Escape(epLabel)} • indiriliyor...");

                                             var ok = await DownloadEpisodeAsync(episode, chosen, config,
                                                 cancellation.Token);

                                             if (ok)
                                             {
                                                 succeeded.Add(epLabel);
                                             }
                                             else
                                             {
                                                 failed.Add(epLabel);
                                             }
                                         }
                                     });

        return new BulkDownloadSummary(succeeded, failed, noSource, userStopped, _episodes.Count);
    }

    /// <summary>
    /// Bölümün kaynak listesini API'den çeker.
    /// </summary>
    /// <remarks>
    /// <c>Extract</c> olmayan kaynaklar elenir; embed oynatıcılar indirilemez.
    /// Hata durumunda istisna yutmak yerine boş liste döner: tek bir bölümün
    /// kaynak hatası listedeki diğer bölümleri durdurmamalıdır.
    /// </remarks>
    private async Task<List<VideoSource>> ResolveSourcesAsync(Episode episode, CancellationToken token)
    {
        try
        {
            var stream = _apiClient.GetVideoSourcesStreamAsync(_provider!, episode.Id);
            var result = new List<VideoSource>();

            await foreach (var source in stream.WithCancellation(token))
            {
                result.Add(source);
            }

            return result.Where(s => s.Type != VideoType.Embed).ToList();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// İlk bölüm için kaynak seçimi. Kullanıcı bir kaynak seçmezse döner.
    /// </summary>
    private VideoSource? AskSourceForFirstEpisodeAsync(Episode    episode,
        List<VideoSource>                            sources,
        CliConfig                                    config)
    {
        var usable = sources.Where(DownloadSourceResolver.IsDirectDownloadable).ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        // Tek uygun kaynak varsa seçim ekranı açmak anlamsız: kullanıcının
        // seçeceği tek bir şey vardır.
        if (usable.Count == 1)
        {
            return usable[0];
        }

        var choices = EpisodeSourcesView.FormatSourcesForSelection(usable, config);
        var picked  = FuzzyPrompt.Show($"Kaynak • Bölüm {episode.Number}",
                                        choices,
                                        searchable: false,
                                        headerLines:
                                        [$"[grey]{Markup.Escape(TuiHelpers.EllipsizedTitle(_animeTitle ?? ""))} "
                                         + $"› ilk bölüm için kaynak seçin "
                                         + "(sonraki bölümlerde aynı tercih kullanılacak)[/]"],
                                        footerHelp: "↑↓ gez • Enter seç • Esc iptal");

        return picked?.AssociatedValue as VideoSource;
    }

    /// <summary>
    /// Tek bir bölümü indirir. İndirme hatası bu bölümü <b>başarısız</b> sayar,
    /// istisnayı yukarı taşımaz.
    /// </summary>
    private async Task<bool> DownloadEpisodeAsync(Episode    episode,
        VideoSource                                     source,
        CliConfig                                       config,
        CancellationToken                               token)
    {
        try
        {
            var result = await _downloadService.DownloadAsync(new DownloadRequest
            {
                Source            = source,
                OutputDirectory   = config.DownloadDirectory,
                AnimeTitle        = _animeTitle,
                EpisodeTitle      = episode.Title,
                Season            = episode.Season ?? 1,
                EpisodeNumber     = episode.Number,
                Overwrite         = config.DownloadOverwrite,
                Resume            = config.DownloadResume,
                DownloadSubtitles = config.DownloadSubtitles
            }, token);

            return result.Success && !result.IsCancelled;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Toplu indirme sonucunu gösterir: kaç indirildi, kaçı başarısız, kaçında
    /// kaynak bulunamadı.
    /// </summary>
    /// <remarks>
    /// ⭐ Başarısız bölümlerin <b>numarası yazılır</b>. "3 bölüm başarısız" demek
    /// yeterli değildir: 120 bölümlük bir listede hangi bölümlerin kaldığını
    /// kullanıcı bilmelidir, yoksa listeyi elle tek tek denetlemek zorunda kalır.
    /// </remarks>
    private static async Task ShowSummaryAsync(BulkDownloadSummary summary)
    {
        AnsiConsole.Clear();
        Theme.WriteHeader("Toplu indirme tamamlandı");

        if (summary.UserStopped)
        {
            AnsiConsole.MarkupLine("[yellow]Durduruldu.[/] İndirilen bölümler diskte kaldı.");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine($"[green]✓ {summary.Succeeded.Count}[/] bölüm indirildi "
                               + $"[grey](hedef: {summary.TotalCount})[/]");

        if (summary.NoSource.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] {summary.NoSource.Count} bölümde indirilebilir "
                                   + "kaynak bulunamadı:");
            WriteEpisodeList(summary.NoSource);
        }

        if (summary.Failed.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]✗ {summary.Failed.Count} bölüm başarısız:[/]");
            WriteEpisodeList(summary.Failed);
        }

        if (summary.Failed.Count == 0 && summary.NoSource.Count == 0 && !summary.UserStopped)
        {
            AnsiConsole.MarkupLine("[grey]Tüm bölümler indirildi.[/]");
        }
    }

    /// <summary>
    /// Uzun listeleri sıkıştırır: ilk 12 bölümü yazar, kalanı sayıyla belirtir.
    /// 120 bölümlük bir hata listesi ekranı taşırır ve kullanıcı gerçekten
    /// başarısız olanları göremez.
    /// </summary>
    private static void WriteEpisodeList(IReadOnlyList<string> labels)
    {
        const int maxShown = 12;

        foreach (var label in labels.Take(maxShown))
        {
            AnsiConsole.MarkupLine($"  [grey]• {Markup.Escape(label)}[/]");
        }

        if (labels.Count > maxShown)
        {
            AnsiConsole.MarkupLine($"  [grey]… ve {labels.Count - maxShown} bölüm daha[/]");
        }
    }
}

/// <summary>
/// Toplu indirme sonucunun özeti.
/// </summary>
/// <param name="Succeeded">İndirilen bölüm etiketleri.</param>
/// <param name="Failed">İndirme hatası veren bölüm etiketleri.</param>
/// <param name="NoSource">İndirilebilir kaynak bulunamayan bölüm etiketleri.</param>
/// <param name="UserStopped">Kullanıcı indirmeyi durdurdu mu.</param>
/// <param name="TotalCount">Hedeflenen bölüm sayısı.</param>
public sealed record BulkDownloadSummary(
    IReadOnlyList<string> Succeeded,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> NoSource,
    bool                  UserStopped,
    int                   TotalCount);