namespace Migurdex.Shared.Interfaces;

public sealed record Mp4Metadata(string Quality,
    long?                              Bitrate,
    long?                              SizeBytes,
    double?                            DurationSeconds,
    int?                               Width           = null,
    int?                               Height          = null,
    string?                            VideoCodec      = null,
    string?                            AudioCodec      = null);

public interface IMp4MetadataReader
{
    Task<string> GetVideoQualityAsync(string videoUrl,
        string?                              referer           = null,
        string?                              userAgent         = null,
        CancellationToken                    cancellationToken = default);

    Task<string> GetVideoQualityAsync(string videoUrl,
        Dictionary<string, string>           headers,
        CancellationToken                    cancellationToken = default);

    Task<Mp4Metadata> GetVideoMetadataAsync(string videoUrl,
        string?                                        referer           = null,
        string?                                        userAgent         = null,
        CancellationToken                              cancellationToken = default);

    Task<Mp4Metadata> GetVideoMetadataAsync(string videoUrl,
        Dictionary<string, string>                     headers,
        CancellationToken                              cancellationToken = default);
}
