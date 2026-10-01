using System.Net;
using System.Net.Sockets;

namespace Migurdex.Tests;

/// <summary>
/// BULGU 51: OAuth loopback testleri dinleyici portunu <b>sabit</b> yazıyordu
/// (46497 / 46498 / 46499). CI'da <c>Test (ubuntu-22.04)</c> kırmızıydı:
///
/// <code>
/// AniListOAuthTests.Loopback_Receives_Code_From_Callback [FAIL]
/// System.Net.HttpListenerException : Address already in use
///    at System.Net.HttpEndPointManager.GetEPListener(...)
///    at System.Net.HttpEndPointManager.RemovePrefixInternal(...)
///    at System.Net.HttpListener.Close(Boolean force)
///    at System.Net.HttpListener.Dispose()
/// </code>
///
/// Yığın izi <c>Start()</c> değil <c>Dispose()</c>'ta olduğunu gösteriyor: dinleyici
/// açılmış, istek alınmış, <b>testin assert'leri geçmiş</b>, patlama yalnızca temizlikte
/// olmuş. <c>GetEPListener</c> kaldırırken portu yeniden bağlamayı deniyor ve port o an
/// dışarıdan tutulduğu için bağlanamıyor.
///
/// Düzeltme sabit port yerine işletim sisteminin verdiği boş bir port kullanmak.
/// Kırılma <b>main üzerinde de</b> yerel olarak yakalandı (603 testlik ağaç, 1/14 koşu),
/// yani PR'ın getirdiği bir hata değil; ama düzeltilmemiş de kalmamalı.
///
/// <para><b>Dürüstlük notu:</b> bu kırılma 14 koşuda 1 kez olduğu için "düzeltince yeşil,
/// geri alınca kırmızı" biçiminde bir kontrol deneyi <b>statistiksel olarak anlamsızdır</b>.
/// Onun yerine doğrudan belirleyici (deterministik) koruma testleri yazıldı:
/// <see cref="FreeLoopbackPortTests"/>. Sabit porta dönülürse onlar kesin kırmızı olur.</para>
/// </summary>
public static class FreeLoopbackPort
{
    /// <summary>
    /// Önceden sabit yazılmış portlar. Koruma testleri bunlara dönülmediğini
    /// doğruluyor; liste boşaltılırsa koruma testleri de anlamsızlaşır.
    /// </summary>
    public static readonly int[] EskiSabitPortlar = [46497, 46498, 46499];

    /// <summary>
    /// İşletim sistemine boş bir loopback portu sorar.
    /// <para>
    /// TOCTOU aralığı (prob dinleyicisi kapatılıp <c>HttpListener</c> açılana kadar)
    /// kapatılamaz — çalışma zamanında tahsis edilen portun tek garantisi bu.
    /// Sabit porta göre risk hâlâ dramatik biçimde düşük: 46499'u tutan şey artık
    /// ancak aynı anda o portu isteyen başka bir süreç olabilir.
    /// </para>
    /// </summary>
    public static int Next()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint) probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }
}
