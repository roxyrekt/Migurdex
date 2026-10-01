using Migurdex.Cli.Services;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Toplu indirmede "ilk bölümde seçilen tercihi sonraki bölümlerde kullan"
/// davranışının testleri. (<c>SourcePreference</c>)
/// </summary>
/// <remarks>
/// Bu sınıf dosya sistemi, ağ veya konsol gerektirmez; bu yüzden testleri
/// doğrudan ve hızlı koşar. Toplu indirme akışının geri kalanı (kaynak çekme,
/// sıralı indirme) ağ gerektirdiği için <c>ExtractorSmokeTests</c> gibi ayrı
/// tutulmamıştır — CI'da o testler filtrelenir, bu yüzden buraya konulan testler
/// <b>her koşuda</b> çalışır.
/// </remarks>
public class SourcePreferenceTests
{
    private static VideoSource Source(string hoster, string quality, VideoType type)
        => new()
        {
            Hoster  = hoster,
            Quality = quality,
            Type    = type,
            Url     = $"https://{hoster.ToLowerInvariant()}.example/{quality}/{type}"
        };

    /// <summary>Hoster alanı <see langword="null"/> olan kaynak.</summary>
    private static VideoSource SourceWithoutHoster()
        => new()
        {
            Hoster  = null,
            Quality = "720p",
            Type    = VideoType.Mp4,
            Url     = "https://anon.example/720p/mp4"
        };

    // ------------------------------------------------------------- temel eşleşme

    [Fact]
    public void Match_WithNoCapturedPreference_ReturnsNull()
    {
        var preference = new SourcePreference();

        var result = preference.Match([Source("TurkAnime", "720p", VideoType.Mp4)]);

        Assert.Null(result);
    }

    [Fact]
    public void Match_WithSameHosterAndQuality_ReturnsThatSource()
    {
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        var result = preference.Match([
            Source("AniHub",   "1080p", VideoType.Mp4),
            Source("TurkAnime", "1080p", VideoType.Mp4),
            Source("TurkAnime", "720p",  VideoType.Mp4)
        ]);

        Assert.NotNull(result);
        Assert.Equal("TurkAnime", result.Hoster);
        Assert.Equal("720p", result.Quality);
    }

    [Fact]
    public void Match_PrefersExactQualityOverFirstHosterMatch()
    {
        // Aynı hoster'da 1080p önce geliyor ama tercih 720p: tam eşleşme kazanmalı.
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        var result = preference.Match([
            Source("TurkAnime", "1080p", VideoType.Mp4),
            Source("TurkAnime", "720p",  VideoType.Mp4)
        ]);

        Assert.NotNull(result);
        Assert.Equal("720p", result.Quality);
    }

    [Fact]
    public void Match_IsCaseInsensitiveOnHosterAndQuality()
    {
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        var result = preference.Match([Source("TURKANIME", "720P", VideoType.Mp4)]);

        Assert.NotNull(result);
        Assert.Equal("TURKANIME", result.Hoster);
    }

    [Fact]
    public void Match_HosterNotPresent_ReturnsNullSoCallerFallsBackToAutoSort()
    {
        // Kaynak listesi bölümden bölüme değişir: 2. bölümde başka hoster olabilir.
        // Bu durumda null döner; çağıran otomatik sıralamaya düşer.
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        var result = preference.Match([Source("Animecix", "1080p", VideoType.Mp4)]);

        Assert.Null(result);
    }

    // ------------------------------------------------------------- kısmi eşleşme

    [Fact]
    public void Match_SameHosterDifferentQuality_FallsBackToSameType()
    {
        // 720p istenmiş ama o hoster'da yalnız 1080p var: tür eşleşen ilk kaynak seçilir.
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        var result = preference.Match([
            Source("TurkAnime", "1080p", VideoType.M3U8),
            Source("TurkAnime", "1080p", VideoType.Mp4)
        ]);

        Assert.NotNull(result);
        Assert.Equal(VideoType.Mp4, result.Type);
    }

    [Fact]
    public void Match_SameHosterDifferentQualityAndType_ReturnsFirstAvailable()
    {
        // Ne kalite ne tür tutmuyorsa hoster havuzundaki ilk kaynak kullanılır;
        // "hiç kaynak yok" demek, bölümü atlamaktan iyidir.
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        var result = preference.Match([Source("TurkAnime", "480p", VideoType.M3U8)]);

        Assert.NotNull(result);
        Assert.Equal("480p", result.Quality);
    }

