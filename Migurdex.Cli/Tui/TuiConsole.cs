namespace Migurdex.Cli.Tui;

internal static class TuiConsole
{
    public static CancellationToken AppToken => TuiApplicationCancellation.Token;

    public static bool IsAppExitRequested => TuiApplicationCancellation.Token.IsCancellationRequested;

    /// <summary>
    /// Terminalin görünür satır sayısı. Konsol yoksa/ölçülemiyorsa makul bir varsayılan döner.
    /// Ekranlar çerçeveyi buna göre kısaltır; aksi hâlde kısa terminalde içerik taşar, terminal
    /// kayar ve başlık yukarı kaçar (altta da eski karelerden kalıntı kalır).
    /// </summary>
    public static int WindowHeight
    {
        get
        {
            try
            {
                var height = Console.WindowHeight;
                return height > 0 ? height : DefaultWindowHeight;
            }
            catch
            {
                return DefaultWindowHeight;
            }
        }
    }

    /// <summary>
    /// Terminalin görünür sütun sayısı. Uzun satırlar sarmasın diye ekranlar metni buna göre
    /// kırpar; sarma olursa bir kayıt iki fiziksel satıra düşer ve canlı bölge pencereden taşar.
    /// </summary>
    public static int WindowWidth
    {
        get
        {
            try
            {
                var width = Console.WindowWidth;
                return width > 0 ? width : DefaultWindowWidth;
            }
            catch
            {
                return DefaultWindowWidth;
            }
        }
    }

    private const int DefaultWindowHeight = 40;

    private const int DefaultWindowWidth = 110;

    public static bool WaitForKey()
    {
        while (!Console.KeyAvailable)
        {
            if (TuiApplicationCancellation.Token.IsCancellationRequested)
            {
                return false;
            }

            Thread.Sleep(50);
        }

        Console.ReadKey(true);
        return true;
    }
}
