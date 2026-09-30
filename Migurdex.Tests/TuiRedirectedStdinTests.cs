using Migurdex.Cli.Tui;
using Spectre.Console;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
///     BULGU 11 / C13 — TUI, <c>Console.KeyAvailable</c> / <c>Console.ReadKey</c> çağrılarını
///     yönlendirilmiş stdin'e karşı denetlemeden yapıyordu. Pipe/NUL dosyadan beslenen
///     non-TTY ortamda bu çağrılar <c>InvalidOperationException</c> fırlatıyor ve ham stack
///     trace basıyordu ("Cannot see if a key has been pressed when ... console input has
///     been redirected from a file").
/// </summary>
/// <remarks>
///     <para>
///         <c>Console</c> statik olduğu için test içinde gerçek yönlendirme koşulu
///         değiştirilemez. Bu yüzden karar mantığı <see cref="TuiConsole.IsInteractive" /> ve
///         <see cref="TuiConsole.IsInputInteractive" /> gibi **saf** fonksiyonlara taşındı; her iki
///         dal (TTY / yönlendirilmiş) bu fonksiyonlar üzerinden test edilir.
///     </para>
///     <para>
///         Windows CI'da testler konsola bağlı değildir, dolayısıyla
///         <c>Console.IsInputRedirected</c> pratikte <c>true</c>'dur ve "TUI gerçekten açılmıyor"
///         davranış testleri burada koşar. Gerçek TTY dalı CI'da koşmaz; o dalın değişmediği
///         kod incelemesiyle gerekçelendirilir (bkz. <see cref="NoRawKeyReadsOutsideTheChokePoint" />
///         ve <see cref="EveryInteractiveScreenRoutesThroughTuiConsole" />).
///     </para>
/// </remarks>
public sealed class TuiRedirectedStdinTests
{
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(20);

    // ------------------------------------------------------------------ soyut katman

    /// <summary>
    ///     Yönlendirilmiş stdin altında TUI'nin açılmaması gerektiğinin tam doğruluk tablosu.
    ///     Etkileşim için **her iki** akışın da bir terminal olması şart: stdout yönlendirilmişken
    ///     canlı ekran görünmez, "seçim bekle" döngüsü de görünmez bir kuyrukta döner.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]  // gerçek TTY: TUI açılır, davranış eskisi gibi
    [InlineData(true, false, false)]  // stdin pipe/NUL: KeyAvailable fırlatırdı
    [InlineData(false, true, false)]  // stdout dosya: ekran görünmez, yine de döngü sonsuz
    [InlineData(true, true, false)]   // her ikisi de yönlendirilmiş (CI, < NUL 2>&1)
    public void IsInteractive_RequiresBothStreamsToBeATerminal(bool isInputRedirected,
                                                                bool isOutputRedirected,
                                                                bool expected)
    {
        Assert.Equal(expected,
                     TuiConsole.IsInteractive(isInputRedirected, isOutputRedirected));
    }

    /// <summary>
    ///     Tuş okunabilirliği yalnızca stdin'e bağlıdır: stdout yönlendirilmiş olsa bile
    ///     <c>Console.KeyAvailable</c>/<c>ReadKey</c> çalışır. Bu yüzden ilerleme/indirme gibi
    ///     "Esc ile iptal" döngüleri bu daha gevşek kuralı kullanır — onları sıkı kurala
    ///     bağlasak Esc iptali sessizce kaybolurdu.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IsInputInteractive_DependsOnStdinOnly(bool isInputRedirected, bool expected)
    {
        Assert.Equal(expected, TuiConsole.IsInputInteractive(isInputRedirected));
    }

    /// <summary>
    ///     Kullanıcıya hangi akışın yönlendirildiğini söyleyen metin; mesajın anlamlı olması için
    ///     üç dalı da ayrı ayrı doğrulanır.
    /// </summary>
    [Theory]
    [InlineData(true, true, "stdin/stdout yönlendirilmiş")]
    [InlineData(true, false, "stdin yönlendirilmiş")]
    [InlineData(false, true, "stdout yönlendirilmiş")]
    public void DescribeRedirect_NamesTheOffendingStream(bool isInputRedirected,
                                                          bool isOutputRedirected,
                                                          string expected)
    {
        Assert.Equal(expected,
                     TuiConsole.DescribeRedirect(isInputRedirected, isOutputRedirected));
    }

