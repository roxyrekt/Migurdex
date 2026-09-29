using System.Text.Json;

namespace Migurdex.Cli.Services.Downloads;

internal sealed class Mp4ResumeMetadata
{
    private const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string SourceFingerprint { get; init; } = string.Empty;
    public string? ETagDigest { get; init; }
    public string? LastModifiedDigest { get; init; }
    public long? TotalBytes { get; init; }
    public int? SegmentCount { get; init; }

    public static Mp4ResumeMetadata Create(
        string sourceFingerprint,
        string? etag,
        string? lastModified,
        long?      totalBytes,
        int?       segmentCount = null)
    {
        return new Mp4ResumeMetadata
        {
            SourceFingerprint  = sourceFingerprint,
            ETagDigest         = NullIfEmpty(DownloadHttp.CreateOpaqueDigest(etag)),
            LastModifiedDigest = NullIfEmpty(DownloadHttp.CreateOpaqueDigest(lastModified)),
            TotalBytes         = totalBytes,
            SegmentCount       = segmentCount
        };
    }

    public bool MatchesResponse(string? etag, string? lastModified)
    {
        var currentEtag         = NullIfEmpty(DownloadHttp.CreateOpaqueDigest(etag));
        var currentLastModified = NullIfEmpty(DownloadHttp.CreateOpaqueDigest(lastModified));

        if (ETagDigest is not null
            && (!string.Equals(ETagDigest, currentEtag, StringComparison.Ordinal)
                || currentEtag is null))
        {
            return false;
        }

        if (LastModifiedDigest is not null
            && (!string.Equals(LastModifiedDigest, currentLastModified, StringComparison.Ordinal)
                || currentLastModified is null))
        {
            return false;
        }

        return true;
    }

    public static bool TryRead(
        string path,
        string sourceFingerprint,
        out Mp4ResumeMetadata? metadata)
    {
        metadata = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var json = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<Mp4ResumeMetadata>(json);
            if (value is null
                || value.Version != CurrentVersion
                || !string.Equals(value.SourceFingerprint,
                                  sourceFingerprint,
                                  StringComparison.Ordinal))
            {
                return false;
            }

            metadata = value;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or JsonException
                                      or NotSupportedException)
        {
            return false;
        }
    }

    public static async Task WriteAsync(
        string               path,
        Mp4ResumeMetadata    metadata,
        CancellationToken    cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        DeleteIfExists(temporaryPath);
        var json = JsonSerializer.Serialize(metadata);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static void Delete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        DeleteIfExists(path);
        DeleteIfExists(path + ".tmp");
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }
}
