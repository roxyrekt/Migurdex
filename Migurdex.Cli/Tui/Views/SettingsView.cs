using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Core.Services;
using Migurdex.Shared.Enums;
using Spectre.Console;
using System.Text.Json;

namespace Migurdex.Cli.Tui.Views;

public class SettingsView : BaseView
{
    private static readonly string[]              _rpcTitleModes = ["Migurdex", "Sağlayıcı", "İçerik"];
    private readonly        AniListOAuthClient    _aniListOAuth;
    private readonly        IApiClientService     _apiClient;
    private readonly        IConfigurationService _configService;
    private readonly        MalOAuthClient        _malOAuth;
    private readonly        OAuthTokenStore       _tokenStore;
    private readonly        IUpdateService        _updateService;
    private                 string?               _lastProviderName;

    public SettingsView(IConfigurationService configService,
        IApiClientService                     apiClient,
        OAuthTokenStore                       tokenStore,
        AniListOAuthClient                    aniListOAuth,
        MalOAuthClient                        malOAuth,
        IUpdateService                        updateService)
    {
        _configService = configService;
        _apiClient     = apiClient;
        _tokenStore    = tokenStore;
        _aniListOAuth  = aniListOAuth;
        _malOAuth      = malOAuth;
        _updateService = updateService;
    }

    public override string GetRpcState()
    {
        return "Ayarlar";
    }

    public override async Task RenderAsync(ITuiNavigator navigator)
    {
        var settingsRunning = true;
        var cursorIndex     = 0;

        var items = new List<SettingItem>
        {
            new()
            {
                IsSection = true,
                Label     = "Oynatma"
            },
            new()
            {
                Id          = "AutoPlay",
                Label       = "Otomatik Oynat",
                ValueGetter = c => c.AutoSelectBestSource ? "Açık" : "Kapalı"
            },
            new()
            {
                Id          = "Timeout",
                Label       = "Bekleme Süresi",
                ValueGetter = c => $"{c.AutoSelectTimeoutSeconds:F1} sn"
            },
            new()
            {
                Id          = "PlayerLogs",
                Label       = "Oynatıcı Logları",
                ValueGetter = c => c.ShowPlayerLogs ? "Açık" : "Kapalı"
            },
            new()
            {
                IsSection = true,
                Label     = "Gizlilik"
            },
            new()
            {
                Id          = "Incognito",
                Label       = "Gizli Mod",
                ValueGetter = c => c.EnableIncognitoMode ? "Açık" : "Kapalı"
            },
            new()
            {
                IsSection = true,
                Label     = "Bildirim"
            },
            new()
            {
                Id          = "Rpc",
                Label       = "Discord RPC",
                ValueGetter = c => c.EnableDiscordRpc ? "Açık" : "Kapalı"
            },
            new()
            {
                Id          = "RpcTitle",
                Label       = "RPC Başlığı",
                ValueGetter = c => NormalizeRpcTitleMode(c.DiscordRpcTitleMode)
            },
            new()
            {
                IsSection = true,
                Label     = "Güncelleme"
            },
            new()
            {
                Id          = "UpdateCheck",
                Label       = "Güncelleme Kontrolü",
                ValueGetter = c => c.UpdateCheckEnabled ? "Açık" : "Kapalı"
            },
            new()
            {
                Id          = "UpdateChannel",
                Label       = "Güncelleme Kanalı",
                ValueGetter = c => UpdateService.IsPrereleaseChannel(c.UpdateChannel) ? "Pre-release" : "Stabil"
            },
            new()
            {
                Id       = "CheckUpdate",
                Label    = "Şimdi Kontrol Et...",
                IsAction = true
            },
            new()
            {
                IsSection = true,
                Label     = "Bağlantı"
            },
            new()
            {
                Id          = "Api",
                Label       = "API Adresi",
                ValueGetter = c => c.ApiBaseUrl
            },
            new()
            {
                Id          = "AniList",
                Label       = "AniList",
                ValueGetter = _ => AniListStatus()
            },
            new()
            {
                Id          = "MyAnimeList",
                Label       = "MyAnimeList",
                ValueGetter = _ => MalStatus()
            },
            new()
            {
                IsSection = true,
                Label     = "Liste"
            },
            new()
            {
                Id       = "Providers",
                Label    = "Sağlayıcı Yönetimi...",
                IsAction = true
            },
            new()
            {
                Id       = "Sorting",
                Label    = "Sıralama Öncelikleri...",
                IsAction = true
            },
            new()
            {
                IsSection = true,
                Label     = "Değişiklikler"
            },
            new()
            {
                Id       = "Save",
                Label    = "Kaydet ve Çık",
                IsAction = true
            },
            new()
            {
                Id       = "Cancel",
                Label    = "İptal",
                IsAction = true
            }
        };

        cursorIndex = NextSelectable(items, cursorIndex, 1);
        var baseline = SnapshotConfig();

        Grid BuildTable(CliConfig config)
        {
            var table = new Table().NoBorder().HideHeaders();
            table.AddColumn("Label", c => c.Width(25));
            table.AddColumn("Value");

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.IsSection)
                {
                    table.AddRow($"[cyan]{Markup.Escape(item.Label)}[/]", "");
                    continue;
                }

                var isSelected = i == cursorIndex;

                var labelPrefix = isSelected ? "› " : "  ";
                var labelStyle  = isSelected ? "bold white on grey23" : "grey";

                if (item.IsAction)
                {
                    var actionStyle = isSelected ? "bold yellow" : "yellow";
                    if (item.Id == "Cancel")
                    {
                        actionStyle = isSelected ? "bold red" : "red";
                    }

                    if (item.Id == "Save")
                    {
                        actionStyle = isSelected ? "bold green" : "green";
                    }

                    table.AddRow($"[{actionStyle}]{labelPrefix}{item.Label}[/]", "");
                }
                else
                {
                    var val        = item.ValueGetter(config);
                    var valueStyle = GetValueStyle(item.Id, val, isSelected);
                    table.AddRow($"[{labelStyle}]{labelPrefix}{item.Label}[/]",
                                 $"[{valueStyle}][[{Markup.Escape(val)}]][/]");
                }
            }

