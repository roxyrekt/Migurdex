namespace Migurdex.Shared.Interfaces;

public interface IMp4MetadataReader
{
    Task<string> GetVideoQualityAsync(string videoUrl,
        string?                              referer           = null,
        string?                              userAgent         = null,
        CancellationToken                    cancellationToken = default);

    Task<string> GetVideoQualityAsync(string videoUrl,
        Dictionary<string, string>           headers,
        CancellationToken                    cancellationToken = default);
}
