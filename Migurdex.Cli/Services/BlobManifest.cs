using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Migurdex.Cli.Services;

public sealed record BlobEntry(
    [property: JsonPropertyName("sha")] string Sha,
    [property: JsonPropertyName("off")] long   Offset,
    [property: JsonPropertyName("len")] int    Length);

public sealed class BlobManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = string.Empty;

    [JsonPropertyName("blobs")]
    public List<BlobEntry> Blobs { get; set; } = [];

    [JsonPropertyName("paths")]
    public Dictionary<string, string> Paths { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("exec")]
    public List<string> Exec { get; set; } = [];

    public Dictionary<string, BlobEntry> BySha()
    {
        var map = new Dictionary<string, BlobEntry>(Blobs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var b in Blobs)
        {
            map[b.Sha] = b;
        }

        return map;
    }

    public static async Task<BlobManifest> LoadGzipAsync(Stream gzip, CancellationToken cancellationToken = default)
    {
        await using var inner = new GZipStream(gzip, CompressionMode.Decompress, leaveOpen: true);

        var manifest = await JsonSerializer.DeserializeAsync<BlobManifest>(inner, cancellationToken: cancellationToken)
                                           .ConfigureAwait(false)
                       ?? throw new InvalidDataException("manifest boş");

        if (manifest.Version < 1 || manifest.Blobs.Count == 0 || manifest.Paths.Count == 0)
        {
            throw new InvalidDataException("manifest eksik veya geçersiz");
        }

        return manifest;
    }
}
