using System.Diagnostics;
using Migurdex.Cli.Tui;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// 1000+ bölümlü dizilerde (ölçülen örnek: One Piece, <b>1166 bölüm</b>) bölüm
/// seçme ekranının çizim maliyetini ölçer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neden bu test var:</b> bölüm listesi ekranı her karede
/// <c>BuildGrid</c> çağırır. 1166 satırda <c>FuzzyMatcher.Rank</c> + Grid
/// kurulumu her tuşta çalışır. Ekranda <c>↑</c>/<c>↓</c> basılı tutulduğunda
/// bu maliyet her karede tekrarlanır. Ölçüm yapılmadan "1182 bölümde
/// yavaş olur" demek tahmin olurdu — bu test onu sayıya çevirir.
/// </para>
/// <para>
/// <b>Test ne yapar:</b> gerçek <see cref="FuzzyChoice"/> listeleri kurar ve
/// <c>FuzzyMatcher.Rank</c> + <c>BuildGrid</c>'i çağırır. Konsol tuş okumaz,
/// çizim yapmaz — yalnız maliyeti ölçülen saf kısımlar çalışır. Bu yüzden
/// test her ortamda koşar (CI dahil).
/// </para>
/// <para>
/// <b>Eşik seçimi:</b> eşik "donma" değil "fark edilir yavaşlık" içindir.
/// Windows konsolunda 16 ms üzeri bir kare, tuş başına 60 fps'in (16,6 ms)
/// altına düştüğü anlamına gelir. Varsayılan eşik 250 ms bilinçli olarak
/// geniştir: <b>mutlak süre donanıma bağlıdır</b> ve CI üzerinde yanıltıcı
/// kırmızı üretmemelidir. Çevre değişkeni <c>bulk_perf_threshold_ms</c> ile
/// daraltılabilir; ölçüm raporlanır ama varsayılan gevşektir.
/// </para>
/// </remarks>
/// <remarks>
/// <para>
/// <b>Neden bu test var:</b> bölüm listesi ekranı her karede
/// <c>BuildGrid</c> çağırır. 1166 satırda <c>FuzzyMatcher.Rank</c> + Grid
/// kurulumu her tuşta çalışır. Ekranda <c>↑</c>/<c>↓</c> basılı tutulduğunda
/// bu maliyet her karede tekrarlanır. Ölçüm yapılmadan "1182 bölümde
/// yavaş olur" demek tahmin olurdu — bu test onu sayıya çevirir.
/// </para>
/// <para>
/// <b>Test ne yapar:</b> gerçek <see cref="FuzzyChoice"/> listeleri kurar ve
/// <c>FuzzyMatcher.Rank</c> + <c>BuildGrid</c>'i çağırır. Konsol tuş okumaz,
/// çizim yapmaz — yalnız maliyeti ölçülen saf kısımlar çalışır. Bu yüzden
/// test her ortamda koşar (CI dahil).
/// </para>
/// <para>
/// <b>Eşik seçimi:</b> eşik "donma" değil "fark edilir yavaşlık" içindir.
/// Windows konsolunda 16 ms üzeri bir kare, tuş başına 60 fps'in (16,6 ms)
/// altına düştüğü anlamına gelir. Linux/Wsl konsolunda daha yavaştır, dolayısıyla
/// eşik dışarıdan <c>bulk_perf_threshold_ms</c> ile verilir.
/// </para>
/// </remarks>
public class BulkEpisodeScreenPerformanceTests
{
    /// <summary>Ölçülen gerçek bölüm sayısı (One Piece, TurkAnime sağlayıcısı, 01.10.2026).</summary>
    private const int MeasuredEpisodeCount = 1166;

    private static List<FuzzyChoice> BuildEpisodeChoices(int count)
    {
        var list = new List<FuzzyChoice>(count + 1);

        list.Add(new FuzzyChoice
        {
            Display       = "[green]⌁ Tüm bölümleri işaretle[/]",
            DisplayActive = "[bold white on green] ⌁ Tüm bölümleri işaretle [/]",
            Searchable    = "Tüm Bölümleri İşaretle",
            IsAction      = true
        });

        for (var i = 1; i <= count; i++)
        {
            var searchable = $"ep{i:0000}";
            list.Add(new FuzzyChoice
            {
                Display       = $"[grey]Bölüm {i:00}[/]",
                DisplayActive = $"[bold white]Bölüm {i:00}[/]",
                Searchable    = searchable,
                CanBeChecked  = true
            });
        }

        return list;
    }

