using Migurdex.Cli.Configuration;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

public static class SourceSelector
{
    public static List<VideoSource> SortVideoSources(List<VideoSource> rawList, CliConfig config)
    {
        if (rawList.Count == 0)
        {
            return rawList;
        }

        IOrderedEnumerable<VideoSource>? ordered = null;

        foreach (var criterion in config.SourceSortPriority)
        {
            Func<VideoSource, object> keySelector = criterion switch
            {
                "Quality" => s => GetRankFromOrderList(s.Quality, config.PreferredQualityOrder),
                "Format"  => s => GetRankFromOrderList(s.Type.ToString(), config.PreferredFormatOrder),
                "Hoster"  => s => GetRankFromOrderList(s.Hoster, config.PreferredHosterOrder),
                "Group"   => s => s.Group ?? "",
                _         => s => 0
            };

            var descending = criterion != "Group";

            if (ordered == null)
            {
                ordered = descending ? rawList.OrderByDescending(keySelector) : rawList.OrderBy(keySelector);
            }
            else
            {
                ordered = descending ? ordered.ThenByDescending(keySelector) : ordered.ThenBy(keySelector);
            }
        }

        return [.. ordered ?? rawList.OrderBy(s => 0)];
    }

    public static bool IsExactMatch(VideoSource s, CliConfig config)
    {
        var bestQuality = config.PreferredQualityOrder.FirstOrDefault() ?? "1080p";
        var bestFormat  = config.PreferredFormatOrder.FirstOrDefault() ?? "M3U8";
        var bestHoster  = config.PreferredHosterOrder.FirstOrDefault() ?? "GoogleDrive";

        var isBestQuality = s.Quality.Contains(bestQuality, StringComparison.OrdinalIgnoreCase);
        var isBestFormat  = s.Type.ToString().Contains(bestFormat, StringComparison.OrdinalIgnoreCase);
        var isBestHoster  = s.Hoster != null && s.Hoster.Contains(bestHoster, StringComparison.OrdinalIgnoreCase);

        return isBestQuality && isBestFormat && isBestHoster;
    }

    public static bool IsAutoEligible(VideoSource s, CliConfig config)
    {
        if (IsListed(s.Hoster, config.AutoNeverHosters)
            || IsListed(s.Quality, config.AutoNeverQualities)
            || IsListed(s.Type.ToString(), config.AutoNeverTypes))
        {
            return false;
        }

        if (config.AutoOnlyHosters.Count > 0 && !IsListed(s.Hoster, config.AutoOnlyHosters))
        {
            return false;
        }

        if (config.AutoOnlyQualities.Count > 0 && !IsListed(s.Quality, config.AutoOnlyQualities))
        {
            return false;
        }

        if (config.AutoOnlyTypes.Count > 0 && !IsListed(s.Type.ToString(), config.AutoOnlyTypes))
        {
            return false;
        }

        return true;
    }

    public static VideoSource? PickBest(IEnumerable<VideoSource> sources, CliConfig config)
    {
        var list     = sources.ToList();
        var eligible = list.Where(s => IsAutoEligible(s, config)).ToList();
        var pool     = eligible.Count > 0 ? eligible : list;
        return SortVideoSources(pool, config).FirstOrDefault();
    }

    private static int GetRankFromOrderList(string? value, List<string> orderList)
    {
        if (string.IsNullOrEmpty(value))
        {
            return -1;
        }

        var valLower = value.ToLowerInvariant();

        for (var i = 0; i < orderList.Count; i++)
        {
            if (valLower.Contains(orderList[i].ToLowerInvariant()))
            {
                return orderList.Count - i;
            }
        }

        return 0;
    }

    private static bool IsListed(string? value, List<string> list)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return list.Any(e => value.Contains(e, StringComparison.OrdinalIgnoreCase));
    }
}