    [Fact]
    public void Match_UnknownCapturedType_DoesNotFilterByType()
    {
        // Type bilinmiyorsa tür filtresi uygulanmamalı; ilk uygun kaynak döner.
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Unknown));

        var result = preference.Match([Source("TurkAnime", "360p", VideoType.M3U8)]);

        Assert.NotNull(result);
        Assert.Equal("360p", result.Quality);
    }

    // ------------------------------------------------------------------ kenar durum

    [Fact]
    public void Capture_WithBlankHoster_LeavesPreferenceUnusable()
    {
        // Hoster boşsa "tercih yok" sayılır; eşleştirme yapılmaz.
        var preference = new SourcePreference();
        preference.Capture(Source("   ", "720p", VideoType.Mp4));

        Assert.False(preference.HasPreference);
        Assert.Null(preference.Match([Source("TurkAnime", "720p", VideoType.Mp4)]));
    }

    [Fact]
    public void Capture_WithNullHoster_LeavesPreferenceUnusable()
    {
        var preference = new SourcePreference();
        preference.Capture(SourceWithoutHoster());

        Assert.False(preference.HasPreference);
    }

    [Fact]
    public void Match_WithNullHosterInCandidates_SkipsThem()
    {
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        var withoutHoster = Source("TurkAnime", "720p", VideoType.Mp4);
        withoutHoster.Hoster = null;

        var result = preference.Match([
            withoutHoster,
            Source("TurkAnime", "720p", VideoType.Mp4)
        ]);

        Assert.NotNull(result);
        Assert.Equal("TurkAnime", result.Hoster);
    }

    [Fact]
    public void Match_WithEmptyCandidateList_ReturnsNull()
    {
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        Assert.Null(preference.Match([]));
    }

    [Fact]
    public void Capture_CalledTwice_ReplacesPreferenceNotAccumulates()
    {
        // İlk bölümde TurkAnime, ikinci bölümde kullanıcı AniHub seçtiyse
        // tercih AniHub olmalı; iki hoster birden tutulmamalı.
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));
        preference.Capture(Source("AniHub", "1080p", VideoType.Mp4));

        var result = preference.Match([
            Source("TurkAnime", "720p",  VideoType.Mp4),
            Source("AniHub",    "1080p", VideoType.Mp4)
        ]);

        Assert.NotNull(result);
        Assert.Equal("AniHub", result.Hoster);
    }

    [Fact]
    public void Capture_ThrowsOnNullSource()
    {
        var preference = new SourcePreference();

        Assert.Throws<ArgumentNullException>(() => preference.Capture(null!));
    }

    [Fact]
    public void Match_ThrowsOnNullCandidateList()
    {
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        Assert.Throws<ArgumentNullException>(() => preference.Match(null!));
    }

    [Fact]
    public void HasPreference_IsFalseBeforeCapture_TrueAfter()
    {
        var preference = new SourcePreference();
        Assert.False(preference.HasPreference);

        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));
        Assert.True(preference.HasPreference);
    }

    // --------------------------------------------------------- bölümler arası tutarlılık

    [Fact]
    public void Match_PrefersPreferredHosterEvenWhenItIsLastInList()
    {
        // Kaynak listesi sırası garanti değildir; tercih edilen hoster sonda olsa da
        // bulunmalıdır. "İlkini al" kuralı burada yanlış sonuç verirdi.
        var preference = new SourcePreference();
        preference.Capture(Source("TurkAnime", "720p", VideoType.Mp4));

        var result = preference.Match([
            Source("Animecix",   "1080p", VideoType.Mp4),
            Source("AniHub",     "720p",  VideoType.Mp4),
            Source("TurkAnime",  "720p",  VideoType.Mp4)
        ]);

        Assert.NotNull(result);
        Assert.Equal("TurkAnime", result.Hoster);
    }

    [Fact]
    public void Match_TrimsSurroundingWhitespaceOnCapture()
    {
        var preference = new SourcePreference();
        preference.Capture(Source("  TurkAnime  ", "  720p  ", VideoType.Mp4));

        var result = preference.Match([Source("TurkAnime", "720p", VideoType.Mp4)]);

        Assert.NotNull(result);
    }
}