    /// <summary>
    ///     Sıkı kural (etkileşim) gevşek kuralı (tuş okunabilir) asla gevşetmez. Bu yüzden bir
    ///     "seçim ekranı" için <c>IsInteractive</c>'ı kullanmak güvenlidir: <c>TryReadKey</c>'in
    ///     fırlatabileceği her koşulda o ekran zaten hiç açılmaz.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InteractiveImpliesKeysAreReadable(bool isInputRedirected, bool isOutputRedirected)
    {
        if (TuiConsole.IsInteractive(isInputRedirected, isOutputRedirected))
        {
            Assert.True(TuiConsole.IsInputInteractive(isInputRedirected));
        }
    }

    // -------------------------------------------------- canlı yardımcı (Console durumu)

    /// <summary>
    ///     Statik yardımcıların, <see cref="Console" />'un gerçek durumundan türetilmiş halleri
    ///     saf sürümlerle çelişmemeli — aksi halde iki katman sessizce ayrışır.
    /// </summary>
    [Fact]
    public void AmbientHelpers_AgreeWithThePureFunctions()
    {
        Assert.Equal(TuiConsole.IsInteractive(Console.IsInputRedirected, Console.IsOutputRedirected),
                     TuiConsole.Interactive);
        Assert.Equal(TuiConsole.IsInputInteractive(Console.IsInputRedirected), TuiConsole.KeysReadable);
    }

    /// <summary>
    ///     <see cref="TuiConsole.TryReadKey" /> tek okuma noktasıdır ve hiçbir koşulda istisna
    ///     fırlatmamalıdır. Yönlendirilmiş stdin'de "tuş yok" döner ve çağıran döngüsüne devam eder.
    /// </summary>
    [Fact]
    public void TryReadKey_NeverThrows()
    {
        var read = RunWithTimeout(() =>
        {
            Assert.False(TuiConsole.TryReadKey(out var key));
            Assert.Equal(default, key);
            return true;
        },
                                PromptTimeout,
                                nameof(TuiConsole.TryReadKey));

        Assert.True(read);
    }

    /// <summary>
    ///     Bekleyen tuşları tüketen yardımcı da yönlendirilmiş stdin'de güvenli bir no-op olmalı.
    /// </summary>
    [Fact]
    public void DrainPendingKeys_NeverThrows()
    {
        RunWithTimeout(() =>
                      {
                          TuiConsole.DrainPendingKeys();
                          return true;
                      },
                      PromptTimeout,
                      nameof(TuiConsole.DrainPendingKeys));
    }

    /// <summary>
    ///     <see cref="TuiConsole.WaitForKey" />, tuş okunamazken beklemek yerine hemen
    ///     <c>false</c> döner: "Devam etmek için bir tuşa basın..." cümlesi etkileşim yokken
    ///     sonsuza kadar bekletirdi. Çağıran bunu "ekranı kapat" olarak yorumlar.
    /// </summary>
    [Fact]
    public void WaitForKey_ReturnsFalseImmediately_WhenKeysAreNotReadable()
    {
        Assert.SkipWhen(TuiConsole.KeysReadable,
            "Gerçek terminalde WaitForKey tuş bekler; yalnızca yönlendirilmiş stdin dalı ölçülebilir.");

        var waited = RunWithTimeout(() => TuiConsole.WaitForKey(), PromptTimeout, nameof(TuiConsole.WaitForKey));

        Assert.False(waited);
    }

    // --------------------------------- davranış: yönlendirilmiş stdin'de çökmez, takılmaz

    /// <summary>
    ///     <c>FuzzyPrompt.Show</c> — asıl çökme noktası. Yönlendirilmiş stdin'de canlı ekran
    ///     hiç açılmaz, seçim yapılmaz ve <c>InvalidOperationException</c> fırlatmaz.
    ///     Zaman aşımı, "çökmedi ama sonsuz döngüye girdi" regresyonunu da yakalar.
    /// </summary>
    [Fact]
    public void FuzzyPromptShow_ReturnsNull_WhenNotInteractive()
    {
        SkipWhenInteractive(nameof(FuzzyPrompt.Show));

        var result = RunWithTimeout(() => FuzzyPrompt.Show("Fuzzy testi", [Theme.MenuItem("Tek")]),
                                    PromptTimeout,
                                    nameof(FuzzyPrompt.Show));

        Assert.Null(result);
    }

