using Migurdex.Shared.Update;

namespace Migurdex.Cli.Services;

/// <summary>
/// Çalışan API'nin sürümünü CLI'ninkiyle karşılaştırır. CLI ve API aynı dağıtımın parçası
/// olduğu için ikisi arasındaki fark, kullanıcının istemediği bir karışıklıktır: 7045'te
/// eski bir API açıkken yeni CLI onu sessizce kullanıyor ve uç şekilleri tutmayabiliyor.
/// </summary>
public static class ApiVersionCheck
{
    /// <summary>Sürümlerden biri bilinmiyorsa uyuşmazlık sayılmaz (API sürümü okunamadı, dev build…).</summary>
    public static bool IsMismatch(string? apiVersion, string? cliVersion)
    {
        var api = ToVersionIdentity(apiVersion);
        var cli = ToVersionIdentity(cliVersion);
        if (string.IsNullOrWhiteSpace(api) || string.IsNullOrWhiteSpace(cli))
        {
            return false;
        }

        return !api.Equals(cli, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Build metadata (SemVer'de <c>+sha</c>) sürüm kimliğine girmez.</summary>
    private static string ToVersionIdentity(string? version)
    {
        return AppInfo.NormalizeTag(version).Split('+')[0].Trim();
    }

    public static string BuildWarningMessage(string? endpoint, string? apiVersion, string? cliVersion)
    {
        return $"Uyarı: {DescribeEndpoint(endpoint)} farklı bir Migurdex sürümü çalışıyor "
               + $"(API: {AppInfo.NormalizeTag(apiVersion)}, CLI: {AppInfo.NormalizeTag(cliVersion)}). "
               + "Bu API'yi kapatıp yeniden başlatman önerilir; aksi halde sonuçlar tutarsız olabilir.";
    }

    /// <summary>
    /// Sürüm okunamıyorsa veya eşleşiyorsa <c>null</c> döner; yalnız gerçek bir karışıklıkta uyarı metni üretir.
    /// </summary>
    public static async Task<string?> BuildWarningAsync(IApiClientService    api,
        string?                                                    endpoint,
        string?                                                    cliVersion,
        CancellationToken                                          cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);

        ApiHealthInfo? health;
        try
        {
            health = await api.GetApiHealthAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Uyarı üretmek asla bir işlemi bozmamalı.
            return null;
        }

        if (health is null || !IsMismatch(health.Version, cliVersion))
        {
            return null;
        }

        return BuildWarningMessage(endpoint, health.Version, cliVersion);
    }

    /// <summary>
    /// Uyuşmazlık varsa stderr'e uyarı yazar ve akışı bozmaz: API ayakta olduğu için
    /// kullanıcı işine devam edebilir, ama neden tutarsız sonuç gördüğünü bilmelidir.
    /// </summary>
    public static async Task ReportAsync(IApiClientService api,
        string?                                                    endpoint,
        CancellationToken                                          cancellationToken = default)
    {
        var warning = await BuildWarningAsync(api, endpoint, AppInfo.GetVersion(), cancellationToken);
        if (warning is not null)
        {
            Console.Error.WriteLine(warning);
        }
    }

    private static string DescribeEndpoint(string? endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && !uri.IsDefaultPort)
        {
            return $"Port {uri.Port}'te";
        }

        return string.IsNullOrWhiteSpace(endpoint) ? "API adresinde" : $"'{endpoint}' adresinde";
    }
}
