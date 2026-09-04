using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface IMetadataProvider
{
    string Name { get; }

    Task<List<MediaMetadata>> SearchMetadataAsync(string title,
        ContentFormat                                    expectedFormat    = ContentFormat.Unknown,
        CancellationToken                                cancellationToken = default);

    Task<MediaMetadata?> GetMetadataByIdAsync(string id, CancellationToken cancellationToken = default);
}
