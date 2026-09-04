using Migurdex.Shared.Enums;
using Migurdex.Shared.Interfaces;
using Migurdex.Shared.Models;
using System.Text.Json;

namespace Migurdex.Core.Services;

public class JikanProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;

    public JikanProvider(ISharedBridge bridge)
    {
        _httpClient = bridge.CreateHttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Migurdex/1.0");
    }

    public string Name => "Jikan";

    public async Task<List<MediaMetadata>> SearchMetadataAsync(string title,
        ContentFormat                                                 expectedFormat    = ContentFormat.Unknown,
        CancellationToken                                             cancellationToken = default)
    {
        var url = $"https://api.jikan.moe/v4/anime?q={Uri.EscapeDataString(title)}&limit=10";
        if (expectedFormat == ContentFormat.Manga)
        {
            url = $"https://api.jikan.moe/v4/manga?q={Uri.EscapeDataString(title)}&limit=10";
        }

        var list = new List<MediaMetadata>();

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return list;
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    list.Add(MapMedia(item));
                }
            }
        }
        catch
        {
            // ignored
        }

        return list;
    }

    public async Task<MediaMetadata?> GetMetadataByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.jikan.moe/v4/anime/{id}";

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                var meta = MapMedia(data);
                meta.MyAnimeListId = meta.ExternalId;

                return meta;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public async Task<string?> QuerySequelIdAsync(string malId, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.jikan.moe/v4/anime/{malId}/relations";

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var       json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc  = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var dataArray) && dataArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataArray.EnumerateArray())
                {
                    var relationType = item.TryGetProperty("relation", out var relProp) ? relProp.GetString() : null;
                    if (relationType == "Sequel")
                    {
                        if (item.TryGetProperty("entry", out var entryArray)
                            && entryArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in entryArray.EnumerateArray())
                            {
                                var type = entry.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                                if (type == "anime")
                                {
                                    if (entry.TryGetProperty("mal_id", out var malIdProp))
                                    {
                                        return malIdProp.ValueKind == JsonValueKind.Number
                                                   ? malIdProp.GetInt32().ToString()
                                                   : malIdProp.GetString();
                                    }
                                }
                            }
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

    private static MediaMetadata MapMedia(JsonElement m)
    {
        var metadata = new MediaMetadata
        {
            ExternalId    = m.GetProperty("mal_id").GetInt32().ToString(),
            Source        = MetadataSource.Jikan,
            Title         = m.GetProperty("title").GetString() ?? "",
            EnglishTitle  = m.TryGetProperty("title_english", out var eng) ? eng.GetString() : null,
            JapaneseTitle = m.TryGetProperty("title_japanese", out var nat) ? nat.GetString() : null,
            OriginalTitle = m.TryGetProperty("title_japanese", out var origNat) ? origNat.GetString() : null,
            Summary       = m.TryGetProperty("synopsis", out var desc) ? desc.GetString() ?? "" : "",
            PosterUrl     = m.GetProperty("images").GetProperty("jpg").GetProperty("large_image_url").GetString() ?? "",
            Status        = m.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "",
            Year =
                m.TryGetProperty("year", out var year) && year.ValueKind != JsonValueKind.Null ? year.GetInt32() : null,
            Score =
                m.TryGetProperty("score", out var score) && score.ValueKind != JsonValueKind.Null
                    ? score.GetDouble()
                    : null,
            TotalEpisodes =
                m.TryGetProperty("episodes", out var ep) && ep.ValueKind != JsonValueKind.Null ? ep.GetInt32() : null,
            Format = MapFormat(m.TryGetProperty("type", out var f) ? f.GetString() : "")
        };

        if (m.TryGetProperty("genres", out var genres))
        {
            metadata.Genres = genres.EnumerateArray().Select(g => g.GetProperty("name").GetString() ?? "").ToList();
        }

        if (m.TryGetProperty("titles", out var syns))
        {
            metadata.Synonyms = syns.EnumerateArray().Select(s => s.GetProperty("title").GetString() ?? "").ToList();
        }

        return metadata;
    }

    private static ContentFormat MapFormat(string? format)
    {
        return format?.ToLower() switch
        {
            "tv"      => ContentFormat.Tv,
            "movie"   => ContentFormat.Movie,
            "ova"     => ContentFormat.Ova,
            "special" => ContentFormat.Special,
            "manga"   => ContentFormat.Manga,
            _         => ContentFormat.Unknown
        };
    }
}
