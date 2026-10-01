using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;

namespace Migurdex.Cli.Services;

/// <summary>
/// Toplu indirmede ilk bölümde seçilen kaynak tercihini sonraki bölümlere taşır.
/// </summary>
/// <remarks>
/// <para>
/// <b>Niçin gerekli:</b> kaynak listesi bölümden bölüme değişir. Kullanıcı
/// 1. bölümde <c>TurkAnime / 720p / MP4</c> seçtiyse, 2. bölümde aynı hoster
/// bulunmayabilir. Bu durumda önce <b>aynı tercihe en yakın</b> kaynak aranır;
/// bulunamazsa çağıran taraf mevcut otomatik sıralamaya düşer. Böylece kalite
/// tercihi 24 bölüm boyunca korunur ama tek bir bölümdeki farklılık tüm işi
/// durdurmaz.
/// </para>
/// <para>
/// <b>Eşleştirme sırası:</b> hoster → kalite → tür. Aynı hoster varsa kalite
/// tercihi yok sayılır; kalite eşleşmezse o hoster'ın en yakın kalitesi seçilir
/// (tam eşleşme <i>şart değildir</i>). Hoster hiç yoksa tümüyle elenir ve
/// <see langword="null"/> döner.
/// </para>
/// <para>
/// Bu sınıf saf (state'siz eksi tek alan) olduğu için testi doğrudan yazılabilir;
/// dosya sistemi, ağ veya konsol gerektirmez.
/// </para>
/// </remarks>
public sealed class SourcePreference
{
    private string? _hoster;
    private string? _quality;
    private VideoType _type = VideoType.Unknown;

    /// <summary>Tercih yakalandı mı?</summary>
    public bool HasPreference => _hoster is not null;

    /// <summary>
    /// Referans alınacak kaynağı kaydeder. <see cref="VideoSource"/> örneğinden
    /// yalnızca <c>Hoster</c>, <c>Quality</c> ve <c>Type</c> alınır — URL
    /// bölümden bölüme değiştiği için saklanmaz.
    /// </summary>
    public void Capture(VideoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _hoster   = string.IsNullOrWhiteSpace(source.Hoster) ? null : source.Hoster.Trim();
        _quality  = string.IsNullOrWhiteSpace(source.Quality) ? null : source.Quality.Trim();
        _type     = source.Type;
    }

    /// <summary>
    /// Verilen kaynaklar arasında kaydedilmiş tercihe en yakın olanı döner.
    /// </summary>
    /// <returns>
    /// Eşleşen kaynak, ya da tercih bulunamadıysa/hister yoksa <see langword="null"/>.
    /// Çağıran bu durumda otomatik sıralamaya düşmelidir.
    /// </returns>
    public VideoSource? Match(IEnumerable<VideoSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (_hoster is null)
        {
            return null;
        }

        var pool = sources.Where(s => s.Hoster is not null
                                     && s.Hoster.Trim()
                                         .Equals(_hoster, StringComparison.OrdinalIgnoreCase))
                          .ToList();

        if (pool.Count == 0)
        {
            return null;
        }

        // Aynı kalite birebir varsa kesin eşleşmedir.
        if (_quality is not null)
        {
            var exactQuality = pool.FirstOrDefault(s => s.Quality is not null
                                                       && s.Quality.Trim()
                                                           .Equals(_quality,
                                                                   StringComparison.OrdinalIgnoreCase));
            if (exactQuality is not null)
            {
                return exactQuality;
            }
        }

        // Kalite eşleşmedi: aynı tür varsa onu yeğle (MP4 istenmişken HLS dönme),
        // yoksa ilk uygun kaynağı al.
        if (_type != VideoType.Unknown)
        {
            var sameType = pool.FirstOrDefault(s => s.Type == _type);
            if (sameType is not null)
            {
                return sameType;
            }
        }

        return pool[0];
    }
}