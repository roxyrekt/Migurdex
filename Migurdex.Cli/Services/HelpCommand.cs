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

    public static bool IsHelpToken(string arg)
    {
        return arg.Equals("--help", StringComparison.OrdinalIgnoreCase)
               || arg.Equals("-h", StringComparison.OrdinalIgnoreCase)
               || arg.Equals("help", StringComparison.OrdinalIgnoreCase);
    }

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
