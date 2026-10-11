namespace Migurdex.Cli.Services.Downloads;

public static class ResumeProgressProbe
{
    public static (long DownloadedBytes, long? TotalBytes) Probe(
        string rootDirectory,
        string animeTitle,
        string episodeTitle,
        int    season,
        double episodeNumber)
    {
        try
        {
            var builder  = new DownloadPathBuilder();
            var animeDir = builder.BuildAnimeDirectory(rootDirectory, animeTitle);
            if (!Directory.Exists(animeDir))
            {
                return (0, null);
            }

            var stem = builder.BuildFileStem(season, episodeNumber, episodeTitle);

            long  downloaded = 0;
            long? total      = null;
            foreach (var file in EnumeratePartialFiles(animeDir, stem))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".part", StringComparison.Ordinal)
                    || name.Contains(".seg", StringComparison.Ordinal))
                {
                    try
                    {
                        downloaded += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                if (name.EndsWith(".part.meta", StringComparison.Ordinal)
                    && total is null
                    && Mp4ResumeMetadata.TryReadAny(file, out var metadata)
                    && metadata?.TotalBytes is > 0)
                {
                    total = metadata.TotalBytes;
                }
            }

            return (downloaded, total);
        }
        catch (Exception ex) when (ex is ArgumentException
                                         or IOException
                                         or UnauthorizedAccessException)
        {
            return (0, null);
        }
    }

    public static List<string> EnumeratePartialFiles(string animeDirectory, string fileStem)
    {
        var found = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(animeDirectory))
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith(fileStem, StringComparison.Ordinal))
                {
                    continue;
                }

                if (name.EndsWith(".part", StringComparison.Ordinal)
                    || name.EndsWith(".part.meta", StringComparison.Ordinal)
                    || name.Contains(".seg", StringComparison.Ordinal))
                {
                    found.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    public static int Cleanup(
        string rootDirectory,
        string animeTitle,
        string episodeTitle,
        int    season,
        double episodeNumber)
    {
        try
        {
            var builder  = new DownloadPathBuilder();
            var animeDir = builder.BuildAnimeDirectory(rootDirectory, animeTitle);
            if (!Directory.Exists(animeDir))
            {
                return 0;
            }

            var stem    = builder.BuildFileStem(season, episodeNumber, episodeTitle);
            var deleted = 0;
            foreach (var file in EnumeratePartialFiles(animeDir, stem))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return deleted;
        }
        catch (Exception ex) when (ex is ArgumentException
                                         or IOException
                                         or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
