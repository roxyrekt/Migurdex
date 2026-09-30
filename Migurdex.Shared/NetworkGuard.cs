using System.Net;
using System.Net.Sockets;

namespace Migurdex.Shared;

/// <summary>
/// Ağ geçidi (SSRF) koruması. Hem API'nin extractor ucu hem de CLI'nin indirici
/// yolu bu tek uygulamayı kullanır; iki tarafta ayrı kopya tutulması
/// güvenlik açığının kaynağıydı (API'de koruma vardı, CLI'de yoktu).
/// </summary>
public static class NetworkGuard
{
    private const string LocalhostName = "localhost";

    /// <summary>
    /// Verilen ana bilgisayar adı engelli bir adrese çözülüyor mu?
    ///
    /// DNS çözümlemesi başarısız olursa **izin verilir**. Aksi hâlde DNS'in
    /// geçici olarak çalışmaması, indirilebilir her kaynağı bozardı; API tarafı
    /// da aynı davranışı uygular.
    /// </summary>
    public static async Task<bool> ResolvesToBlockedAddressAsync(
        string            host,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return true;
        }

        if (string.Equals(host, LocalhostName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsBlockedAddress(literal);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }

        foreach (var address in addresses)
        {
            if (IsBlockedAddress(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Loopback, "herhangi bir adres" ve özel / bağlantı-yerel aralıkları engeller.
    /// </summary>
    public static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 0.0.0.0/8, 10/8, 172.16/12, 192.168/16, 169.254/16, 100.64/10
            return bytes[0] == 0
                   || bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }
}
