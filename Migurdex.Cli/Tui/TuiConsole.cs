using Spectre.Console;

namespace Migurdex.Cli.Tui;

/// <summary>
/// TUI'nin tuş okuma/etkileşim denetimi için **tek** doğrulama noktası.
/// </summary>
/// <remarks>
/// <para>
/// <c>Console.KeyAvailable</c> ve <c>Console.ReadKey</c> stdin bir dosyadan/borudan
/// yönlendirildiğinde <see cref="InvalidOperationException"/> fırlatır
/// ("Cannot read keys when input is redirected from a file or pipe"). Bu iki çağrı
/// TUI içinde doğrudan kullanılırsa non-TTY ortamda ham stack trace çıkar.
/// </para>
/// <para>
/// Bu sınıf iki katmandan oluşur:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Soyut katman</b> — <see cref="IsInteractive"/> ve <see cref="IsInputInteractive"/>
///     saf fonksiyonlardır. <see cref="Console"/> statik olduğu için test içinde gerçek
///     yönlendirme koşulu değiştirilemez; bu yüzden her iki dal da bu fonksiyonlar
///     üzerinden test edilir.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Gerçek katman</b> — <see cref="Interactive"/>, <see cref="KeysReadable"/> ve
///     asıl okuma noktası olan <see cref="TryReadKey"/>. <see cref="TryReadKey"/>
///     hiçbir koşulda istisna fırlatmaz.
///     </description>
///   </item>
/// </list>
/// </remarks>
internal static class TuiConsole
{
    public static CancellationToken AppToken => TuiApplicationCancellation.Token;

    public static bool IsAppExitRequested => TuiApplicationCancellation.Token.IsCancellationRequested;

    // ---------------------------------------------------------------- soyut katman

    /// <summary>
    /// Tuş okunabilir mi? <c>Console.KeyAvailable</c>/<c>ReadKey</c> yalnızca stdin bir
    /// terminal ise çalışır. <b>stdout</b> yönlendirilmiş olması tuş okumayı bozmaz;
    /// bu yüzden ilgili kural stdin'e bağlıdır.
    /// </summary>
    public static bool IsInputInteractive(bool isInputRedirected) => !isInputRedirected;

    /// <summary>
    /// TUI tam etkileşimli mi? Bir "seçim yap" ekranı için hem stdin hem stdout bir
    /// terminal olmalıdır: stdout yönlendirilmişken canlı ekran kullanıcıya hiç
    /// görünmez, döngü de görünmez bir kuyrukta sonsuza kadar döner.
    /// </summary>
    public static bool IsInteractive(bool isInputRedirected, bool isOutputRedirected)
        => !isInputRedirected && !isOutputRedirected;

    /// <summary>Etkileşimsiz ortamda döngünün takılıp kalmaması için nedeni düz metin olarak verir.</summary>
    internal static string DescribeRedirect(bool isInputRedirected, bool isOutputRedirected)
    {
        if (isInputRedirected && isOutputRedirected)
        {
            return "stdin/stdout yönlendirilmiş";
        }

        return isInputRedirected ? "stdin yönlendirilmiş" : "stdout yönlendirilmiş";
    }

    // ------------------------------------------------------- gerçek konsol durumu

    /// <inheritdoc cref="IsInteractive(bool,bool)"/>
    public static bool Interactive => IsInteractive(Console.IsInputRedirected, Console.IsOutputRedirected);

    /// <inheritdoc cref="IsInputInteractive(bool)"/>
    public static bool KeysReadable => IsInputInteractive(Console.IsInputRedirected);

    // ------------------------------------------------------------ güvenli tuş okuma

    /// <summary>
    /// Tuş sırası varsa okur. Yönlendirilmiş stdin, kapalı konsol ya da desteklenmeyen
    /// platformda <c>KeyAvailable</c>/<c>ReadKey</c> fırlatabildiği için burada yutulur;
    /// çağıran taraf her koşulda güvenle "tuş yok" yanıtını alabilmelidir.
    /// </summary>
    public static bool TryReadKey(out ConsoleKeyInfo key)
    {
        key = default;

        if (!KeysReadable)
        {
            return false;
        }

        try
        {
            if (!Console.KeyAvailable)
            {
                return false;
            }

            key = Console.ReadKey(true);
            return true;
        }
        catch (InvalidOperationException)
        {
            // stdin yönlendirilmiş ya da konsol yok.
            return false;
        }
        catch (IOException)
        {
            // stdin kapatılmış.
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Kuyrukta bekleyen tuşları tüketir. Önceki ekrandan kalan tuşların yeni istemi
    /// tetiklemesini engeller. Etkileşimsiz ortamda doğal olarak hiçbir şey yapmaz.
    /// </summary>
    public static void DrainPendingKeys()
    {
        while (TryReadKey(out _))
        {
            // yalnızca tüketilir
        }
    }

    /// <summary>
    /// Bir tuşa basılana kadar bekler; iptal edilirse <c>false</c> döner.
    /// Etkileşim yokken "Devam etmek için bir tuşa basın..." beklemenin anlamı olmadığı
    /// için beklemeden <c>false</c> döner — çağıran bunu "ekranı kapat" olarak yorumlar.
    /// </summary>
    public static bool WaitForKey()
    {
        if (!KeysReadable)
        {
            return false;
        }

        while (true)
        {
            if (TryReadKey(out _))
            {
                return true;
            }

            if (TuiApplicationCancellation.Token.IsCancellationRequested)
            {
                return false;
            }

            Thread.Sleep(50);
        }
    }

    // ------------------------------------------------------------------- bildirim

    /// <summary>
    /// Etkileşimli olmayan ortamda açılamayan ekranlar için kullanıcıya anlamlı mesaj yazar.
    /// </summary>
    public static void ReportNonInteractive(string step)
    {
        AnsiConsole.MarkupLine(
            $"[yellow][[!]] Etkileşimli terminal yok "
            + $"({DescribeRedirect(Console.IsInputRedirected, Console.IsOutputRedirected)}); "
            + $"\"{Markup.Escape(step)}\" ekranı açılmadı.[/]");
        AnsiConsole.MarkupLine(
            "[grey]Yönlendirilebilir kullanım için: migurdex --help[/]");
    }
}
