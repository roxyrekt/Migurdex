using Migurdex.Shared.Models;
using System.Globalization;
using System.Text;

namespace Migurdex.Cli.Services.Downloads;

public sealed class DownloadPathBuilder : IDownloadPathBuilder
{
    public const int MaxComponentUtf8Bytes      = 200;
    public const int TemporarySuffixUtf8Bytes  = 96;

    private const string AnimeFallback   = "Anime";
    private const string EpisodeFallback = "Bölüm";
    private const int    MaxCodePoint    = 0x10FFFF;

    public static int MaxFullPathUtf8Bytes => OperatingSystem.IsWindows() ? 240 : 1024;

    public DownloadPath Build(
        string  rootDirectory,
        string  animeTitle,
        string  episodeTitle,
        int     season,
        double  episodeNumber,
        string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Çıktı dizini boş olamaz.", nameof(rootDirectory));
        }

        var root = Path.GetFullPath(rootDirectory);
        var normalizedExtension = NormalizeExtension(extension);
        var separatorBytes = Encoding.UTF8.GetByteCount(Path.DirectorySeparatorChar.ToString());
        var extensionBytes = Encoding.UTF8.GetByteCount(normalizedExtension ?? string.Empty);
        // C5: tam hedef yolu İKİ ayraç içeriyor (root -> anime dizini -> dosya
        // adı) ama ayraç maliyeti bütçeden yalnızca BİR kez düşülüyordu.
        // Hesap: anime + stem bütçesi `availableBytes` kadar olduğunda
        //   tam yol = root + ayraç + anime + ayraç + stem + uzantı
        //          = root + uzantı + 2*ayraç + availableBytes
        //          = Max + ayraç - TemporarySuffix
        // yani bütçe tam olarak 1 ayraç (1 UTF-8 baytı) eksik hesaplanıyordu
        // ve EnsureFullPathBudget tam dolu bir yolda yanlışlıkla patlıyordu
        // ("Tam çıktı yolu güvenli dosya adı bütçesini aşıyor").
        var availableBytes = MaxFullPathUtf8Bytes
                             - Encoding.UTF8.GetByteCount(root)
                             - separatorBytes
                             - separatorBytes
                             - extensionBytes
                             - TemporarySuffixUtf8Bytes;
        if (availableBytes < 48)
        {
            throw new ArgumentException("Çıktı dizini tam hedef yolu için çok uzun.", nameof(rootDirectory));
        }

        var anime = SanitizeComponent(animeTitle, AnimeFallback);
        var animeBudget = Math.Clamp(availableBytes * 2 / 5, 24, MaxComponentUtf8Bytes);
        anime = TruncateUtf8(anime, animeBudget);
        var fileBudget = availableBytes - Encoding.UTF8.GetByteCount(anime);
        if (fileBudget < 24)
        {
            animeBudget = Math.Max(16, animeBudget - (24 - fileBudget));
            anime       = TruncateUtf8(anime, animeBudget);
            fileBudget  = availableBytes - Encoding.UTF8.GetByteCount(anime);
        }

        if (fileBudget < 16)
        {
            throw new ArgumentException("Çıktı yolu tam hedef yolu için çok uzun.", nameof(rootDirectory));
        }

        var episode = SanitizeComponent(episodeTitle, EpisodeFallback);
        var animeDir = Path.Combine(root, anime);
        EnsureUnderRoot(root, animeDir);

        var seasonNumber = season < 1 ? 1 : season;
        var episodeText  = FormatEpisodeNumber(episodeNumber);
        var stem         = $"S{seasonNumber.ToString("00", CultureInfo.InvariantCulture)}E{episodeText}";

        if (!string.IsNullOrWhiteSpace(episodeTitle))
        {
            stem += " - " + episode;
        }

        stem = TruncateUtf8(SanitizeComponent(stem, "Episode"), fileBudget);
        var mediaPath = Path.Combine(animeDir, stem + (normalizedExtension ?? string.Empty));
        EnsureUnderRoot(root, mediaPath);
        EnsureFullPathBudget(mediaPath, TemporarySuffixUtf8Bytes);