    /// <summary>
    ///     Akış taramalı istem de aynı kapıdan geçmeli; yoksa arka plandaki tarama iptal
    ///     edilmeden sonsuza kadar dönerdi.
    /// </summary>
    [Fact]
    public void FuzzyPromptShowDynamic_ReturnsEmptyResult_WhenNotInteractive()
    {
        SkipWhenInteractive(nameof(FuzzyPrompt.ShowDynamic));

        var result = RunWithTimeout(
            () => FuzzyPrompt.ShowDynamic("Dinamik testi", EmptyStream(), _ => [], Theme.BackChoice("Vazgeç")),
            PromptTimeout,
            nameof(FuzzyPrompt.ShowDynamic));

        Assert.Null(result.Selection);
        Assert.Empty(result.AccumulatedItems);
    }

    /// <summary>
    ///     Sıralama istemi yalnızca klavye girişiyle anlam taşıyor; etkileşim yoksa
    ///     kullanıcının sıralaması değiştirilmeden <c>null</c> döner.
    /// </summary>
    [Fact]
    public void ReorderPromptShow_ReturnsNull_WhenNotInteractive()
    {
        SkipWhenInteractive(nameof(ReorderPrompt.Show));

        var result = RunWithTimeout(
            () => ReorderPrompt.Show("Sıralama testi",
                                     [
                                         new ReorderItem
                                         {
                                             Key = "a", DisplayName = "A"
                                         },
                                         new ReorderItem
                                         {
                                             Key = "b", DisplayName = "B"
                                         }
                                     ]),
            PromptTimeout,
            nameof(ReorderPrompt.Show));

        Assert.Null(result);
    }

