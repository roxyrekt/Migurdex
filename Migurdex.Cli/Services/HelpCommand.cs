namespace Migurdex.Cli.Services;

/// <summary>
/// Üst düzey yardım rotası. TUI'ye ihtiyaç duymaz: yalnızca stdout'a yazar,
/// böylece stdin/stdout yönlendirilmiş (TTY'siz) ortamlarda da çalışır.
/// </summary>
public static class HelpCommand
{
    private static readonly string[] DelegatedCommands =
    [
        "version",
        "update",
        "auth",
        "search",
        "play",
        "continue",
        "download"
    ];

    /// <summary>Argümanın üst düzey yardım bayrağı olup olmadığını döndürür.</summary>
    public static bool IsHelpToken(string arg)
    {
        return arg.Equals("--help", StringComparison.OrdinalIgnoreCase)
               || arg.Equals("-h", StringComparison.OrdinalIgnoreCase)
               || arg.Equals("help", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Yardım isteğinin TUI'ye düşmeden üst düzey komut yönlendirmesinde
    /// yakalanması gerekip gerekmediğini belirler. Alt komutlar kendi
    /// <c>--help</c> çıktısını ürettiği için ilk argüman bir komutsa bu rota devre dışı kalır.
    /// </summary>
    public static bool IsTopLevelRequest(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            return false;
        }

        if (IsHelpToken(args[0]))
        {
            return true;
        }

        if (DelegatedCommands.Any(command => args[0].Equals(command, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return args.Any(IsHelpToken);
    }

    /// <summary>Yardım metnini <paramref name="output"/>'a yazar ve başarı kodu döndürür.</summary>
    public static int Run(TextWriter? output = null)
    {
        PrintHelp(output ?? Console.Out);
        return 0;
    }

    public static void PrintHelp(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine("Kullanım:");
        output.WriteLine("  migurdex                       TUI'yi başlatır (etkileşimli terminal gerekir).");
        NonInteractiveCommand.WriteCommandLines(output);
        output.WriteLine("  migurdex --help                Bu yardım metnini yazar.");
        output.WriteLine();
        NonInteractiveCommand.WriteFlagLegend(output);
    }
}
