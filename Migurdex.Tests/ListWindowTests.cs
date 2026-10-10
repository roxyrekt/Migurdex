using Migurdex.Cli.Tui;
using Migurdex.Cli.Tui.Views;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Kullanıcı raporu: kısa terminalde ayarlar listesi pencereden taşıyor, terminal kayıyor ve
/// ekranın üstünde önceki karenin kalıntısı kalıyordu ("... kalma durumu"). Pencereleme
/// matematiği bu yüzden saf fonksiyona çıkarıldı; bu testler "içerik pencereye sığar ve imleç
/// her zaman görünür" sözünü doğrular.
/// </summary>
public sealed class ListWindowTests
{
    [Theory]
    [InlineData(40, SettingsView.ChromeRows, int.MaxValue, 33)]
    [InlineData(30, SettingsView.ChromeRows, 31, 23)]
    [InlineData(40, BatchDownloadView.GridChromeRows, 12, 12)]
    [InlineData(20, BatchDownloadView.GridChromeRows, 12, 9)]
    [InlineData(12, BatchDownloadView.GridChromeRows, 12, 1)]
    [InlineData(6, SettingsView.ChromeRows, 31, 1)]
    [InlineData(0, SettingsView.ChromeRows, 31, 1)]
    [InlineData(1, 1, 1, 1)]
    public void Budget_ReservesChromeAndOverflowSlack(int windowHeight, int chromeRows, int maxRows, int expected)
    {
        Assert.Equal(expected, ListWindow.Budget(windowHeight, chromeRows, maxRows));
    }

    [Fact]
    public void SettingsScreen_ContentNeverFillsTheLastRowsOfTheWindow()
    {
        // Ayarlar ekranının sabit çerçevesi 5 satır (başlık+boşluk, tablo önü, tablo sonu, ipucu).
        for (var height = SettingsView.ChromeRows + 3; height <= 80; height++)
        {
            var budget = ListWindow.Budget(height, SettingsView.ChromeRows, int.MaxValue);
            Assert.True(budget + SettingsView.ChromeRows <= height - 2,
                        $"yükseklik {height}: {budget} satır + {SettingsView.ChromeRows} çerçeve pencereyi dolduruyor");
        }
    }

    [Fact]
    public void BatchScreen_ContentNeverFillsTheLastRowsOfTheWindow()
    {
        for (var height = BatchDownloadView.GridChromeRows + 3; height <= 80; height++)
        {
            var budget = ListWindow.Budget(height, BatchDownloadView.GridChromeRows, 12);
            Assert.True(budget + BatchDownloadView.GridChromeRows <= height - 2,
                        $"yükseklik {height}: {budget} satır + {BatchDownloadView.GridChromeRows} çerçeve pencereyi dolduruyor");
        }
    }

    [Fact]
    public void EpisodePrompt_KeepsRequestedPageSizeOnTallTerminals()
    {
        // 30 satır: 6 çerçeve + 2 başlık + 15 satır = 23 satır → istenen sayfa boyu korunur.
        Assert.Equal(15, ListWindow.Budget(30, 8, 15));

        // 16 satır: 15 satır sığmaz, sayfa pencereye indirilir.
        Assert.Equal(6, ListWindow.Budget(16, 8, 15));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(23)]
    [InlineData(31)]
    [InlineData(100)]
    public void Compute_AlwaysKeepsTheCursorInsideTheWindow(int budget)
    {
        const int count = 31;
        for (var cursor = 0; cursor < count; cursor++)
        {
            var (start, end) = ListWindow.Compute(count, cursor, budget);

            Assert.True(start >= 0 && end <= count, $"pencere taştı: [{start}, {end}) / {count}");
            Assert.True(end > start, $"boş pencere: [{start}, {end})");
            Assert.True(cursor >= start && cursor < end,
                        $"imleç görünmüyor: imleç {cursor}, pencere [{start}, {end})");
            Assert.True(end - start <= Math.Min(budget, count),
                        $"pencere bütçeyi aştı: {end - start} > {budget}");
        }
    }

    [Fact]
    public void Compute_NeverStartsOnAnOrphanedRowBelowASectionHeader()
    {
        const int count = 30;
        static bool IsSection(int index) => index % 10 == 0;

        for (var budget = 1; budget <= 12; budget++)
        {
            for (var cursor = 0; cursor < count; cursor++)
            {
                var (start, end) = ListWindow.Compute(count, cursor, budget, IsSection);

                Assert.True(cursor >= start && cursor < end,
                            $"imleç görünmüyor: imleç {cursor}, pencere [{start}, {end})");
                Assert.False(start > 0 && IsSection(start - 1) && !IsSection(start),
                             $"başlık pencerenin dışında kaldı: pencere {start}'da başlıyor");
                // Bölüm başlığını içeri almak pencereyi en fazla bir satır büyütür.
                Assert.True(end - start <= Math.Min(budget, count) + 1,
                            $"pencere bütçeyi aştı: {end - start} > {budget} + 1");
            }
        }
    }

    [Fact]
    public void Compute_PullsTheSectionHeaderIntoTheWindow()
    {
        static bool IsSection(int index) => index % 10 == 0;

        // İmleç bir bölümün ilk seçeneğinde; pencere tam başlığın altından başlıyordu.
        var (start, end) = ListWindow.Compute(30, 11, 1, IsSection);

        Assert.Equal(10, start);
        Assert.True(11 >= start && 11 < end, $"imleç görünmüyor: [{start}, {end})");
    }

    [Fact]
    public void Compute_TopAndBottomCursorsShowTheEdgesOfTheList()
    {
        const int count = 31;

        var top = ListWindow.Compute(count, 0, 12);
        Assert.Equal(0, top.Start);

        var bottom = ListWindow.Compute(count, count - 1, 12);
        Assert.Equal(count, bottom.End);
        Assert.Equal(count - 12, bottom.Start);
    }

    [Fact]
    public void Compute_BudgetBeyondTheListShowsEverything()
    {
        var (start, end) = ListWindow.Compute(7, 3, 500);

        Assert.Equal(0, start);
        Assert.Equal(7, end);
    }

    [Fact]
    public void Compute_EmptyListReturnsAnEmptyWindow()
    {
        Assert.Equal((0, 0), ListWindow.Compute(0, 0, 12));
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(999, 30)]
    public void Compute_ClampsOutOfRangeCursor(int cursor, int expectedVisibleRow)
    {
        var (start, end) = ListWindow.Compute(31, cursor, 12);

        Assert.True(expectedVisibleRow >= start && expectedVisibleRow < end,
                    $"imleç görünmüyor: [{start}, {end})");
    }
}