        return new DownloadPath(root, animeDir, stem, normalizedExtension, mediaPath);
    }

    public DownloadPath Build(
        string  rootDirectory,
        string  animeTitle,
        int     season,
        double  episodeNumber,
        string  episodeTitle,
        string? extension = null)
    {
        return Build(rootDirectory, animeTitle, episodeTitle, season, episodeNumber, extension);
    }

    public DownloadPath Build(
        string       rootDirectory,
        AnimeDetails details,
        Episode      episode,
        string?      extension = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(episode);

        return Build(
            rootDirectory,
            details.Title,
            episode.Title,
            episode.Season ?? 1,
            episode.Number,
            extension);
    }

    public string BuildAnimeDirectory(string rootDirectory, string animeTitle)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Çıktı dizini boş olamaz.", nameof(rootDirectory));
        }

        var root  = Path.GetFullPath(rootDirectory);
        var anime = SanitizeComponent(animeTitle, AnimeFallback);
        var availableBytes = MaxFullPathUtf8Bytes
                             - Encoding.UTF8.GetByteCount(root)
                             - Encoding.UTF8.GetByteCount(Path.DirectorySeparatorChar.ToString())
                             - TemporarySuffixUtf8Bytes;
        if (availableBytes < 8)
        {
            throw new ArgumentException("Çıktı dizini tam hedef yolu için çok uzun.", nameof(rootDirectory));
        }

        anime = TruncateUtf8(anime, Math.Min(MaxComponentUtf8Bytes, availableBytes));
        var animeDir = Path.Combine(root, anime);
        EnsureUnderRoot(root, animeDir);
        EnsureFullPathBudget(animeDir, TemporarySuffixUtf8Bytes);
        return animeDir;
    }

    public string BuildFileStem(int season, double episodeNumber, string episodeTitle)
    {
        var seasonNumber = season < 1 ? 1 : season;
        var episodeText  = FormatEpisodeNumber(episodeNumber);
        var stem         = $"S{seasonNumber.ToString("00", CultureInfo.InvariantCulture)}E{episodeText}";

        if (!string.IsNullOrWhiteSpace(episodeTitle))
        {
            stem += " - " + SanitizeComponent(episodeTitle, EpisodeFallback);
        }

        return SanitizeComponent(stem, "Episode");
    }

    public string BuildFilePath(
        string  rootDirectory,
        string  animeTitle,
        string  episodeTitle,
        int     season,
        double  episodeNumber,
        string? extension = null)
    {
        return Build(rootDirectory, animeTitle, episodeTitle, season, episodeNumber, extension).MediaPath;
    }

    public string BuildPath(
        string  rootDirectory,
        string  animeTitle,
        string  episodeTitle,
        int     season,
        double  episodeNumber,
        string? extension = null)
    {
        return BuildFilePath(rootDirectory, animeTitle, episodeTitle, season, episodeNumber, extension);
    }

    public static string SanitizeComponent(string? value, string fallback = "download")
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return fallback;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var codePoint in EnumerateCodePoints(text))
        {
            if (IsUnsafeCodePoint(codePoint))
            {
                builder.Append('_');
                continue;
            }

            builder.Append(char.ConvertFromUtf32(codePoint));
        }

        var sanitized = builder.ToString().Trim();
        sanitized = sanitized.TrimEnd(' ', '.');
        if (sanitized.Length == 0 || sanitized is "." or "..")
        {
            return fallback;
        }

        sanitized = TruncateUtf8(sanitized, MaxComponentUtf8Bytes);
        sanitized = sanitized.TrimEnd(' ', '.');
        if (sanitized.Length == 0 || sanitized is "." or "..")
        {
            return fallback;
        }

        if (IsWindowsReservedName(sanitized))
        {
            sanitized = "_" + sanitized;
            sanitized = TruncateUtf8(sanitized, MaxComponentUtf8Bytes)
                                  .TrimEnd(' ', '.');
        }

        return sanitized.Length == 0 ? fallback : sanitized;
    }

    public static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var value = extension.Trim();
        if (value.StartsWith(".", StringComparison.Ordinal))
        {
            value = value[1..];
        }

        if (value.Length == 0
            || value.Any(c => !char.IsAsciiLetterOrDigit(c))
            || value.Length > 12)
        {
            throw new ArgumentException("Dosya uzantısı geçersiz.", nameof(extension));
        }

        return "." + value;
    }

    private static string FormatEpisodeNumber(double episodeNumber)
    {
        if (double.IsNaN(episodeNumber) || double.IsInfinity(episodeNumber) || episodeNumber < 0)
        {
            episodeNumber = 1;
        }

        if (episodeNumber < 1)
        {
            episodeNumber = 1;
        }

        var rounded = Math.Round(episodeNumber, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded - Math.Round(rounded)) < 0.0001)
        {
            return ((long) Math.Round(rounded)).ToString("00", CultureInfo.InvariantCulture);
        }

        return rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static IEnumerable<int> EnumerateCodePoints(string value)
    {
        for (var index = 0; index < value.Length;)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                var codePoint = char.ConvertToUtf32(current, value[index + 1]);
                if (codePoint is >= 0 and <= MaxCodePoint)
                {
                    yield return codePoint;
                    index += 2;
                    continue;
                }
            }

            if (char.IsSurrogate(current))
            {
                yield return MaxCodePoint + 1;
                index++;
                continue;
            }

            yield return current;
            index++;
        }
    }

    private static bool IsUnsafeCodePoint(int codePoint)
    {
        if (codePoint > MaxCodePoint)
        {
            return true;
        }

        if (codePoint is '\0' or '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*')
        {
            return true;
        }

        if (codePoint is >= 0 and <= 0x1F
            || codePoint is >= 0x7F and <= 0x9F)
        {
            return true;
        }

        var invalid = Path.GetInvalidFileNameChars();
        return Array.Exists(invalid, c => c == codePoint);
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder();
        var bytes   = 0;
        foreach (var codePoint in EnumerateCodePoints(value))
        {
            var character = char.ConvertFromUtf32(Math.Min(codePoint, 0x10FFFF));
            var size      = Encoding.UTF8.GetByteCount(character);
            if (bytes + size > maxBytes)
            {
                break;
            }

            builder.Append(character);
            bytes += size;
        }

        return builder.ToString();
    }

    private static bool IsWindowsReservedName(string value)
    {
        var dotIndex = value.IndexOf('.');
        var firstPart = dotIndex >= 0 ? value[..dotIndex] : value;
        return firstPart.Equals("CON", StringComparison.OrdinalIgnoreCase)
               || firstPart.Equals("PRN", StringComparison.OrdinalIgnoreCase)
               || firstPart.Equals("AUX", StringComparison.OrdinalIgnoreCase)
               || firstPart.Equals("NUL", StringComparison.OrdinalIgnoreCase)
               || (firstPart.Length == 4
                   && firstPart.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   && firstPart[3] is >= '1' and <= '9' or '¹' or '²' or '³')
               || (firstPart.Length == 4
                   && firstPart.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)
                   && firstPart[3] is >= '1' and <= '9' or '¹' or '²' or '³')
               || firstPart.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
               || firstPart.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
               || firstPart.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase);
    }

    internal static void EnsureFullPathBudget(string path, int reservedSuffixBytes)
    {
        if (reservedSuffixBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedSuffixBytes));
        }

        var fullPath = Path.GetFullPath(path);
        var byteCount = Encoding.UTF8.GetByteCount(fullPath);
        if (byteCount > MaxFullPathUtf8Bytes - reservedSuffixBytes)
        {
            throw new ArgumentException("Tam çıktı yolu güvenli dosya adı bütçesini aşıyor.", nameof(path));
        }
    }

    private static void EnsureUnderRoot(string root, string candidate)
    {
        var fullRoot     = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var relative      = Path.GetRelativePath(fullRoot, fullCandidate);
        var comparison    = OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal;

        if (Path.IsPathRooted(relative)
            || relative.Equals("..", comparison)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, comparison))
        {
            throw new ArgumentException("Çıktı yolu ana dizinin dışına çıkıyor.", nameof(candidate));
        }
    }
}
