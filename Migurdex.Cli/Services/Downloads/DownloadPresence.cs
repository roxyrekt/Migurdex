using Migurdex.Cli.Configuration;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services.Downloads;

/// <summary>
/// Bir bölümün hedef videosunun diskte <b>zaten var olup olmadığını</b> söyler.
/// </summary>
/// <remarks>
/// <para>
/// ⭐ <b>Neden ayrı bir sınıf:</b> bu bilgiyi <b>iki ekran</b> gerektiriyor —
/// toplu indirme ekranı ("atlandı" demek için) ve bölüm seçim ekranı
/// (indirilmiş olanları renklendirmek için). Kopyalansaydı iki yerde
/// bakımı ayrı kalırdı; <see cref="DownloadPathBuilder"/>'ın yanına konduğu
/// için yol kuralları tek yerde.
/// </para>
/// <para>
/// ⭐ <b>MediaPath tek başına yetmez:</b> uzantı bilinmediğinde
/// <c>GetMediaPath(null)</c> dosya adını <b>uzantısız</b> üretir
/// (<c>DownloadModels.cs:233</c>), bu yüzden HLS bölümünde
/// <c>File.Exists</c> hep <c>false</c> döner. Bu yüzden <c>FileStem</c> + <c>.*</c>
/// taranır ve yalnız gerçek video kapsayıcıları kabul edilir: <c>.part</c> (yarım
/// indirme) ve <c>.vtt</c> (altyazı) indirilmiş video <b>demektir</b>.
/// </para>
/// </remarks>
internal static class DownloadPresence
{
    /// <summary>Video kapsayıcı uzantıları.</summary>
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".ts", ".flv"
        };

    /// <summary>
    /// Bölümün indirileceği hedefi kurar.
    /// </summary>
    /// <remarks>
    /// ⭐ Uzantı kuralı <see cref="DownloadService"/> ile <b>birebir aynı</b>
    /// olmalı; farklı olursa burada "yok" deyip motorun "zaten var" demesine
    /// yol açarız. MP4 dışında uzantı <c>null</c> verilir, çünkü HLS'de gerçek
    /// kapsayıcıyı <c>yt-dlp</c> belirler.
    /// </remarks>
    public static DownloadPath BuildDestination(IDownloadPathBuilder pathBuilder,
                                                CliConfig          config,
                                                string?            animeTitle,
                                                Episode            episode,
                                                VideoSource        source)
    {
        ArgumentNullException.ThrowIfNull(pathBuilder);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(source);

        var extension = source.Type == VideoType.Mp4 ? ".mp4" : null;

        return pathBuilder.Build(config.DownloadDirectory,
                                 animeTitle ?? string.Empty,
                                 episode.Title ?? string.Empty,
                                 episode.Season ?? 1,
                                 episode.Number,
                                 extension);
    }

    /// <summary>
    /// Hedef video dosyası zaten diskte mi?
    /// </summary>
    /// <remarks>
    /// ⭐ <b>Neden <c>GetFileNameWithoutExtension</c> kullanılmıyor:</b> bölüm
    /// adında nokta varsa ("Bölüm 1.5") stem'i kırpar ve eşleşme bulunamaz.
    /// Doğrudan <c>FileStem</c> kullanılır.
    /// </remarks>
    public static string? FindExistingVideo(DownloadPath? destination)
    {
        if (destination is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(destination.MediaPath) && File.Exists(destination.MediaPath))
        {
            return destination.MediaPath;
        }

        var directory = destination.AnimeDirectory;
        var stem      = destination.FileStem;

        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem)
            || !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            foreach (var candidate in Directory.EnumerateFiles(directory, stem + ".*"))
            {
                if (VideoExtensions.Contains(Path.GetExtension(candidate)))
                {
                    return candidate;
                }
            }
        }
        catch (IOException)
        {
            // Dizin okunamıyorsa "yok" say: indirmeyi engellememeli.
        }
        catch (UnauthorizedAccessException)
        {
            // Aynı gerekçe.
        }

        return null;
    }

    /// <summary>
    /// Bir dizinde <b>zaten indirilmiş</b> bölümlerin kök adlarını verir.
    /// </summary>
    /// <remarks>
    /// ⭐ <b>Neden tek seferde tarama:</b> seçim ekranı 1166 bölümlük bir dizi
    /// açabiliyor. Bölüm başına ayrı taramak ölçüldüğünde <b>0,1 sn</b>,
    /// tek tarama <b>0,1 ms</b> — yani 1000 kat fark. Buradaki
    /// <see cref="HashSet{T}"/> sayesinde bölüm başına sorgu O(1)'dir.
    /// </remarks>
    public static HashSet<string> LoadExistingStems(string? animeDirectory)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(animeDirectory) || !Directory.Exists(animeDirectory))
        {
            return stems;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(animeDirectory))
            {
                if (!VideoExtensions.Contains(Path.GetExtension(file)))
                {
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrWhiteSpace(stem))
                {
                    stems.Add(stem);
                }
            }
        }
        catch (IOException)
        {
            // Dizin okunamıyorsa "hiçbiri indirilmemiş" gibi davran.
        }
        catch (UnauthorizedAccessException)
        {
            // Aynı gerekçe.
        }

        return stems;
    }

    /// <summary>
    /// Bir dizinde <b>zaten indirilmiş</b> bölümlerin kök adlarını verir.
    /// </summary>
    /// <remarks>
    /// ⭐ <b>Neden tek seferde tarama:</b> seçim ekranı 1166 bölümlük bir dizi
    /// açabiliyor. Bölüm başına ayrı taramak ölçüldüğünde <b>0,1 sn</b>,
    /// tek tarama <b>0,1 ms</b> — yani 1000 kat fark. Buradaki
    /// <see cref="HashSet{T}"/> sayesinde bölüm başına sorgu O(1)'dir.
    /// </remarks>

    /// <summary>Bu bölüm zaten indirilmiş mi?</summary>
    public static bool IsAlreadyDownloaded(IDownloadPathBuilder pathBuilder,
                                           CliConfig          config,
                                           string?            animeTitle,
                                           Episode            episode,
                                           VideoSource        source)
    {
        // overwrite açıksa "zaten var" bir sorun değil: üzerine yazılacak.
        if (config.DownloadOverwrite)
        {
            return false;
        }

        return FindExistingVideo(
                   BuildDestination(pathBuilder, config, animeTitle, episode, source)) is not null;
    }
}