            var grid = new Grid();
            grid.AddColumn();
            grid.AddRow(table);
            grid.AddRow(new Text(string.Empty));
            grid.AddRow(new Markup("[grey]↑↓ gez • Enter değiştir • Esc geri[/]"));
            return grid;
        }

        while (settingsRunning)
        {
            AnsiConsole.Clear();
            Theme.WriteHeader("Ayarlar");
            AnsiConsole.WriteLine();

            SettingItem? pendingSelection = null;
            var          pendingEscape    = false;

            await AnsiConsole.Live(BuildTable(_configService.Config))
                             .StartAsync(async ctx =>
                             {
                                 var last = string.Empty;
                                 while (settingsRunning && pendingSelection is null && !pendingEscape)
                                 {
                                     var config = _configService.Config;
                                     var fp     = cursorIndex + "|" + SnapshotConfig();
                                     if (!fp.Equals(last, StringComparison.Ordinal))
                                     {
                                         ctx.UpdateTarget(BuildTable(config));
                                         last = fp;
                                     }

                                     if (Console.KeyAvailable)
                                     {
                                         var key = Console.ReadKey(true);
                                         switch (key.Key)
                                         {
                                             case ConsoleKey.UpArrow:
                                                 cursorIndex = NextSelectable(items, cursorIndex, -1);
                                                 break;
                                             case ConsoleKey.DownArrow:
                                                 cursorIndex = NextSelectable(items, cursorIndex, 1);
                                                 break;
                                             case ConsoleKey.Enter:
                                                 var selected = items[cursorIndex];
                                                 if (!selected.IsSection)
                                                 {
                                                     pendingSelection = selected;
                                                 }

                                                 break;
                                             case ConsoleKey.Escape:
                                                 pendingEscape = true;
                                                 break;
                                         }
                                     }
                                     else
                                     {
                                         await Task.Delay(15);
                                     }
                                 }
                             });

            if (pendingEscape && settingsRunning)
            {
                if (SnapshotConfig() == baseline
                    || Theme.Confirm("[red]Kaydedilmeden çıkılsın mı?[/]", false))
                {
                    _configService.Reload();
                    settingsRunning = false;
                    navigator.Pop();
                }
            }
            else if (pendingSelection is not null && settingsRunning)
            {
                if (await HandleSelectionAsync(pendingSelection, _configService.Config, navigator))
                {
                    settingsRunning = false;
                }
            }
        }
    }

    private string SnapshotConfig()
    {
        try
        {
            return JsonSerializer.Serialize(_configService.Config);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeRpcTitleMode(string? mode)
    {
        return _rpcTitleModes.Contains(mode) ? mode! : "İçerik";
    }

    private string AniListStatus()
    {
        if (!_tokenStore.TryGet(WatchSyncService.ProviderName, out var token) || token is null)
        {
            return "Bağlı değil";
        }

        if (token.IsExpired)
        {
            return "Süresi dolmuş";
        }

        return token.NeedsRefresh ? "Bağlı (yenilenecek)" : "Bağlı";
    }

    private string AniListDetail()
    {
        if (!_tokenStore.TryGet(WatchSyncService.ProviderName, out var token) || token is null)
        {
            return "AniList bağlı değil. Bağlanmak için Enter'a basın.";
        }

        if (token.IsExpired)
        {
            return "AniList token süresi dolmuş. Yenilemek için Enter'a basın.";
        }

        return $"AniList bağlı. Token son kullanma: {token.ExpiresAtUtc:u}.";
    }

    private static async Task<string?> WaitForLoopbackCodeAsync(int port, string callbackPath, string serviceName)
    {
        string? code = null;
        using (var cts = new CancellationTokenSource())
        {
            var waitTask = LoopbackCodeReceiver.WaitForCodeAsync(port,
                                                                 callbackPath,
                                                                 TimeSpan.FromMinutes(2),
                                                                 cancellationToken: cts.Token);
            while (!waitTask.IsCompleted)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                {
                    cts.Cancel();
                    Toast.Show("[grey]Vazgeçildi.[/]");
                    return null;
                }

                try
                {
                    await Task.Delay(200, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (waitTask.Status == TaskStatus.RanToCompletion)
            {
                code = await waitTask;
            }
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            code = Theme.Ask("Kod (boş = vazgeç):", string.Empty)?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                return null;
            }
        }

        return code;
    }

    private async Task RunAniListLoginAsync()
    {
        if (!AniListAppCredentials.IsConfigured)
        {
            Toast.Show("[red]AniList istemci bilgisi eksik; giriş yapılamıyor.[/]");
            return;
        }

        if (_tokenStore.TryGet(WatchSyncService.ProviderName, out var existing)
            && existing is not null
            && !existing.IsExpired)
        {
            Toast.Show(AniListDetail());
            if (Theme.Confirm("AniList bağlantısı kesilsin mi?", false))
            {
                _tokenStore.Remove(WatchSyncService.ProviderName);
                Toast.Show("[green]AniList çıkış yapıldı.[/]");
            }

            return;
        }

        var redirectUri = AniListOAuthDefaults.LoopbackRedirectUri;
        BrowserHelper.OpenBrowser(_aniListOAuth.BuildAuthorizeUrl(redirectUri));

        AnsiConsole.MarkupLine("[cyan]Tarayıcıda AniList onayını verin.[/] [grey](Esc: vazgeç)[/]");

        var code = await WaitForLoopbackCodeAsync(AniListOAuthDefaults.LoopbackPort,
                                                  AniListOAuthDefaults.CallbackPath,
                                                  "AniList");
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var token = await _aniListOAuth.ExchangeCodeAsync(code, redirectUri);
        if (token is null)
        {
            Toast.Show("[red]Token alınamadı; tekrar deneyin.[/]");
            return;
        }

        _tokenStore.Set(token);
        Toast.Show("[green]AniList bağlantısı kuruldu.[/]");
    }

    private string MalStatus()
    {
        if (!_tokenStore.TryGet(WatchSyncService.MalProviderName, out var token) || token is null)
        {
            return "Bağlı değil";
        }

        if (token.IsExpired)
        {
            return "Süresi dolmuş";
        }

        return token.NeedsRefresh ? "Bağlı (yenilenecek)" : "Bağlı";
    }

    private string MalDetail()
    {
        if (!_tokenStore.TryGet(WatchSyncService.MalProviderName, out var token) || token is null)
        {
            return "MyAnimeList bağlı değil. Bağlanmak için Enter'a basın.";
        }

        if (token.IsExpired)
        {
            return "MyAnimeList token süresi dolmuş. Yenilemek için Enter'a basın.";
        }

        return $"MyAnimeList bağlı. Token son kullanma: {token.ExpiresAtUtc:u}.";
    }

    private async Task RunMalLoginAsync()
    {
        if (!MalAppCredentials.IsConfigured)
        {
            Toast.Show("[red]MyAnimeList istemci bilgisi eksik; giriş yapılamıyor.[/]");
            return;
        }

        if (_tokenStore.TryGet(WatchSyncService.MalProviderName, out var existing)
            && existing is not null
            && !existing.IsExpired)
        {
            Toast.Show(MalDetail());
            if (Theme.Confirm("MyAnimeList bağlantısı kesilsin mi?", false))
            {
                _tokenStore.Remove(WatchSyncService.MalProviderName);
                Toast.Show("[green]MyAnimeList çıkış yapıldı.[/]");
            }

            return;
        }

        var redirectUri = MalAppCredentials.LoopbackRedirectUri;
        BrowserHelper.OpenBrowser(_malOAuth.BuildAuthorizeUrl(redirectUri));

        AnsiConsole.MarkupLine("[cyan]Tarayıcıda MyAnimeList onayını verin.[/] [grey](Esc: vazgeç)[/]");

        var code = await WaitForLoopbackCodeAsync(MalAppCredentials.LoopbackPort,
                                                  MalAppCredentials.CallbackPath,
                                                  "MyAnimeList");
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        var token = await _malOAuth.ExchangeCodeAsync(code, redirectUri);
        if (token is null)
        {
            Toast.Show("[red]Token alınamadı; tekrar deneyin.[/]");
            return;
        }

        _tokenStore.Set(token);
        Toast.Show("[green]MyAnimeList bağlantısı kuruldu.[/]");
    }

    private static string NextRpcTitleMode(string? current)
    {
        var idx = Array.IndexOf(_rpcTitleModes, NormalizeRpcTitleMode(current));
        return _rpcTitleModes[(idx + 1) % _rpcTitleModes.Length];
    }

    private async Task RunUpdateCheckNowAsync()
    {
        UpdateCheckResult? result = null;
        await AnsiConsole.Status()
                         .Spinner(Spinner.Known.Dots)
                         .StartAsync("Sürüm kontrol ediliyor...",
                                     async _ =>
                                     {
                                         result = await _updateService.CheckForUpdatesAsync(true);
                                     });

        if (result is null)
        {
            Toast.Show("[red]Sürüm kontrolü yapılamadı (çevrimdışı olabilir).[/]");
            return;
        }

        if (!result.IsUpdateAvailable)
        {
            Toast.Show($"[green]Zaten güncelsin (v{Markup.Escape(result.CurrentVersion)}).[/]");
            return;
        }

        Toast.Show($"[yellow]Yeni sürüm:[/] v{Markup.Escape(result.CurrentVersion)} → "
                   + $"v{Markup.Escape(result.LatestVersion)}. "
                   + "Çıkıp [cyan]migurdex update[/] ile kurun.");
    }

    private async Task<bool> HandleSelectionAsync(SettingItem item, CliConfig config, ITuiNavigator navigator)
    {
        switch (item.Id)
        {
            case "AutoPlay":
                config.AutoSelectBestSource = !config.AutoSelectBestSource;
                break;
            case "Timeout":
                config.AutoSelectTimeoutSeconds =
                    Theme.Ask("Bekleme süresi (sn):", config.AutoSelectTimeoutSeconds);

                if (config.AutoSelectTimeoutSeconds < 0.2)
                {
                    config.AutoSelectTimeoutSeconds = 0.2;
                }

                if (config.AutoSelectTimeoutSeconds > 120.0)
                {
                    config.AutoSelectTimeoutSeconds = 120.0;
                }

                break;
            case "Rpc":
                config.EnableDiscordRpc = !config.EnableDiscordRpc;
                break;
            case "RpcTitle":
                config.DiscordRpcTitleMode = NextRpcTitleMode(config.DiscordRpcTitleMode);
                break;
            case "Incognito":
                config.EnableIncognitoMode = !config.EnableIncognitoMode;
                break;
            case "PlayerLogs":
                config.ShowPlayerLogs = !config.ShowPlayerLogs;
                break;
            case "UpdateCheck":
                config.UpdateCheckEnabled = !config.UpdateCheckEnabled;
                break;
            case "UpdateChannel":
                config.UpdateChannel =
                    UpdateService.IsPrereleaseChannel(config.UpdateChannel) ? "stable" : "prerelease";
                config.SkippedVersion = null;
                break;
            case "CheckUpdate":
                await RunUpdateCheckNowAsync();
                break;
            case "Api":
                var apiUrl = (Theme.Ask("API adresi:", config.ApiBaseUrl ?? string.Empty) ?? string.Empty).Trim()
                    .TrimEnd('/');
                if (string.IsNullOrEmpty(apiUrl))
                {
                    Toast.Show("[red]Adres boş olamaz. Değişiklik yapılmadı.[/]");
                    break;
                }

                if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var apiUri)
                    || (apiUri.Scheme != Uri.UriSchemeHttp && apiUri.Scheme != Uri.UriSchemeHttps))
                {
                    Toast.Show("[red]Geçersiz adres. http:// veya https:// ile başlamalı. Değişiklik yapılmadı.[/]");
                    break;
                }

                config.ApiBaseUrl = apiUrl;
                break;
            case "AniList":
                await RunAniListLoginAsync();
                break;
            case "MyAnimeList":
                await RunMalLoginAsync();
                break;
            case "Providers":
                await ConfigureProvidersAsync(config);
                break;
            case "Sorting":
                await ConfigureSortingPrioritiesAsync(config);
                break;
            case "Save":
                _configService.Save();
                Toast.Show("[green]Kaydedildi.[/]");
                navigator.Pop();
                return true;
            case "Cancel":
                _configService.Reload();
                navigator.Pop();
                return true;
        }

        return false;
    }

    private async Task ConfigureProvidersAsync(CliConfig config)
    {
        var active = true;

        ApiResult<IReadOnlyList<ProviderInfo>>? providersResult = null;
        await AnsiConsole.Status()
                         .Spinner(Spinner.Known.Dots)
                         .StartAsync("Sağlayıcılar yükleniyor...",
                                     async _ =>
                                     {
                                         providersResult = await _apiClient.GetProvidersAsync();
                                     });

        var providers   = providersResult!.Data;
        var cursorIndex = 0;

        if (_lastProviderName is not null)
        {
            for (var i = 0; i < providers.Count; i++)
            {
                if (providers[i].Name.Equals(_lastProviderName, StringComparison.OrdinalIgnoreCase))
                {
                    cursorIndex = i;
                    break;
                }
            }
        }

        if (providers.Count == 0)
        {
            AnsiConsole.Clear();
            Theme.WriteHeader("Sağlayıcı yönetimi");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]Sağlayıcı listesi alınamadı.[/]");
            if (providersResult.Error is not null)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(providersResult.Error)}[/]");
            }

            Console.ReadKey(true);
            return;
        }

        Grid BuildGrid()
        {
            var table = new Table().NoBorder().HideHeaders();
            table.AddColumn("Name", c => c.Width(25));
            table.AddColumn("Status");

            for (var i = 0; i < providers.Count; i++)
            {
                var p          = providers[i];
                var isSelected = i == cursorIndex;
                var isDisabled = config.DisabledProviders.Contains(p.Name, StringComparer.OrdinalIgnoreCase);

                var labelPrefix = isSelected ? "› " : "  ";
                var labelStyle  = isSelected ? "bold white on grey23" : "grey";
                var statusText  = isDisabled ? "Kapalı" : "Açık";
                var statusStyle =
                    isDisabled
                        ? isSelected ? "bold red" : "red"
                        : isSelected
                            ? "bold green"
                            : "green";

                table.AddRow($"[{labelStyle}]{labelPrefix}{p.Name}[/]", $"[{statusStyle}][[{statusText}]][/]");
            }

            table.AddRow("", "");
            var backLabel = cursorIndex == providers.Count ? "› Geri" : "  Geri";
            var backStyle = cursorIndex == providers.Count ? "bold yellow" : "yellow";
            table.AddRow($"[{backStyle}]{backLabel}[/]", "");

            var grid = new Grid();
            grid.AddColumn();
            grid.AddRow(table);
            grid.AddRow(new Text(string.Empty));
            grid.AddRow(new Markup("[grey]↑↓ gez • Enter değiştir • Esc geri[/]"));
            return grid;
        }

        string Fingerprint() => cursorIndex + "|" + string.Join(",", config.DisabledProviders);

        AnsiConsole.Clear();
        Theme.WriteHeader("Sağlayıcı yönetimi");
        AnsiConsole.WriteLine();

        AnsiConsole.Live(BuildGrid())
                   .Start(ctx =>
                   {
                       var last = string.Empty;
                       while (active)
                       {
                           var fp = Fingerprint();
                           if (!fp.Equals(last, StringComparison.Ordinal))
                           {
                               ctx.UpdateTarget(BuildGrid());
                               last = fp;
                           }

                           if (Console.KeyAvailable)
                           {
                               var key = Console.ReadKey(true);
                               switch (key.Key)
                               {
                                   case ConsoleKey.UpArrow:
                                       cursorIndex = (cursorIndex - 1 + providers.Count + 1) % (providers.Count + 1);
                                       break;
                                   case ConsoleKey.DownArrow:
                                       cursorIndex = (cursorIndex + 1) % (providers.Count + 1);
                                       break;
                                   case ConsoleKey.Enter:
                                       if (cursorIndex == providers.Count)
                                       {
                                           active = false;
                                       }
                                       else
                                       {
                                           var p = providers[cursorIndex];
                                           _lastProviderName = p.Name;
                                           if (config.DisabledProviders.Contains(
                                                   p.Name,
                                                   StringComparer.OrdinalIgnoreCase))
                                           {
                                               config.DisabledProviders.RemoveAll(x => x.Equals(p.Name,
                                                   StringComparison.OrdinalIgnoreCase));
                                           }
                                           else
                                           {
                                               config.DisabledProviders.Add(p.Name);
                                           }
                                       }

                                       break;
                                   case ConsoleKey.Escape:
                                       active = false;
                                       break;
                               }
                           }
                           else
                           {
                               Thread.Sleep(15);
                           }
                       }
                   });
    }

    private async Task ConfigureSortingPrioritiesAsync(CliConfig config)
    {
        var active = true;
        while (active)
        {
            AnsiConsole.Clear();
            Theme.WriteHeader("Sıralama öncelikleri");
            AnsiConsole.WriteLine();

            var choices = new List<FuzzyChoice>
            {
                new()
                {
                    Display       = "[silver]Genel Kategori[/]",
                    DisplayActive = "[bold white]Genel Kategori[/]",
                    Searchable    = "Genel Kategori"
                },
                new()
                {
                    Display       = "[silver]Çözünürlük[/]",
                    DisplayActive = "[bold white]Çözünürlük[/]",
                    Searchable    = "Çözünürlük"
                },
                new()
                {
                    Display       = "[silver]Format[/]",
                    DisplayActive = "[bold white]Format[/]",
                    Searchable    = "Format"
                },
                new()
                {
                    Display       = "[silver]Sunucu/Oynatıcı[/]",
                    DisplayActive = "[bold white]Sunucu/Oynatıcı[/]",
                    Searchable    = "Sunucu/Oynatıcı"
                },
                new()
                {
                    Display =
                        $"[silver]Otomatik: Sunucular[/]{RuleSummary(config.AutoNeverHosters, config.AutoOnlyHosters)}",
                    DisplayActive =
                        $"[bold white]Otomatik: Sunucular[/]{RuleSummary(config.AutoNeverHosters, config.AutoOnlyHosters)}",
                    Searchable = "Otomatik: Sunucular"
                },
                new()
                {
                    Display =
                        $"[silver]Otomatik: Kaliteler[/]{RuleSummary(config.AutoNeverQualities, config.AutoOnlyQualities)}",
                    DisplayActive =
                        $"[bold white]Otomatik: Kaliteler[/]{RuleSummary(config.AutoNeverQualities, config.AutoOnlyQualities)}",
                    Searchable = "Otomatik: Kaliteler"
                },
                new()
                {
                    Display =
                        $"[silver]Otomatik: Türler[/]{RuleSummary(config.AutoNeverTypes, config.AutoOnlyTypes)}",
                    DisplayActive =
                        $"[bold white]Otomatik: Türler[/]{RuleSummary(config.AutoNeverTypes, config.AutoOnlyTypes)}",
                    Searchable = "Otomatik: Türler"
                },
                new()
                {
                    Display       = "[red]Geri[/]",
                    DisplayActive = "[bold red]Geri[/]",
                    Searchable    = "Geri"
                }
            };
            var sortChoice = FuzzyPrompt.Show("Alan seçin:", choices);

            if (sortChoice == null || sortChoice.Searchable == "Geri")
            {
                active = false;
                break;
            }

            switch (sortChoice.Searchable)
            {
                case "Genel Kategori":
                    {
                        var items = config.SourceSortPriority.Select(p => new ReorderItem
                                          {
                                              Key = p,
                                              DisplayName = p switch
                                              {
                                                  "Quality" => "Çözünürlük (Quality)",
                                                  "Format"  => "Akış Formatı (M3U8/Mp4)",
                                                  "Hoster"  => "Sunucu (GoogleDrive/Vidmoly vb.)",
                                                  "Group"   => "Fansub (Group)",
                                                  _         => p
                                              }
                                          })
                                          .ToList();
                        var result = ReorderPrompt.Show("Kategori Sıralaması", items);
                        if (result != null)
                        {
                            config.SourceSortPriority = [.. result.Select(r => r.Key)];
                        }
                    }
                    break;

                case "Çözünürlük":
                    {
                        var items = config.PreferredQualityOrder.Select(q => new ReorderItem
                                          {
                                              Key         = q,
                                              DisplayName = q
                                          })
                                          .ToList();
                        var result = ReorderPrompt.Show("Çözünürlük Sıralaması", items);
                        if (result != null)
                        {
                            config.PreferredQualityOrder = [.. result.Select(r => r.Key)];
                        }
                    }
                    break;

                case "Format":
                    {
                        var items = config.PreferredFormatOrder.Select(f => new ReorderItem
                                          {
                                              Key         = f,
                                              DisplayName = f
                                          })
                                          .ToList();
                        var result = ReorderPrompt.Show("Format Sıralaması", items);
                        if (result != null)
                        {
                            config.PreferredFormatOrder = [.. result.Select(r => r.Key)];
                        }
                    }
                    break;

                case "Sunucu/Oynatıcı":
                    {
                        var mergedHosters = await GetMergedHostersAsync();
                        var items = mergedHosters.Select(h => new ReorderItem
                                                 {
                                                     Key         = h,
                                                     DisplayName = h
                                                 })
                                                 .ToList();
                        var result = ReorderPrompt.Show("Sunucu Sıralaması", items);
                        if (result != null)
                        {
                            config.PreferredHosterOrder = [.. result.Select(r => r.Key)];
                        }
                    }
                    break;

                case "Otomatik: Sunucular":
                    ConfigureAutoList("Otomatik: Sunucular",
                                      await GetMergedHostersAsync(),
                                      config.AutoNeverHosters,
                                      config.AutoOnlyHosters);
                    break;

                case "Otomatik: Kaliteler":
                    ConfigureAutoList("Otomatik: Kaliteler",
                                      [.. config.PreferredQualityOrder],
                                      config.AutoNeverQualities,
                                      config.AutoOnlyQualities);
                    break;

                case "Otomatik: Türler":
                    ConfigureAutoList("Otomatik: Türler",
                                      [
                                          .. Enum.GetNames<VideoType>()
                                                 .Where(n => !n.Equals(nameof(VideoType.Embed),
                                                                       StringComparison.OrdinalIgnoreCase))
                                      ],
                                      config.AutoNeverTypes,
                                      config.AutoOnlyTypes);
                    break;
            }
        }
    }

    private async Task<List<string>> GetMergedHostersAsync()
    {
        var config = _configService.Config;

        ApiResult<IReadOnlyList<string>>? extractorsResult = null;
        await AnsiConsole.Status()
                         .Spinner(Spinner.Known.Dots)
                         .StartAsync("Sunucu listesi yükleniyor...",
                                     async _ =>
                                     {
                                         extractorsResult =
                                             await _apiClient.GetExtractorsAsync();
                                     });

        var mergedHosters = new List<string>(config.PreferredHosterOrder);
        foreach (var ext in extractorsResult!.Data)
        {
            if (!mergedHosters.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                mergedHosters.Add(ext);
            }
        }

        return mergedHosters;
    }

    private static string RuleSummary(List<string> neverList, List<string> onlyList)
    {
        var parts = new List<string>();
        if (neverList.Count > 0)
        {
            parts.Add($"{neverList.Count} asla");
        }

        if (onlyList.Count > 0)
        {
            parts.Add($"{onlyList.Count} sadece");
        }

        return parts.Count == 0 ? string.Empty : $" [grey]({string.Join(", ", parts)})[/]";
    }

    private static string AutoRuleState(string item, List<string> neverList, List<string> onlyList)
    {
        if (neverList.Contains(item, StringComparer.OrdinalIgnoreCase))
        {
            return "Asla";
        }

        return onlyList.Contains(item, StringComparer.OrdinalIgnoreCase) ? "Sadece" : "Otomatik";
    }

    private static void CycleAutoRule(string item, List<string> neverList, List<string> onlyList)
    {
        if (neverList.RemoveAll(x => x.Equals(item, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            if (!onlyList.Contains(item, StringComparer.OrdinalIgnoreCase))
            {
                onlyList.Add(item);
            }
        }
        else if (onlyList.RemoveAll(x => x.Equals(item, StringComparison.OrdinalIgnoreCase)) > 0)
        {
        }
        else if (!neverList.Contains(item, StringComparer.OrdinalIgnoreCase))
        {
            neverList.Add(item);
        }
    }

    private static void ConfigureAutoList(string title,
        List<string>                             allItems,
        List<string>                             neverList,
        List<string>                             onlyList)
    {
        var cursorIndex = 0;
        var running     = true;

        Grid BuildGrid()
        {
            var table = new Table().NoBorder().HideHeaders();
            table.AddColumn("Name", c => c.Width(25));
            table.AddColumn("Status");

            for (var i = 0; i < allItems.Count; i++)
            {
                var item       = allItems[i];
                var isSelected = i == cursorIndex;
                var state      = AutoRuleState(item, neverList, onlyList);

                var labelPrefix = isSelected ? "› " : "  ";
                var labelStyle  = isSelected ? "bold white on grey23" : "grey";
                var stateText = state switch
                {
                    "Asla"   => "Asla",
                    "Sadece" => "Sadece",
                    _        => "Otomatik"
                };
                var stateStyle = state switch
                {
                    "Asla"   => isSelected ? "bold red" : "red",
                    "Sadece" => isSelected ? "bold yellow" : "yellow",
                    _        => isSelected ? "bold green" : "green"
                };

                table.AddRow($"[{labelStyle}]{labelPrefix}{Markup.Escape(item)}[/]",
                             $"[{stateStyle}][[{stateText}]][/]");
            }

            table.AddRow("", "");
            var backLabel = cursorIndex == allItems.Count ? "› Geri" : "  Geri";
            var backStyle = cursorIndex == allItems.Count ? "bold yellow" : "yellow";
            table.AddRow($"[{backStyle}]{backLabel}[/]", "");

            var grid = new Grid();
            grid.AddColumn();
            grid.AddRow(table);
            grid.AddRow(new Text(string.Empty));
            grid.AddRow(new Markup("[grey]Enter değiştir (Otomatik → Asla → Sadece) • Esc geri[/]"));
            return grid;
        }

        string Fingerprint() => cursorIndex + "|" + string.Join(",", neverList) + "|" + string.Join(",", onlyList);

        AnsiConsole.Clear();
        Theme.WriteHeader(title);
        AnsiConsole.MarkupLine(
            "[green]Otomatik:[/] [grey]kural yok ·[/] [red]Asla:[/] [grey]otomatik seçilmez ·[/] [yellow]Sadece:[/] [grey]yalnız işaretliler otomatik seçilir[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.Live(BuildGrid())
                   .Start(ctx =>
                   {
                       var last = string.Empty;
                       while (running)
                       {
                           var fp = Fingerprint();
                           if (!fp.Equals(last, StringComparison.Ordinal))
                           {
                               ctx.UpdateTarget(BuildGrid());
                               last = fp;
                           }

                           if (Console.KeyAvailable)
                           {
                               var key = Console.ReadKey(true);
                               switch (key.Key)
                               {
                                   case ConsoleKey.UpArrow:
                                       cursorIndex = (cursorIndex - 1 + allItems.Count + 1) % (allItems.Count + 1);
                                       break;
                                   case ConsoleKey.DownArrow:
                                       cursorIndex = (cursorIndex + 1) % (allItems.Count + 1);
                                       break;
                                   case ConsoleKey.Enter:
                                       if (cursorIndex == allItems.Count)
                                       {
                                           running = false;
                                       }
                                       else
                                       {
                                           CycleAutoRule(allItems[cursorIndex], neverList, onlyList);
                                       }

                                       break;
                                   case ConsoleKey.Escape:
                                       running = false;
                                       break;
                               }
                           }
                           else
                           {
                               Thread.Sleep(15);
                           }
                       }
                   });

        AnsiConsole.Clear();
    }

    private static int NextSelectable(List<SettingItem> items, int from, int direction)
    {
        if (items.Count == 0)
        {
            return from;
        }

        var idx = from;
        for (var step = 0; step < items.Count; step++)
        {
            idx = (idx + direction + items.Count) % items.Count;
            if (!items[idx].IsSection)
            {
                return idx;
            }
        }

        return from;
    }

    private static string GetValueStyle(string id, string value, bool isSelected)
    {
        var bold = isSelected ? "bold " : "";
        var color = id switch
        {
            "AutoPlay" or "Rpc" or "Incognito" or "PlayerLogs" or "UpdateCheck" =>
                value == "Açık" ? "green" : "grey",
            "UpdateChannel" => value == "Pre-release" ? "yellow" : "green",
            "AniList" or "MyAnimeList" => value.Contains("Süresi dolmuş")
                                              ? "red"
                                              : value.StartsWith("Bağlı (yenilenecek)")
                                                  ? "yellow"
                                                  : value.StartsWith("Bağlı")
                                                      ? "green"
                                                      : "grey",
            _ => isSelected ? "white" : "silver"
        };

        return $"{bold}{color}";
    }

    private class SettingItem
    {
        public string                  Id          { get; set; } = string.Empty;
        public string                  Label       { get; set; } = string.Empty;
        public Func<CliConfig, string> ValueGetter { get; set; } = _ => string.Empty;
        public bool                    IsAction    { get; set; }
        public bool                    IsSection   { get; set; }
    }
}