    /// <summary>
    ///     Kullanıcı yalnızca "stack trace yok" değil, **neden** olmadığını da görmeli:
    ///     hangi ekran açılamadı söylenmeli ve yönlendirilebilir bir alternatif gösterilmeli.
    /// </summary>
    [Fact]
    public void FuzzyPromptShow_ExplainsWhyTheScreenCouldNotOpen()
    {
        SkipWhenInteractive(nameof(FuzzyPrompt.Show));

        var writer   = new StringWriter();
        var previous = AnsiConsole.Console;
        try
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                Out  = new AnsiConsoleOutput(writer)
            });
            console.Profile.Width = 400;
            AnsiConsole.Console = console;

            RunWithTimeout(() => FuzzyPrompt.Show("Yönlendirme testi", [Theme.MenuItem("Tek")]),
                           PromptTimeout,
                           nameof(FuzzyPrompt.Show));
        }
        finally
        {
            AnsiConsole.Console = previous;
        }

        var text = writer.ToString();
        Assert.Contains("Etkileşimli terminal yok", text);
        Assert.Contains("Yönlendirme testi", text);
        Assert.Contains("--help", text);
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("   at ", text);
    }

    // --------------------------------------- kaynak taraması: her çağrı noktası kapsanmış

    /// <summary>
    ///     Regresyon kalkanı: TUI ağacında <c>Console.KeyAvailable</c> / <c>Console.ReadKey</c>
    ///     çağrısı yalnızca <c>TuiConsole.TryReadKey</c> içinde bulunabilir. Yeni bir ekran
    ///     korumasız <c>KeyAvailable</c> eklerse bu test kırılır.
    /// </summary>
    [Fact]
    public void NoRawKeyReadsOutsideTheChokePoint()
    {
        var tuiDir = FindTuiSourceDirectory();
        Assert.SkipWhen(tuiDir is null, "Migurdex kaynak ağacı bulunamadı; tarama atlandı.");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(tuiDir!, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(tuiDir!, file);
            if (relative.Equals("TuiConsole.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines   = File.ReadAllLines(file);
            var inBlock = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComments(lines[i], ref inBlock);
                if (code.Contains("Console.KeyAvailable") || code.Contains("Console.ReadKey"))
                {
                    offenders.Add($"{relative}:{i + 1} → {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
                    "Yönlendirilmiş stdin'de istisna fırlatacak korumasız tuş okuma çağrısı var. "
                    + "Bunlar TuiConsole.TryReadKey üzerinden yapılmalı:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     "Tek doğrulama noktası" kuralının kaynak tarafındaki karşılığı: TUI ağacında
    ///     <c>Console.IsInputRedirected</c> yalnızca <c>TuiConsole</c> içinde okunabilir, her
    ///     ekran kendi kopyasını yazmamalıdır.
    /// </summary>
    [Fact]
    public void RedirectStateIsReadOnlyInsideTuiConsole()
    {
        var tuiDir = FindTuiSourceDirectory();
        Assert.SkipWhen(tuiDir is null, "Migurdex kaynak ağacı bulunamadı; tarama atlandı.");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(tuiDir!, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(tuiDir!, file);
            if (relative.Equals("TuiConsole.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines   = File.ReadAllLines(file);
            var inBlock = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComments(lines[i], ref inBlock);
                if (code.Contains("Console.IsInputRedirected") || code.Contains("Console.IsOutputRedirected"))
                {
                    offenders.Add($"{relative}:{i + 1} → {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
                    "Yönlendirme durumu TuiConsole dışında doğrudan okunmamalı:\n  "
                    + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     Tuş okuyan her TUI ekranı ya <c>TuiConsole.Interactive</c> kapısından geçmeli ya da
    ///     döngüsünde yalnızca <c>TuiConsole.TryReadKey</c> kullanmalıdır. Yarım düzeltme
    ///     (sadece FuzzyPrompt'un kapatılması) bu listedeki bir dosyayı sessizce boşta bırakırdı.
    /// </summary>
    [Theory]
    // Ayraç olarak düz `/` kullanılır: `Path.Combine` Linux'ta `\`'i ayraç saymaz,
    // `Views\X.cs` diye bir dosya adı üretir ve tarama tutmaz (Linux CI'da ölçüldü:
    // `Beklenen TUI dosyası yok: Views\EpisodeSourcesView.cs`). Windows da `/`
    // kabul ettiği için tek yazım iki platformda da geçerlidir.
    [InlineData("FuzzyPrompt.cs")]
    [InlineData("ReorderPrompt.cs")]
    [InlineData("SyncAmbiguityPrompt.cs")]
    [InlineData("Views/EpisodeSourcesView.cs")]
    [InlineData("Views/SettingsView.cs")]
    [InlineData("Views/MainMenuView.cs")]
    public void EveryInteractiveScreenRoutesThroughTuiConsole(string relativePath)
    {
        var tuiDir = FindTuiSourceDirectory();
        Assert.SkipWhen(tuiDir is null, "Migurdex kaynak ağacı bulunamadı; tarama atlandı.");

        // Bölücüyü işletim sistemi ayracına çevir.
        var full = Path.Combine(tuiDir!,
                                relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Beklenen TUI dosyası yok: {relativePath}");

        var source = File.ReadAllText(full);
        var guarded =
            source.Contains("TuiConsole.TryReadKey", StringComparison.Ordinal)
            || source.Contains("TuiConsole.Interactive", StringComparison.Ordinal)
            || source.Contains("TuiConsole.DrainPendingKeys", StringComparison.Ordinal)
            || source.Contains("TuiConsole.WaitForKey", StringComparison.Ordinal)
            // Ana menü doğrudan tuş okumaz; yalnızca korumalı FuzzyPrompt.Show çağırır.
            || source.Contains("FuzzyPrompt.Show", StringComparison.Ordinal);

        Assert.True(guarded, $"{relativePath} TuiConsole korumasından geçmiyor.");
    }

    // ------------------------------------------------------------------------ yardımcılar

    private static void SkipWhenInteractive(string member)
    {
        Assert.SkipWhen(TuiConsole.Interactive,
            $"{member} gerçek bir terminalde canlı ekran açıp tuş beklerdi; "
            + "bu test yalnızca yönlendirilmiş stdin/stdout dalında anlamlıdır.");
    }

    private static T RunWithTimeout<T>(Func<T> action, TimeSpan timeout, string what)
    {
        var task = Task.Run(action);
        if (!task.Wait(timeout))
        {
            Assert.Fail($"{what} {timeout.TotalSeconds:F0} saniye içinde dönmedi — "
                        + "muhtemelen etkileşimsiz ortamda sonsuz döngüye girildi.");
        }

        return task.Result;
    }

    private static async IAsyncEnumerable<int> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static string? FindTuiSourceDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable("MIGURDEX_TUI_SOURCE");
        if (!string.IsNullOrWhiteSpace(overridden) && Directory.Exists(overridden))
        {
            return overridden;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Migurdex.slnx")))
            {
                var tui = Path.Combine(directory.FullName, "Migurdex.Cli", "Tui");
                if (Directory.Exists(tui))
                {
                    return tui;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    ///     Satırdaki yorumları keser; kaynak taraması yorum satırlarını güncel kod sanmasın.
    /// </summary>
    private static string StripComments(string line, ref bool inBlockComment)
    {
        var builder = new System.Text.StringBuilder(line.Length);

        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            {
                break;
            }

            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            builder.Append(line[i]);
        }

        return builder.ToString();
    }
}
