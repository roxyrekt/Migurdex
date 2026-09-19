using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Services;
using Migurdex.Cli.Tui;
using Migurdex.Cli.Tui.Views;
using Migurdex.Core.Services;
using Migurdex.Shared.Models;
using Spectre.Console;

namespace Migurdex.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CleanStaleBackup();

        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("-v", StringComparison.OrdinalIgnoreCase))
            || (args.Length > 0 && args[0].Equals("version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"migurdex v{Migurdex.Shared.Update.AppInfo.GetVersion()}");
            return 0;
        }

        if (args.Length > 0 && args[0].Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            var updateServices = new ServiceCollection();
            ConfigureServices(updateServices);
            using var updateProvider = updateServices.BuildServiceProvider();
            return await UpdateCommand.RunAsync(args[1..], updateProvider);
        }

        if (args.Length > 0 && args[0].Equals("auth", StringComparison.OrdinalIgnoreCase))
        {
            var authServices = new ServiceCollection();
            ConfigureServices(authServices);
            using var authProvider = authServices.BuildServiceProvider();
            return await AuthCommand.RunAsync(args[1..],
                                              authProvider.GetRequiredService<OAuthTokenStore>(),
                                              authProvider.GetRequiredService<AniListOAuthClient>(),
                                              authProvider.GetRequiredService<MalOAuthClient>());
        }

        if (args.Length > 0 && NonInteractiveCommand.IsCommand(args[0]))
        {
            var nonInteractiveServices = new ServiceCollection();
            ConfigureServices(nonInteractiveServices);
            using var nonInteractiveProvider = nonInteractiveServices.BuildServiceProvider();
            return await NonInteractiveCommand.RunAsync(args, nonInteractiveProvider);
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreCursor();
        Console.CancelKeyPress += (s, e) =>
        {
            RestoreCursor();
            Environment.Exit(0);
        };

        Console.Title = "Migurdex Terminal Client";

        var services = new ServiceCollection();
        ConfigureServices(services);

        var serviceProvider = services.BuildServiceProvider();

        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[grey]~~[/] [bold cyan]Migurdex başlatılıyor...[/] [grey]~~[/]");
        AnsiConsole.WriteLine();

        var apiService = serviceProvider.GetRequiredService<IApiClientService>();

        var isOnline = await AnsiConsole.Status()
                                        .Spinner(Spinner.Known.Dots)
                                        .StartAsync("API kontrol ediliyor...",
                                                    async ctx =>
                                                    {
                                                        if (await apiService.IsApiOnlineAsync())
                                                        {
                                                            return true;
                                                        }

                                                        ctx.Status(
                                                            "API başlatılıyor...");
                                                        return await apiService.TryStartApiDaemonAsync();
                                                    });

        if (!isOnline)
        {
            AnsiConsole.MarkupLine("[yellow][[!]] API bağlantısı kurulamadı.[/]");
            AnsiConsole.MarkupLine(
                "[grey]İstemciyi açıp ayarlardan API adresini değiştirebilirsiniz.[/]");
            AnsiConsole.MarkupLine("[grey]Devam etmek için bir tuşa basın...[/]");
            Console.ReadKey(true);
        }

        var navigator = serviceProvider.GetRequiredService<ITuiNavigator>();
        var mainMenu  = serviceProvider.GetRequiredService<MainMenuView>();

        _ = Task.Run(() => serviceProvider.GetRequiredService<WatchSyncService>().FlushQueueAsync());

        var noUpdateCheck = args.Any(a => a.Equals("--no-update-check", StringComparison.OrdinalIgnoreCase));
        await MaybePromptForUpdateAsync(serviceProvider, noUpdateCheck);

        navigator.Start(mainMenu);

        AnsiConsole.Clear();
        RestoreCursor();
        return 0;
    }

    private static async Task MaybePromptForUpdateAsync(IServiceProvider services, bool noUpdateCheck)
    {
        if (noUpdateCheck || Console.IsInputRedirected)
        {
            return;
        }

        try
        {
            var updateService = services.GetRequiredService<IUpdateService>();
            var configService = services.GetRequiredService<IConfigurationService>();

            var result = await updateService.CheckForUpdatesAsync();
            if (result is null || !result.IsUpdateAvailable)
            {
                return;
            }

            if (result.LatestVersion.Equals(configService.Config.SkippedVersion,
                                            StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[yellow]Yeni sürüm mevcut:[/] v{result.CurrentVersion} → v{result.LatestVersion}"
                           + (result.IsPrerelease ? " [grey](pre-release)[/]" : ""))
                    .AddChoices("Evet, güncelle", "Hayır", "Bu sürümü atla"));

            if (choice == "Evet, güncelle")
            {
                var code = await UpdateCommand.RunAsync(["--yes"], services);
                if (code == 0)
                {
                    Environment.Exit(0);
                }

                AnsiConsole.MarkupLine("[grey]Mevcut sürümle devam etmek için bir tuşa basın...[/]");
                Console.ReadKey(true);
            }
            else if (choice == "Bu sürümü atla")
            {
                configService.Config.SkippedVersion = result.LatestVersion;
                try { configService.Save(); }
                catch
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void CleanStaleBackup()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return;
            }

            var backup = exe + ".old";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static void RestoreCursor()
    {
        Console.Write("\x1b[?25h");
        AnsiConsole.Cursor.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton<IDiscordRpcService, DiscordRpcService>();
        services.AddSingleton<IMpvPlayerService, MpvPlayerService>();

        services.AddSingleton<HttpClient>();
        services.AddSingleton<IApiClientService, ApiClientService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        services.AddSingleton<OAuthTokenStore>();
        services.AddSingleton<AniListOAuthClient>(_ => new AniListOAuthClient(new CliBridge(),
                                                                              AniListAppCredentials.ClientId,
                                                                              AniListAppCredentials
                                                                                  .ClientSecret));
        services.AddSingleton<MalOAuthClient>(_ => new MalOAuthClient(new CliBridge(),
                                                                      MalAppCredentials.ClientId));
        services.AddSingleton<WatchSyncService>(sp =>
        {
            var api      = sp.GetRequiredService<IApiClientService>();
            var store    = sp.GetRequiredService<OAuthTokenStore>();
            var aniOauth = sp.GetRequiredService<AniListOAuthClient>();
            var malOauth = sp.GetRequiredService<MalOAuthClient>();
            var aniList  = new AniListListClient(new CliBridge(), aniOauth, store);
            var mal      = new MalListClient(new CliBridge(), malOauth, store);
            return new WatchSyncService(aniList,
                                        store,
                                        (provider, animeId, season, episode, title, ct) =>
                                            MapEpisodeAsync(api, provider, animeId, season, episode, title, ct),
                                        malClient: mal);
        });

        services.AddSingleton<ITuiNavigator, TuiNavigator>();

        services.AddTransient<MainMenuView>();
        services.AddTransient<SearchView>();
        services.AddTransient<SearchResultsView>();
        services.AddTransient<AnimeDetailsView>();
        services.AddTransient<EpisodeSourcesView>();
        services.AddTransient<PlaybackMenuView>();
        services.AddTransient<FavoritesView>();
        services.AddTransient<WatchHistoryView>();
        services.AddTransient<SettingsView>();
    }

    private static async Task<EpisodeMappingResult> MapEpisodeAsync(IApiClientService api,
        string                                                                        provider,
        string                                                                        animeId,
        int                                                                           season,
        double                                                                        episode,
        string?                                                                       title,
        CancellationToken                                                             cancellationToken)
    {
        try
        {
            var result = await api.MapTrackerEpisodeAsync(provider, animeId, season, episode, cancellationToken);
            if (result.IsSuccess && result.Data is not null)
            {
                return new EpisodeMappingResult
                {
                    Mapping = result.Data
                };
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return new EpisodeMappingResult();
            }

            var resolved = await api.ResolveTrackerIdAsync(provider,
                                                           animeId,
                                                           [title],
                                                           cancellationToken: cancellationToken);
            if (resolved.IsSuccess && resolved.Data is not null && resolved.Data.Ambiguous)
            {
                return new EpisodeMappingResult
                {
                    Ambiguous  = true,
                    Candidates = resolved.Data.Candidates
                };
            }

            return new EpisodeMappingResult();
        }
        catch
        {
            return new EpisodeMappingResult();
        }
    }
}
