using Migurdex.Core.Services;
using Migurdex.Shared.Interfaces;
using Spectre.Console;

namespace Migurdex.Cli.Services;

public static class AuthCommand
{
    public static async Task<int> RunAsync(string[] args,
        OAuthTokenStore                             store,
        AniListOAuthClient                          aniListOAuth,
        MalOAuthClient                              malOAuth)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        return sub switch
        {
            "login"  => await LoginAsync(args[1..], store, aniListOAuth, malOAuth),
            "logout" => Logout(args[1..], store),
            "status" => Status(store),
            _        => Help()
        };
    }

    private static async Task<int> LoginAsync(string[] args,
        OAuthTokenStore                                store,
        AniListOAuthClient                             aniListOAuth,
        MalOAuthClient                                 malOAuth)
    {
        var target = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();
        if (string.IsNullOrEmpty(target) || target == "anilist")
        {
            return await RunFlowAsync("AniList",
                                      AniListAppCredentials.IsConfigured,
                                      AniListOAuthDefaults.LoopbackPort,
                                      AniListOAuthDefaults.CallbackPath,
                                      AniListOAuthDefaults.LoopbackRedirectUri,
                                      aniListOAuth,
                                      args,
                                      store);
        }

        if (target is "mal" or "myanimelist")
        {
            return await RunFlowAsync("MyAnimeList",
                                      MalAppCredentials.IsConfigured,
                                      MalAppCredentials.LoopbackPort,
                                      MalAppCredentials.CallbackPath,
                                      MalAppCredentials.LoopbackRedirectUri,
                                      malOAuth,
                                      args,
                                      store);
        }

        AnsiConsole.MarkupLine($"[red]Bilinmeyen servis:[/] {target}. Kullanılabilir: anilist, mal");
        return 1;
    }

    private static async Task<int> RunFlowAsync(string serviceName,
        bool                                           isConfigured,
        int                                            port,
        string                                         callbackPath,
        string                                         redirectUri,
        IOAuthFlow                                     flow,
        string[]                                       args,
        OAuthTokenStore                                store)
    {
        if (!isConfigured)
        {
            AnsiConsole.MarkupLine($"[red]{serviceName} istemci bilgisi eksik; giriş yapılamıyor.[/]");
            return 1;
        }

        var manual = args.Any(a => a.Equals("--manual", StringComparison.OrdinalIgnoreCase));
        var url    = flow.BuildAuthorizeUrl(redirectUri);

        string? code;
        if (manual)
        {
            AnsiConsole.MarkupLine("Tarayıcıda bu adresi açıp uygulamayı onaylayın:");
            AnsiConsole.WriteLine(url);
            AnsiConsole.Markup("[cyan]Kod:[/] ");
            code = Console.ReadLine()?.Trim();
        }
        else
        {
            BrowserHelper.OpenBrowser(url);
            AnsiConsole.MarkupLine($"Tarayıcıda {serviceName} onayı bekleniyor... "
                                   + "[grey](tarayıcı açılmadıysa --manual ile deneyin)[/]");
            code = await LoopbackCodeReceiver.WaitForCodeAsync(port, callbackPath, TimeSpan.FromMinutes(2));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            AnsiConsole.MarkupLine("[red]Yetki kodu alınamadı.[/]");
            return 1;
        }

        var token = await flow.ExchangeCodeAsync(code, redirectUri);
        if (token is null)
        {
            AnsiConsole.MarkupLine("[red]Token alınamadı; kodu kontrol edip tekrar deneyin.[/]");
            return 1;
        }

        store.Set(token);
        AnsiConsole.MarkupLine($"[green]{serviceName} bağlantısı kuruldu.[/]");
        return 0;
    }

    private static int Logout(string[] args, OAuthTokenStore store)
    {
        var target = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();

        if (target is "mal" or "myanimelist")
        {
            return DoLogout("MyAnimeList", WatchSyncService.MalProviderName, store);
        }

        if (target == "all")
        {
            DoLogout("AniList", WatchSyncService.AniListProviderName, store);
            DoLogout("MyAnimeList", WatchSyncService.MalProviderName, store);
            return 0;
        }

        return DoLogout("AniList", WatchSyncService.AniListProviderName, store);
    }

    private static int DoLogout(string name, string providerKey, OAuthTokenStore store)
    {
        if (store.Remove(providerKey))
        {
            AnsiConsole.MarkupLine($"[green]{name} çıkış yapıldı.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[grey]{name} zaten bağlı değil.[/]");
        }

        return 0;
    }

    private static int Status(OAuthTokenStore store)
    {
        PrintStatus("AniList", WatchSyncService.AniListProviderName, store);
        PrintStatus("MyAnimeList", WatchSyncService.MalProviderName, store);
        return 0;
    }

    private static void PrintStatus(string name, string providerKey, OAuthTokenStore store)
    {
        if (!store.TryGet(providerKey, out var token) || token is null)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{name}: bağlı değil.[/] [cyan]`migurdex auth login {providerKey}`[/] ile bağlanın.");
            return;
        }

        if (token.IsExpired)
        {
            AnsiConsole.MarkupLine($"[yellow]{name}: token süresi dolmuş; izleme kaydı kuyruğa alınıyor.[/]");
            AnsiConsole.MarkupLine($"[cyan]`migurdex auth login {providerKey}`[/] ile yeniden bağlanın.");
            return;
        }

        AnsiConsole.MarkupLine($"[green]{name}: bağlı.[/] [grey](son kullanma: {token.ExpiresAtUtc:u})[/]");
    }

    private static int Help()
    {
        AnsiConsole.WriteLine(
            "Kullanım: migurdex auth <login [anilist|mal] [--manual] | logout [anilist|mal|all] | status>");
        return 0;
    }
}
