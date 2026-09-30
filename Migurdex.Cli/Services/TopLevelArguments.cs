namespace Migurdex.Cli.Services;

/// <summary>
/// Üst düzey argüman doğrulaması. <c>Program</c> bu noktaya geldiğinde sürüm, yardım ve
/// alt komutlar zaten ele alınmıştır; geriye yalnız TUI kalmıştır. TUI tek bir bayrağı
/// tanıdığı için listede olmayan her argüman kullanım hatasıdır (exit 2) — yoksa
/// yazım hatası sessizce yok sayılıp TUI açılır, TTY yoksa de stack trace basılır.
/// </summary>
public static class TopLevelArguments
{
    private static readonly string[] _tuiFlags = ["--no-update-check"];

    /// <summary>TUI'yi açarken kabul edilen bayraklar.</summary>
    public static IReadOnlyList<string> TuiFlags { get; } = Array.AsReadOnly(_tuiFlags);

    public static bool IsTuiFlag(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        return Array.Exists(_tuiFlags,
                            flag => flag.Equals(arg, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Argümanlar yalnız TUI bayraklarından oluşuyorsa <c>true</c>; aksi halde
    /// <paramref name="error"/> hatanın metnini taşır.
    /// </summary>
    public static bool TryValidate(string[] args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var unknown = Array.Find(args, arg => !IsTuiFlag(arg));
        if (unknown is null)
        {
            error = null;
            return true;
        }

        error = unknown.StartsWith('-') ? $"Bilinmeyen bayrak: {unknown}"
                                       : $"Bilinmeyen komut: {unknown}";
        return false;
    }

    /// <summary>Alt komutlarla aynı biçimde hata + kullanım yazar ve kullanım hatası kodu döner.</summary>
    public static int PrintUsageError(TextWriter error, string message)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(message);

        error.WriteLine($"Hata: {message}");
        error.WriteLine("Kullanım: migurdex --help");
        return 2;
    }
}
