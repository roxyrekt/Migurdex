using Migurdex.Shared.Models;
using System.Globalization;

namespace Migurdex.Cli.Services;

/// <summary>
/// Toplu indirmede hangi bölümlerin seçildiğini tanımlayan metin söz dizimi.
///
/// Kabul edilen biçimler:
/// <list type="bullet">
/// <item><description><c>all</c> / <c>tümü</c> / <c>hepsi</c>: sezon filtresi (<c>-s</c>) verilmişse o sezonun tamamı,
/// verilmemişse tüm sezonlardaki bütün bölümler</description></item>
/// <item><description><c>1,2,3</c>: tek tek numaralar</description></item>
/// <item><description><c>1-12</c>: kapalı aralık (1 ve 12 dahil)</description></item>
/// <item><description><c>1-3,7,9-10</c>: karışık liste</description></item>
/// <item><description><c>*</c>: <c>all</c> ile aynı</description></item>
/// </list>
/// Numaralar kültürden bağımsız olarak ayrıştırılır; boşluklar yok sayılır.
///
/// Sezon kuralı: bölüm numaraları sezona göre tekrar ettiği için numara listeleri
/// (<c>1-12</c> gibi) çok sezonlu anlamelerde <b>sezon ister</b>; aksi halde aynı numaralar her
/// sezondan bir kez indirilirdi. <c>all</c> ise sezon verilmezse bilinçli olarak tüm sezonları
/// kapsar.
/// </summary>
public sealed class EpisodeSelectionSpec
{
    private readonly List<(double From, double To)> _ranges;

    private EpisodeSelectionSpec(bool includesAll, List<(double From, double To)> ranges)
    {
        IncludesAll = includesAll;
        _ranges     = ranges;
    }

    public bool IncludesAll { get; }

    public IReadOnlyList<(double From, double To)> Ranges => _ranges;

    public static EpisodeSelectionSpec All => new(true, []);

    public static bool TryParse(string? text, out EpisodeSelectionSpec? spec, out string? error)
    {
        spec  = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Bölüm listesi boş olamaz.";
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed is "*" || IsAllKeyword(trimmed))
        {
            spec = All;
            return true;
        }

        var ranges = new List<(double From, double To)>();
        foreach (var rawPart in trimmed.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            if (IsAllKeyword(part) || part == "*")
            {
                spec = All;
                return true;
            }

            var separatorIndex = part.IndexOf('-', 1);
            if (separatorIndex > 0)
            {
                var fromText = part[..separatorIndex];
                var toText   = part[(separatorIndex + 1)..];
                if (!TryParseNumber(fromText, out var from) || !TryParseNumber(toText, out var to))
                {
                    error = $"Bölüm aralığı okunamadı: '{part}'.";
                    return false;
                }

                if (to < from)
                {
                    error = $"Bölüm aralığı ters: '{part}'.";
                    return false;
                }

                ranges.Add((from, to));
                continue;
            }

            if (!TryParseNumber(part, out var single))
            {
                error = $"Bölüm numarası okunamadı: '{part}'.";
                return false;
            }

            ranges.Add((single, single));
        }

        if (ranges.Count == 0)
        {
            error = "Bölüm listesi boş olamaz.";
            return false;
        }

        spec = new EpisodeSelectionSpec(false, ranges);
        return true;
    }

    /// <summary>
    /// Seçimi sağlayıcıdan gelen bölüm listesine uygular. Sıra her zaman sezon, sonra bölüm
    /// numarasıdır; aynı numara iki kez dönerse tek kopya kalır.
    ///
    /// <paramref name="season"/> verilmezse: anime tek sezonluysa o sezon, çok sezonluysa
    /// <see cref="IncludesAll"/> için tüm sezonlar, numara listeleri için hata döner
    /// (belirsizliği sessizce tahmin etmemek için).
    /// </summary>
    public bool TryResolve(
        IReadOnlyList<Episode> episodes,
        int?                   season,
        out List<Episode>      chosen,
        out string?            error)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        chosen = [];
        error  = null;

        var seasons = episodes.Select(episode => episode.Season ?? 1)
                              .Distinct()
                              .OrderBy(value => value)
                              .ToList();

        int? scope;
        if (season.HasValue)
        {
            scope = season.Value;
        }
        else if (seasons.Count <= 1)
        {
            scope = seasons.Count == 1 ? seasons[0] : null;
        }
        else if (IncludesAll)
        {
            scope = null;
        }
        else
        {
            error = $"Bu anime {seasons.Count} sezon içeriyor ({string.Join(", ", seasons)}); bölüm numaraları "
                    + "sezona göre tekrar eder. Sezonu -s <sezon> ile belirtin veya tüm sezonlar için "
                    + "--episodes all kullanın.";
            return false;
        }

        var ordered = episodes.Where(episode => !scope.HasValue || (episode.Season ?? 1) == scope.Value)
                              .OrderBy(episode => episode.Season ?? 1)
                              .ThenBy(episode => episode.Number)
                              .ThenBy(episode => episode.Id, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var episode in ordered)
        {
            if (!IncludesAll && !Matches(episode.Number))
            {
                continue;
            }

            if (seen.Add(EpisodeKey(episode)))
            {
                chosen.Add(episode);
            }
        }

        return true;
    }

    /// <summary>
    /// Seçimde hiçbir bölüm bulunamazsa kullanıcıya hangi numaraların dışarıda kaldığını söylemek
    /// için kullanılan okunabilir liste (en fazla <paramref name="maxItems"/> öğe).
    /// </summary>
    public string Describe(int maxItems = 8)
    {
        if (IncludesAll)
        {
            return "tümü";
        }

        var parts = _ranges.Select(range => Math.Abs(range.From - range.To) < 0.0001
                                                ? FormatNumber(range.From)
                                                : $"{FormatNumber(range.From)}-{FormatNumber(range.To)}")
                           .ToList();
        if (parts.Count <= maxItems)
        {
            return string.Join(", ", parts);
        }

        return string.Join(", ", parts.Take(maxItems)) + $" (+{parts.Count - maxItems})";
    }

    private bool Matches(double number)
    {
        foreach (var (from, to) in _ranges)
        {
            if (number >= from - 0.0001 && number <= to + 0.0001)
            {
                return true;
            }
        }

        return false;
    }

    private static string EpisodeKey(Episode episode)
    {
        return $"{episode.Season ?? 1}:{episode.Number.ToString("R", CultureInfo.InvariantCulture)}";
    }

    private static bool IsAllKeyword(string value)
    {
        return value.Equals("all", StringComparison.OrdinalIgnoreCase)
               || value.Equals("hepsi", StringComparison.OrdinalIgnoreCase)
               || value.Equals("tumu", StringComparison.OrdinalIgnoreCase)
               || value.Equals("tümü", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseNumber(string value, out double number)
    {
        return double.TryParse(value.Trim().Replace(',', '.'),
                               NumberStyles.Float,
                               CultureInfo.InvariantCulture,
                               out number)
               && double.IsFinite(number)
               && number > 0;
    }

    private static string FormatNumber(double value)
    {
        return value % 1 == 0
                   ? ((long) Math.Round(value)).ToString(CultureInfo.InvariantCulture)
                   : value.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
