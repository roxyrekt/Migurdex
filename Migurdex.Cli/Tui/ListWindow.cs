namespace Migurdex.Cli.Tui;

/// <summary>
/// Uzun listeleri terminal penceresine sığdıran saf pencereleme matematiği.
///
/// Neden gerekli: liste pencereden uzun çizilirse terminal kaydırır (scroll) ve canlı bölge
/// kareler arasında satır kaydırır; ekranın üstünde önceki karenin kalıntısı kalır, başlık
/// yukarı kaçar. Yalnızca sığan satırları çizmek kaydırmayı tamamen engeller.
/// <see cref="Views.SettingsView"/>, <see cref="Views.BatchDownloadView"/> ve
/// <see cref="EpisodeMultiSelectPrompt"/> aynı matematiği paylaşır; <c>ListWindowTests</c>
/// bu sınıfın sözünü doğrular.
/// </summary>
internal static class ListWindow
{
    /// <summary>
    /// Çerçevenin dışında kalan sabit satırlara yer bırakarak listeye ayrılabilecek satır sayısı.
    /// Terminal çok kısaysa en az 1 satır döner (taşma kabul edilir, boş ekran çizilmez).
    /// </summary>
    /// <param name="windowHeight">Terminalin görünür satır sayısı.</param>
    /// <param name="chromeRows">Liste dışındaki sabit satırlar (başlık, boşluklar, ipucu...).</param>
    /// <param name="maxRows">Liste için üst sınır (ör. toplu indirmede 12).</param>
    public static int Budget(int windowHeight, int chromeRows, int maxRows)
    {
        // -2: bir satır bölüm başlığını pencereye çekme payı, bir satır da son satıra yazılan
        // yeni satırın terminali kaydırmaması için güvenlik payı.
        return Math.Min(Math.Max(1, maxRows), Math.Max(1, windowHeight - chromeRows - 2));
    }

    /// <summary>
    /// İmleci her zaman içinde tutan <c>[Start, End)</c> penceresini döndürür. Pencere dışındaki
    /// satırlar hiç çizilmez; böylece ekran boyu içerik değişse bile terminal kaymaz.
    /// </summary>
    /// <param name="count">Toplam satır sayısı.</param>
    /// <param name="cursorIndex">Seçili satır; aralık dışındaysa kırpılır.</param>
    /// <param name="budget">Çizilebilecek en fazla satır (<see cref="Budget"/> çıktısı).</param>
    /// <param name="isSection">
    /// Satır bölüm başlığı mı? Pencere bir bölümün ortasından başlarsa başlık da pencerenin başına
    /// alınır; üstte sahipsiz seçenekler görünmez.
    /// </param>
    public static (int Start, int End) Compute(
        int              count,
        int              cursorIndex,
        int              budget,
        Func<int, bool>? isSection = null)
    {
        if (count <= 0)
        {
            return (0, 0);
        }

        var span = Math.Clamp(budget, 1, count);
        cursorIndex = Math.Clamp(cursorIndex, 0, count - 1);

        var start = Math.Max(0, cursorIndex - (span / 2));
        var end   = Math.Min(count, start + span);

        if (end - start < span)
        {
            start = Math.Max(0, end - span);
        }

        if (isSection is not null && start > 0 && !isSection(start) && isSection(start - 1))
        {
            // Bölüm başlığı da görünsün. Pencere en fazla bir satır büyür; imleç yine içeride
            // kalır (büyüme payı Budget'ın güvenlik payından karşılanır).
            start--;
            end = Math.Min(count, Math.Max(start + span, cursorIndex + 1));
        }

        return (start, end);
    }
}
