namespace Migurdex.Cli.Tui;

/// <summary>
/// Toplu indirme ekranındaki tek satırlık ilerleme satırlarını terminal genişliğine sığdırır.
///
/// Neden gerekli: uzun bölüm adı + dosya yolu tek satıra sığmayınca terminal satırı kaydırır,
/// her kayıt iki fiziksel satıra düşer, canlı bölge pencereden büyür ve ekran kayar (üstte
/// kalıntı kalır). Bu yüzden kesme kararı burada — saf fonksiyon olarak — verilir;
/// <see cref="Views.BatchDownloadView"/> yalnızca renkleri ekler.
/// </summary>
internal static class BatchProgressLine
{
    public const string Separator = " • ";

    public const string Arrow = " → ";

    /// <summary>Etiket + ayrıntıyı <c>•</c> ayracıyla tek satıra sığdırır.</summary>
    /// <param name="prefix">Etiketten önce harcanmış görünür genişlik (işaret + boşluk).</param>
    public static (string Label, string Detail) Fit(string label, string detail, int prefix, int width)
    {
        return Fit(label, detail, prefix, Separator, width);
    }

    /// <summary>Tamamlanan satırlar için <c>→</c> ayraçlı sürüm.</summary>
    public static (string Label, string Detail) FitArrow(string label, string detail, int prefix, int width)
    {
        return Fit(label, detail, prefix, Arrow, width);
    }

    private static (string Label, string Detail) Fit(
        string label,
        string detail,
        int    prefix,
        string separator,
        int    width)
    {
        var budget = Math.Max(separator.Length + 4, width - Math.Max(0, prefix));
        if (string.IsNullOrEmpty(detail))
        {
            return (Theme.Ellipsize(label, budget), string.Empty);
        }

        // Ayrıntı (aşama, %, hız) etiketten daha değerli: önce ona yer ayrılır, etiket kırpılır.
        var minLabel     = Math.Min(24, Math.Max(4, budget / 3));
        var detailBudget = Math.Max(0, budget - separator.Length - minLabel);
        var detailText   = Theme.Ellipsize(detail, detailBudget);
        var labelBudget  = Math.Max(0, budget - separator.Length - detailText.Length);

        return (Theme.Ellipsize(label, labelBudget), detailText);
    }
}
