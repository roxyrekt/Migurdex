using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Services;

public partial class AniListProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;

    public AniListProvider(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Migurdex/1.0");
    }

    public string Name => "AniList";

    public async Task<List<MediaMetadata>> SearchMetadataAsync(string title,
        ContentFormat                                                 expectedFormat    = ContentFormat.Unknown,
        CancellationToken                                             cancellationToken = default)
    {
        const string query = """

                                     query ($search: String, $type: MediaType) {
                                       Page(page: 1, perPage: 10) {
                                         media(search: $search, type: $type) {
                                           id
                                           idMal
                                           title {
                                             romaji
                                             english
                                             native
                                           }
                                           description
                                           format
                                           status
                                           seasonYear
                                           averageScore
                                           episodes
                                           genres
                                           synonyms
                                           coverImage {
                                             extraLarge
                                           }
                                           bannerImage
                                         }
                                       }
                                     }
                             """;

        var variables = new
        {
            search = title,
            type   = expectedFormat == ContentFormat.Manga ? "MANGA" : "ANIME"
        };

        return await FetchListFromAniList(query, variables, cancellationToken);
    }

    public async Task<MediaMetadata?> GetMetadataByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        const string query = """

                                     query ($id: Int) {
                                       Media(id: $id) {
                                         id
                                         idMal
                                         title {
                                           romaji
                                           english
                                           native
                                         }
                                         description
                                         format
                                         status
                                         seasonYear
                                         averageScore
                                         episodes
                                         genres
                                         synonyms
                                         coverImage {
                                           extraLarge
                                         }
                                         bannerImage
                                       }
                                     }
                             """;

        var variables = new
        {
            id = int.Parse(id)
        };

        var list = await FetchListFromAniList(query, variables, cancellationToken);

        return list.FirstOrDefault();
    }

    public async Task<MediaMetadata?> GetMetadataByMalIdAsync(string malId,
        CancellationToken                                            cancellationToken = default)
    {
        const string query = """

                                     query ($idMal: Int) {
                                       Media(idMal: $idMal, type: ANIME) {
                                         id
                                         idMal
                                         title {
                                           romaji
                                           english
                                           native
                                         }
                                         description
                                         format
                                         status
                                         seasonYear
                                         averageScore
                                         episodes
                                         genres
                                         synonyms
                                         coverImage {
                                           extraLarge
                                         }
                                         bannerImage
                                       }
                                     }
                             """;

        try
        {
            var variables = new
            {
                idMal = int.Parse(malId)
            };

            var list = await FetchListFromAniList(query, variables, cancellationToken);

            return list.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> QuerySequelIdAsync(string id, CancellationToken cancellationToken = default)
    {
        const string query = """
                                 query ($id: Int) {
                                   Media(id: $id) {
                                     relations {
                                       edges {
                                         relationType
                                         node {
                                           id
                                           type
                                         }
                                       }
                                     }
                                   }
                                 }
                             """;

        try
        {
            var variables = new
            {
                id = int.Parse(id)
            };

            var requestBody = new
            {
                query,
                variables
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("https://graphql.anilist.co", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("Media", out var media)
                && media.TryGetProperty("relations", out var relations)
                && relations.TryGetProperty("edges", out var edges))
            {
                foreach (var edge in edges.EnumerateArray())
                {
                    var relType = edge.TryGetProperty("relationType", out var relTypeProp)
                                      ? relTypeProp.GetString()
                                      : null;

                    if (relType == "SEQUEL")
                    {
                        if (edge.TryGetProperty("node", out var node)
                            && node.TryGetProperty("id", out var nodeIdProp)
                            && node.TryGetProperty("type", out var typeProp)
                            && typeProp.GetString() == "ANIME")
                        {
                            return nodeIdProp.GetInt32().ToString();
                        }
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private async Task<List<MediaMetadata>> FetchListFromAniList(string query,
        object                                                          variables,
        CancellationToken                                               cancellationToken = default)
    {
        var requestBody = new
        {
            query,
            variables
        };
        var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("https://graphql.anilist.co", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var       json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc  = JsonDocument.Parse(json);

        var list = new List<MediaMetadata>();

        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("Page", out var page) && page.TryGetProperty("media", out var mediaList))
            {
                foreach (var m in mediaList.EnumerateArray())
                {
                    list.Add(MapMedia(m));
                }
            }
            else if (data.TryGetProperty("Media", out var media))
            {
                list.Add(MapMedia(media));
            }
        }

        return list;
    }

    private static MediaMetadata MapMedia(JsonElement m)
    {
        var metadata = new MediaMetadata
        {
            ExternalId    = m.GetProperty("id").GetInt32().ToString(),
            Source        = MetadataSource.AniList,
            Title         = m.GetProperty("title").GetProperty("romaji").GetString() ?? "",
            EnglishTitle  = m.GetProperty("title").TryGetProperty("english", out var eng) ? eng.GetString() : null,
            RomajiTitle   = m.GetProperty("title").GetProperty("romaji").GetString(),
            JapaneseTitle = m.GetProperty("title").TryGetProperty("native", out var nat) ? nat.GetString() : null,
            OriginalTitle =
                m.GetProperty("title").TryGetProperty("native", out var natOrig) ? natOrig.GetString() : null,
            Summary   = m.TryGetProperty("description", out var desc) ? CleanHtml(desc.GetString()) : "",
            PosterUrl = m.GetProperty("coverImage").GetProperty("extraLarge").GetString() ?? "",
            BannerUrl = m.TryGetProperty("bannerImage", out var banner) ? banner.GetString() : null,
            Status    = m.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "",
            Year =
                m.TryGetProperty("seasonYear", out var year) && year.ValueKind != JsonValueKind.Null
                    ? year.GetInt32()
                    : null,
            Score =
                m.TryGetProperty("averageScore", out var score) && score.ValueKind != JsonValueKind.Null
                    ? score.GetDouble() / 10.0
                    : null,
            TotalEpisodes =
                m.TryGetProperty("episodes", out var ep) && ep.ValueKind != JsonValueKind.Null ? ep.GetInt32() : null,
            Format = MapFormat(m.TryGetProperty("format", out var f) ? f.GetString() : "")
        };

        if (m.TryGetProperty("idMal", out var malIdProp) && malIdProp.ValueKind != JsonValueKind.Null)
        {
            metadata.MyAnimeListId = malIdProp.GetInt32().ToString();
            metadata.AniListId     = metadata.ExternalId;
        }

        if (m.TryGetProperty("genres", out var genres))
        {
            metadata.Genres = genres.EnumerateArray().Select(g => g.GetString() ?? "").ToList();
        }

        if (m.TryGetProperty("synonyms", out var syns))
        {
            metadata.Synonyms = syns.EnumerateArray().Select(s => s.GetString() ?? "").ToList();
        }

        return metadata;
    }

    private static ContentFormat MapFormat(string? format)
    {
        return format switch
        {
            "TV" or "TV_SHORT" => ContentFormat.Tv,
            "MOVIE"            => ContentFormat.Movie,
            "OVA"              => ContentFormat.Ova,
            "SPECIAL"          => ContentFormat.Special,
            "MANGA"            => ContentFormat.Manga,
            _                  => ContentFormat.Unknown
        };
    }

    [GeneratedRegex("<.*?>")]
    private static partial Regex HtmlTagRegex();

    private static string CleanHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        return HtmlTagRegex().Replace(html, "").Trim();
    }
}
