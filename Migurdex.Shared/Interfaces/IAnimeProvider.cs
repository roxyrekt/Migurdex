using Migurdex.Shared.Models;

namespace Migurdex.Shared.Interfaces;

public interface IAnimeProvider : IProvider
{
    Task<List<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<AnimeDetails> GetDetailsAsync(string animeId, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<List<string>> GetGroupsAsync(string episodeId, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<List<VideoSource>> GetVideoSourcesAsync(string episodeId,
        string?                                         group             = null,
        CancellationToken                               cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