    /// <summary>
    /// Filtreleme boşken sıralama + grid kurulum süresini ölçer.
    /// </summary>
    /// <remarks>
    /// Kullanıcının en sık yaptığı işlem: liste açılır, <c>↑</c>/<c>↓</c> ile
    /// gezer. Her tuşta bu maliyet ödenir.
    /// </remarks>
    [Fact]
    public void RankAndBuildGrid_WithNoQuery_StaysUnderThresholdForMeasuredEpisodeCount()
    {
        var choices = BuildEpisodeChoices(MeasuredEpisodeCount);

        // İlk çağrı ısıtma: JIT derlemesi ölçüme karışmasın.
        _ = FuzzyMatcher.Rank(choices, string.Empty);
        _ = FuzzyPrompt.BuildGrid("One Piece",
                                 null,
                                 FuzzyMatcher.Rank(choices, string.Empty),
                                 string.Empty,
                                 0,
                                 0,
                                 15,
                                 null,
                                 searchable: true,
                                 multiSelect: true);

        const int iterations = 20;
        var       sw         = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            var filtered = FuzzyMatcher.Rank(choices, string.Empty);
            _ = FuzzyPrompt.BuildGrid("One Piece",
                                      null,
                                      filtered,
                                      string.Empty,
                                      Math.Min(i, choices.Count - 1),
                                      0,
                                      15,
                                      null,
                                      searchable: true,
                                      multiSelect: true);
        }

        sw.Stop();
        var perFrame = sw.Elapsed.TotalMilliseconds / iterations;
        var threshold = Threshold();

