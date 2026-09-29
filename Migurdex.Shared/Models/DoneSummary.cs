namespace Migurdex.Shared.Models;

/// <summary>
///     Akış sonu (<c>done</c>) özetindeki tekil hata kaydı. <c>scope</c>, hatanın hangi işlemde
///     oluştuğunu belirtir (<c>search</c>, <c>sources</c>, <c>extract</c>).
/// </summary>
public sealed record DoneErrorItem(string Provider, string? Scope, string Error);

/// <summary>
///     Akış sonu (<c>done</c>) özeti: kaç öğe başarıyla işlendi, kaçı başarısız oldu, hatalar ve
///     işlenen toplam kalem sayısı.
/// </summary>
public sealed record DoneSummary(int Succeeded,
                                 int           Failed,
                                 List<DoneErrorItem> Errors,
                                 int?          TotalItems = null);
