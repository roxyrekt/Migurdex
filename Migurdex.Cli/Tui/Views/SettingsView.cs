using Migurdex.Cli.Configuration;
using Migurdex.Cli.Services;
using Migurdex.Shared.Enums;
using Spectre.Console;

namespace Migurdex.Cli.Tui.Views;

public class SettingsView : BaseView
{
    private readonly IApiClientService     _apiClient;
    private readonly IConfigurationService _configService;
    private          string?               _lastProviderName;

    public SettingsView(IConfigurationService configService, IApiClientService apiClient)
    {
        _configService = configService;
        _apiClient     = apiClient;
    }

    public override void Render(ITuiNavigator navigator)
    {
        var settingsRunning = true;
        var cursorIndex     = 0;

        var items = new List<SettingItem>
        {
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
                Id          = "Rpc",
                Label       = "Discord RPC",
                ValueGetter = c => c.EnableDiscordRpc ? "Açık" : "Kapalı"
            },
            new()
            {
                Id          = "Incognito",
                Label       = "Gizli Mod",
                ValueGetter = c => c.EnableIncognitoMode ? "Açık" : "Kapalı"
            },
            new()
            {
                Id          = "Api",
                Label       = "API Adresi",
                ValueGetter = c => c.ApiBaseUrl
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

        while (settingsRunning)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[grey]~~[/] [yellow]Ayarlar[/] [grey]~~[/]");
            AnsiConsole.WriteLine();

            var config = _configService.Config;

            var table = new Table().NoBorder().HideHeaders();
            table.AddColumn("Label", c => c.Width(25));
            table.AddColumn("Value");

            for (var i = 0; i < items.Count; i++)
            {
                var item       = items[i];
                var isSelected = i == cursorIndex;

                var labelPrefix = isSelected ? "> " : "  ";
                var labelStyle  = isSelected ? "bold white" : "grey";
                var valueStyle  = isSelected ? "bold cyan" : "cyan";

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
                    var val = item.ValueGetter(config);
                    table.AddRow($"[{labelStyle}]{labelPrefix}{item.Label}[/]", $"[{valueStyle}][[{val}]][/]");
                }
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Yön tuşları ile gezinin, Enter ile değiştirin.[/]");

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    cursorIndex = (cursorIndex - 1 + items.Count) % items.Count;
                    break;
                case ConsoleKey.DownArrow:
                    cursorIndex = (cursorIndex + 1) % items.Count;
                    break;
                case ConsoleKey.Enter:
                    var selected = items[cursorIndex];
                    if (HandleSelection(selected, config, navigator, ref settingsRunning))
                    {
                    }

                    break;
                case ConsoleKey.Escape:
                    _configService.Reload();
                    settingsRunning = false;
                    navigator.Pop();
                    break;
            }
        }
    }

    private bool HandleSelection(SettingItem item, CliConfig config, ITuiNavigator navigator, ref bool running)
    {
        switch (item.Id)
        {
            case "AutoPlay":
                config.AutoSelectBestSource = !config.AutoSelectBestSource;
                break;
            case "Timeout":
                config.AutoSelectTimeoutSeconds =
                    AnsiConsole.Ask("Bekleme süresi (sn):", config.AutoSelectTimeoutSeconds);

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
            case "Incognito":
                config.EnableIncognitoMode = !config.EnableIncognitoMode;
                break;
            case "Api":
                var apiUrl = (AnsiConsole.Ask("API adresi:", config.ApiBaseUrl ?? string.Empty) ?? string.Empty).Trim()
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
            case "Providers":
                ConfigureProviders(config);
                break;
            case "Sorting":
                ConfigureSortingPriorities(config);
                break;
            case "Save":
                _configService.Save();
                Toast.Show("[green]Kaydedildi.[/]");
                running = false;
                navigator.Pop();
                return true;
            case "Cancel":
                _configService.Reload();
                running = false;
                navigator.Pop();
                return true;
        }

        return false;
    }

    private void ConfigureProviders(CliConfig config)
    {
        var active = true;

        ApiResult<IReadOnlyList<ProviderInfo>>? providersResult = null;
        AnsiConsole.Status()
                   .Spinner(Spinner.Known.Dots)
                   .Start("Sağlayıcılar yükleniyor...",
                          _ =>
                          {
                              providersResult = _apiClient.GetProvidersAsync().GetAwaiter().GetResult();
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

        while (active)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[grey]~~[/] [yellow]Sağlayıcı Yönetimi[/] [grey]~~[/]");
            AnsiConsole.WriteLine();

            if (providers.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Sağlayıcı listesi alınamadı.[/]");
                if (providersResult.Error is not null)
                {
                    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(providersResult.Error)}[/]");
                }

                Console.ReadKey(true);
                return;
            }

            var table = new Table().NoBorder().HideHeaders();
            table.AddColumn("Name", c => c.Width(25));
            table.AddColumn("Status");

            for (var i = 0; i < providers.Count; i++)
            {
                var p          = providers[i];
                var isSelected = i == cursorIndex;
                var isDisabled = config.DisabledProviders.Contains(p.Name, StringComparer.OrdinalIgnoreCase);

                var labelPrefix = isSelected ? "> " : "  ";
                var labelStyle  = isSelected ? "bold white" : "grey";
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
            var backLabel = cursorIndex == providers.Count ? "> Geri" : "  Geri";
            var backStyle = cursorIndex == providers.Count ? "bold yellow" : "yellow";
            table.AddRow($"[{backStyle}]{backLabel}[/]", "");

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Yön tuşları ile gezinin, Enter ile değiştirin.[/]");

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
                        if (config.DisabledProviders.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
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
    }

    private void ConfigureSortingPriorities(CliConfig config)
    {
        var active = true;
        while (active)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[grey]~~[/] [yellow]Sıralama Öncelikleri[/] [grey]~~[/]");
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
                        var mergedHosters = GetMergedHosters();
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
                                      GetMergedHosters(),
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

    private List<string> GetMergedHosters()
    {
        var config = _configService.Config;

        ApiResult<IReadOnlyList<string>>? extractorsResult = null;
        AnsiConsole.Status()
                   .Spinner(Spinner.Known.Dots)
                   .Start("Sunucu listesi yükleniyor...",
                          _ =>
                          {
                              extractorsResult =
                                  _apiClient.GetExtractorsAsync().GetAwaiter().GetResult();
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

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[grey]~~[/] [yellow]{Markup.Escape(title)}[/] [grey]~~[/]");
            AnsiConsole.MarkupLine(
                "[green]Otomatik:[/] [grey]kural yok ·[/] [red]Asla:[/] [grey]otomatik seçilmez ·[/] [gold1]Sadece:[/] [grey]yalnız işaretliler otomatik seçilir[/]");
            AnsiConsole.WriteLine();

            var table = new Table().NoBorder().HideHeaders();
            table.AddColumn("Name", c => c.Width(25));
            table.AddColumn("Status");

            for (var i = 0; i < allItems.Count; i++)
            {
                var item       = allItems[i];
                var isSelected = i == cursorIndex;
                var state      = AutoRuleState(item, neverList, onlyList);

                var labelPrefix = isSelected ? "> " : "  ";
                var labelStyle  = isSelected ? "bold white" : "grey";
                var stateText = state switch
                {
                    "Asla"   => "Asla",
                    "Sadece" => "Sadece",
                    _        => "Otomatik"
                };
                var stateStyle = state switch
                {
                    "Asla"   => isSelected ? "bold red" : "red",
                    "Sadece" => isSelected ? "bold gold1" : "gold1",
                    _        => isSelected ? "bold green" : "green"
                };

                table.AddRow($"[{labelStyle}]{labelPrefix}{Markup.Escape(item)}[/]",
                             $"[{stateStyle}][[{stateText}]][/]");
            }

            table.AddRow("", "");
            var backLabel = cursorIndex == allItems.Count ? "> Geri" : "  Geri";
            var backStyle = cursorIndex == allItems.Count ? "bold yellow" : "yellow";
            table.AddRow($"[{backStyle}]{backLabel}[/]", "");

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Enter ile durumu değiştirin (Otomatik → Asla → Sadece).[/]");

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
                        return;
                    }

                    CycleAutoRule(allItems[cursorIndex], neverList, onlyList);
                    break;
                case ConsoleKey.Escape:
                    return;
            }
        }
    }

    private class SettingItem
    {
        public string                  Id          { get; set; } = string.Empty;
        public string                  Label       { get; set; } = string.Empty;
        public Func<CliConfig, string> ValueGetter { get; set; } = _ => string.Empty;
        public bool                    IsAction    { get; set; }
    }
}