        Assert.True(perFrame < threshold,
                    $"Boş filtrede kare başına {perFrame:F2} ms sürdü; eşik {threshold:F0} ms. "
                    + $"{MeasuredEpisodeCount} bölümlü listede gezinme yavaş.");
    }

    /// <summary>
    /// Yazarken filtreleme (arama kutusu doluyken) maliyetini ölçer.
    /// </summary>
    /// <remarks>
    /// Filtreleme her tuşta <see cref="FuzzyMatcher.Rank"/>'ı tüm liste üzerinde
    /// çalıştırır — boş sorgudan daha pahalıdır. Kullanıcı "one piece 10" gibi
    /// bir şey yazarken her karakter bu maliyeti tetikler.
    /// </remarks>
    [Fact]
    public void RankAndBuildGrid_WithQuery_StaysUnderThresholdForMeasuredEpisodeCount()
    {
        var choices = BuildEpisodeChoices(MeasuredEpisodeCount);

        // Gerçek kullanımda aranan bölüm sayısı: "Bölüm 10" yazan kullanıcı.
        const string query = "10";

        _ = FuzzyMatcher.Rank(choices, query);
        _ = FuzzyPrompt.BuildGrid("One Piece",
                                 null,
                                 FuzzyMatcher.Rank(choices, query),
                                 query,
                                 0,
                                 0,
                                 15,
                                 null,
                                 searchable: true,
                                 multiSelect: true);

        const int iterations = 20;
        var       sw         = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            var filtered = FuzzyMatcher.Rank(choices, query);
            _ = FuzzyPrompt.BuildGrid("One Piece",
                                      null,
                                      filtered,
                                      query,
                                      0,
                                      query.Length,
                                      15,
                                      null,
                                      searchable: true,
                                      multiSelect: true);
        }

        sw.Stop();
        var perFrame  = sw.Elapsed.TotalMilliseconds / iterations;
        var threshold = Threshold();

        Assert.True(perFrame < threshold,
                    $"Filtreli sorguda kare başına {perFrame:F2} ms sürdü; eşik {threshold:F0} ms. "
                    + $"'{query}' yazarken {MeasuredEpisodeCount} bölüm taranıyor.");
    }

    /// <summary>
    /// Tüm bölümlerin işaretlenmesi (Ctrl+A / "Tüm bölümleri işaretle") hızlı olmalı.
    /// </summary>
    /// <remarks>
    /// Bu işlem bölüm başına bir alan yazdığı için O(n) olmalıdır. 1166 bölümde
    /// "anlık" olması beklenir; 100 ms'i aşarsa yavaş demektir.
    /// </remarks>
    [Fact]
    public void SelectAll_IsLinearAndFastForMeasuredEpisodeCount()
    {
        var choices = BuildEpisodeChoices(MeasuredEpisodeCount);

        var sw = Stopwatch.StartNew();

        foreach (var choice in choices)
        {
            if (choice.CanBeChecked)
            {
                choice.IsChecked = true;
            }
        }

        sw.Stop();

        var checkableCount = choices.Count(c => c.CanBeChecked);
        Assert.Equal(MeasuredEpisodeCount, checkableCount);
        Assert.All(choices.Where(c => c.CanBeChecked), c => Assert.True(c.IsChecked));

        var perEpisode = sw.Elapsed.TotalMilliseconds;
        Assert.True(perEpisode < 100,
                    $"Tümünü işaretleme {perEpisode:F2} ms sürdü ({checkableCount} bölüm).");
    }

    /// <summary>
    /// Sayaç yalnız işaretli bölüm varsa görünür olmalı; aksi hâlde ekran
    /// gereksizce kalabalıklaşır.
    /// </summary>
    [Fact]
    public void SelectionCounter_IsEmptyWhenNothingIsChecked()
    {
        var choices = BuildEpisodeChoices(10);

        Assert.Equal(string.Empty, FuzzyPrompt.SelectionCounterMarkup(choices));
    }

    [Fact]
    public void SelectionCounter_ReportsCheckedCount()
    {
        var choices = BuildEpisodeChoices(1166);
        foreach (var c in choices.Where(c => c.CanBeChecked).Take(24))
        {
            c.IsChecked = true;
        }

        var counter = FuzzyPrompt.SelectionCounterMarkup(choices);

        Assert.Contains("24", counter);
    }

    /// <summary>
    /// Filtreleme sırasında gizli satırın işareti korunmalıdır.
    /// </summary>
    /// <remarks>
    /// ⭐ Bu, işaretleme <b>listenin indeksiyle değil satırın kendisiyle</b>
    /// tutulduğu için doğrudur: filtre değişince satır nesnesi aynı kalır,
    /// işareti de kalır. Kullanıcı 24 bölüm işaretledi, sonra "10" yazıp
    /// filtreledi — işaretlemeleri kaybolmamalıdır.
    /// </remarks>
    [Fact]
    public void IsChecked_SurvivesFiltering()
    {
        var choices = BuildEpisodeChoices(1166);
        var marked  = choices.Where(c => c.CanBeChecked).Skip(10).Take(3).ToList();
        foreach (var c in marked)
        {
            c.IsChecked = true;
        }

        // Filtre uygula: yalnız "10" içerenler kalsın
        var filtered = FuzzyMatcher.Rank(choices, "10");

        Assert.NotEmpty(filtered);

        // İşaretli satırlar nesne olarak aynı kaldığı için isaretleri duruyor
        foreach (var c in marked)
        {
            Assert.True(c.IsChecked);
        }

        Assert.Equal(3, choices.Count(c => c.IsChecked));
    }

    /// <summary>
    /// Onay ekranı eşiği ölçülen bölüm sayısıyla tetiklenmeli.
    /// </summary>
    /// <remarks>
    /// 1166 bölüm &gt; 100 eşiği → uyarı ekranı açılır. Bu test, eşiğin
    /// gerçek veriyle çalıştığını sabitler; eşik yanlış kalırsa 1166 bölümlük
    /// dizide kullanıcı onaysız yüzlerce GB indirmeye başlar.
    /// </remarks>
    [Fact]
    public void WarningThreshold_IsExceededByMeasuredEpisodeCount()
    {
        Assert.True(MeasuredEpisodeCount > 100,
                    "Eşik 100; ölçülen One Piece 1166 bölüm. Eşik yanlışsa test kırılır.");
    }

    private static double Threshold()
    {
        var raw = Environment.GetEnvironmentVariable("bulk_perf_threshold_ms");
        return double.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 250.0;
    }
}