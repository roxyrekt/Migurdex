using System.Collections.Concurrent;

namespace Migurdex.Shared.Models;

/// <summary>
///     Bir kaynak akışının (SSE) çözümleme sonucunu toplar: kaç kaynak gönderildi, hangi kaynaklar
///     çözümlenemedi ve bu hataların listesi. Eşzamanlı görevlerden beslendiği için thread-safe'tir.
///     Hataların tek yerde toplanması, akış sonu özetinin (<c>done</c>) tutarlı üretilmesini sağlar.
/// </summary>
public sealed class SourceExtractionReport
{
    /// <summary>
    ///     Per-source extractor hatalarının taşındığı <c>scope</c> değeri. SSE sözleşmesinde
    ///     <c>"search"</c> ve <c>"sources"</c> scope'larıyla birlikte bu değer de kullanılır.
    /// </summary>
    public const string Scope = "extract";

    public const string FailureMessage = "Kaynak çözümleme hatası.";

    private readonly ConcurrentDictionary<string, byte> _seenUrls =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentBag<DoneErrorItem> _errors = [];
    private int                               _succeeded;

    /// <summary>Akışta istemciye gönderilen (benzersiz) kaynak sayısı.</summary>
    public int Succeeded => Volatile.Read(ref _succeeded);

    /// <summary>Çözümlenemeyen kaynak sayısı; her hata için <see cref="Errors" />'a bir kayıt düşer.</summary>
    public int Failed => _errors.Count;

    public IReadOnlyList<DoneErrorItem> Errors => _errors.ToList();

    /// <summary>İşlenen toplam kalem sayısı: gönderilen kaynaklar + çözümlenemeyen kaynaklar.</summary>
    public int TotalItems => Succeeded + Failed;

    /// <summary>
    ///     Kaynağı akışa almaya çalışır. Boş URL'li veya daha önce gönderilmiş (duplicate) kaynaklar
    ///     sayılmaz; yeni ve gönderilebilir bir kaynak için başarı sayacını artırır.
    /// </summary>
    /// <returns><c>true</c> ise kaynak akışa yazılmalıdır.</returns>
    public bool TryTrackSource(VideoSource? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Url))
        {
            return false;
        }

        if (!_seenUrls.TryAdd(source.Url, 0))
        {
            return false;
        }

        Interlocked.Increment(ref _succeeded);
        return true;
    }

    /// <summary>
    ///     Bir kaynağın çözümlenemediğini kaydeder; hem <see cref="Failed" /> sayacını artırır hem de
    ///     <see cref="Errors" /> listesine <see cref="Scope" /> alanı <c>extract</c> olan bir hata ekler.
    /// </summary>
    public void RecordFailure(string provider, string? message = null)
    {
        _errors.Add(new DoneErrorItem(provider, Scope, message ?? FailureMessage));
    }

    /// <summary>Sayacın anlık görüntüsünü akış sonu özetine çevirir.</summary>
    public DoneSummary ToDoneSummary() => new(Succeeded, Failed, _errors.ToList(), TotalItems);
}
