namespace Migurdex.Cli.Tui;

public class FuzzyChoice
{
    public string  Display         { get; set; } = string.Empty;
    public string  DisplayActive   { get; set; } = string.Empty;
    public string  Searchable      { get; set; } = string.Empty;
    public object? AssociatedValue { get; set; }

    public bool IsAction { get; set; }

    /// <summary>
    /// Çoklu seçimde bu satır işaretli mi?
    /// </summary>
    /// <remarks>
    /// Yalnızca <c>FuzzyPrompt.ShowMulti</c> içinde anlamlıdır; tekli seçimde
    /// hiç okunmaz. Değer <see cref="FuzzyChoice"/> nesnesinde tutulur, çünkü
    /// filtreleme sırasında liste yeniden sıralanır ve imleç konumu listeye göre
    /// değişir — işaretleme ise satırın kendisine bağlıdır.
    /// </remarks>
    public bool IsChecked { get; set; }

    /// <summary>
    /// Çoklu seçimde bu satır <c>Space</c> ile işaretlenebilir mi?
    /// </summary>
    /// <remarks>
    /// Aksiyon satırları (favoriye ekle, "tümünü işaretle", geri) gezinme
    /// komutlarıdır; işaretlenebilir olmaları anlamsız olur ve kullanıcı yanlış
    /// anlar. Varsayılan <see langword="false"/>'tur ve eylem satırlarında
    /// <see langword="true"/> yazılmaz — bu alan yalnızca seçilebilir bölüm
    /// satırlarında açılır.
    /// </remarks>
    public bool CanBeChecked { get; set; }
}