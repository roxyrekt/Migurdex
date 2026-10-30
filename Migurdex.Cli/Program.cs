using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Migurdex.Cli.Dependencies;
using Migurdex.Cli.Services;
using Migurdex.Cli.Services.Downloads;
using Migurdex.Cli.Tui;
using Migurdex.Cli.Tui.Views;
using Migurdex.Core.Database;
using Migurdex.Core.Extensions;
using Migurdex.Core.Services;
using Migurdex.Core.Services.Turnstile;
using Migurdex.Shared.Models;
using Migurdex.Shared.Update;
using Spectre.Console;
using System.Text;

namespace Migurdex.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // ignored
        }

        try
        {
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // ignored
        }

        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        }

        CleanStaleBackup();

        if (args.Any(a => a.Equals("--version", StringComparison.OrdinalIgnoreCase)
                          || a.Equals("-v", StringComparison.OrdinalIgnoreCase))
            || (args.Length > 0 && args[0].Equals("version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"migurdex {AppInfo.GetDisplayVersion()}");
            return 0;
        }

        if (HelpCommand.IsTopLevelRequest(args))
        {
            // TTY yokken TUI'yi açmak Console.ReadKey ile çöktürür; yardım yalnızca yazma yapar.
            return HelpCommand.Run();
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

        if (args.Length > 0 && args[0].Equals("deps", StringComparison.OrdinalIgnoreCase))
        {
            var depsServices = new ServiceCollection();
            ConfigureServices(depsServices);
            using var depsProvider = depsServices.BuildServiceProvider();

            using var depsCts         = new CancellationTokenSource();
            var       cancelRequested = 0;

            ConsoleCancelEventHandler depsCancelHandler = (_, eventArgs) =>
            {
                if (Interlocked.Exchange(ref cancelRequested, 1) == 0)
                {
                    eventArgs.Cancel = true;
                    depsCts.Cancel();
                }
            };

            Console.CancelKeyPress += depsCancelHandler;
            try
            {
                return await DepsCommand.RunAsync(args[1..], depsProvider, depsCts.Token);
            }
            catch (OperationCanceledException) when (depsCts.IsCancellationRequested)
            {
                await Console.Error.WriteLineAsync("İptal edildi.");

                return 130;
            }
            finally
            {
                Console.CancelKeyPress -= depsCancelHandler;
            }
        }

        if (args.Length > 0 && NonInteractiveCommand.IsCommand(args[0]))
        {
            var nonInteractiveServices = new ServiceCollection();
            ConfigureServices(nonInteractiveServices);
            using var nonInteractiveProvider = nonInteractiveServices.BuildServiceProvider();
            return await NonInteractiveCommand.RunAsync(args, nonInteractiveProvider);
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreCursor();
        TuiApplicationCancellation.Reset();
        ITuiNavigator? activeNavigator   = null;
        var            cancelSignalCount = 0;
        ConsoleCancelEventHandler tuiCancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (TuiApplicationCancellation.IsModalActive && TuiApplicationCancellation.CancelModal())
            {
                RestoreCursor();
                return;
            }

            if (Interlocked.Increment(ref cancelSignalCount) == 1)
            {
                TuiApplicationCancellation.RequestCancellation();
                activeNavigator?.Exit();
                RestoreCursor();
            }
            else
            {
                eventArgs.Cancel = false;
            }
        };
        Console.CancelKeyPress += tuiCancelHandler;

        try
        {
            Console.Title = "Migurdex Terminal Client";

            var services = new ServiceCollection();
            ConfigureServices(services);

            using var serviceProvider = services.BuildServiceProvider();

            AnsiConsole.Clear();
            AnsiConsole.MarkupLine("[bold cyan]Migurdex[/] [grey]başlatılıyor...[/]");
            AnsiConsole.WriteLine();

            var apiService = serviceProvider.GetRequiredService<IApiClientService>();
            var tuiToken   = TuiApplicationCancellation.Token;

            var isOnline = await AnsiConsole.Status()
                                            .Spinner(Spinner.Known.Dots)
                                            .StartAsync("API kontrol ediliyor...",
                                                        async ctx =>
                                                        {
                                                            if (await apiService.IsApiOnlineAsync(tuiToken))
                                                            {
                                                                tuiToken.ThrowIfCancellationRequested();
                                                                return true;
                                                            }

                                                            ctx.Status(
                                                                "API başlatılıyor...");
                                                            var started =
                                                                await apiService.TryStartApiDaemonAsync(tuiToken);
                                                            tuiToken.ThrowIfCancellationRequested();
                                                            return started;
                                                        });

            if (!isOnline)
            {
                AnsiConsole.MarkupLine("[yellow][[!]] API bağlantısı kurulamadı.[/]");
                AnsiConsole.MarkupLine(
                    "[grey]İstemciyi açıp ayarlardan API adresini değiştirebilirsiniz.[/]");
                AnsiConsole.MarkupLine("[grey]Devam etmek için bir tuşa basın...[/]");
                if (!Console.IsInputRedirected)
                {
                    try { Console.ReadKey(true); }
                    catch
                    {
                        // ignored
                    }
                }
            }

            var navigator = serviceProvider.GetRequiredService<ITuiNavigator>();
            activeNavigator = navigator;
            var mainMenu = serviceProvider.GetRequiredService<MainMenuView>();

            _ = Task.Run(() => serviceProvider.GetRequiredService<WatchSyncService>().FlushQueueAsync());

            var noUpdateCheck = args.Any(a => a.Equals("--no-update-check", StringComparison.OrdinalIgnoreCase));
            var noDepUpdateCheck = noUpdateCheck
                                   || args.Any(a => a.Equals("--no-dep-check", StringComparison.OrdinalIgnoreCase));

            await MaybePromptForDependenciesAsync(serviceProvider);
            tuiToken.ThrowIfCancellationRequested();
            await MaybePromptForDependencyUpdatesAsync(serviceProvider, noDepUpdateCheck);
            tuiToken.ThrowIfCancellationRequested();
            await MaybePromptForUpdateAsync(serviceProvider, noUpdateCheck);
            tuiToken.ThrowIfCancellationRequested();

            await navigator.StartAsync(mainMenu);

            AnsiConsole.Clear();
            return 0;
        }
        catch (OperationCanceledException) when (TuiApplicationCancellation.Token.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= tuiCancelHandler;
            RestoreCursor();
            TuiApplicationCancellation.Reset();
        }
    }

    private static async Task MaybePromptForDependenciesAsync(IServiceProvider services)
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        try
        {
            var resolver      = services.GetService<DependencyResolver>();
            var configService = services.GetRequiredService<IConfigurationService>();

            if (resolver is null || !resolver.CanAutoInstall)
            {
                return;
            }

            // Kullanıcı bir kez "Hayır" dediyse açılışta tekrar sormayalım.
            if (configService.Config.DependencyPromptDismissed)
            {
                return;
            }

            var missing = DependencySetupRunner.FindMissing(resolver);
            if (missing.Count == 0)
            {
                return;
            }

            var names = string.Join(", ", missing.Select(DependencyCatalog.DisplayName));
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow][[!]][/] Eksik araçlar: [white]{Markup.Escape(names)}[/]");
            AnsiConsole.MarkupLine("[grey]Bunlar olmadan oynatma veya HLS indirme çalışmayabilir.[/]");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                    .AddChoices("Şimdi kur", "Daha sonra", "Bir daha sorma"));

            switch (choice)
            {
                case "Şimdi kur":
                    IReadOnlyList<DependencyInstallResult> results = [];
                    await AnsiConsole.Status()
                                     .Spinner(Spinner.Known.Dots)
                                     .StartAsync("Araçlar kuruluyor...",
                                                 async ctx =>
                                                 {
                                                     var progress =
                                                         new Progress<string>(line => ctx.Status(Markup.Escape(line)));
                                                     results = await DependencySetupRunner.InstallAsync(resolver,
                                                                   missing,
                                                                   progress,
                                                                   TuiApplicationCancellation
                                                                       .Token);
                                                 });

                    foreach (var result in results)
                    {
                        var name = DependencyCatalog.DisplayName(result.Kind);
                        AnsiConsole.MarkupLine(result.Success
                                                   ? $"[green]✓[/] {Markup.Escape(name)} kuruldu."
                                                   : $"[red]✗[/] {Markup.Escape(name)}: {Markup.Escape(result.Message)}");
                    }

                    break;
                case "Bir daha sorma":
                    configService.Config.DependencyPromptDismissed = true;
                    try { configService.Save(); }
                    catch
                    {
                        // ignored
                    }

                    break;
            }
        }
        catch
        {
            // Araç istemi asla uygulamayı engellememeli.
        }
    }

    private static async Task MaybePromptForDependencyUpdatesAsync(IServiceProvider services, bool skip)
    {
        if (skip || Console.IsInputRedirected)
        {
            return;
        }

        try
        {
            var checker       = services.GetService<DependencyUpdateChecker>();
            var configService = services.GetRequiredService<IConfigurationService>();

            if (checker is null || !configService.Config.DependencyUpdateCheckEnabled)
            {
                return;
            }

            var config = configService.Config;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                TuiApplicationCancellation.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            IReadOnlyList<DependencyUpdate> updates;
            try
            {
                updates = await checker.CheckAsync(false,
                                                   progress: null,
                                                   cancellationToken: cts.Token);
            }
            catch (OperationCanceledException) when (!TuiApplicationCancellation.Token.IsCancellationRequested)
            {
                return;
            }

            if (updates.Count == 0)
            {
                return;
            }

            var pending = updates.Where(u => !config.SkippedDependencyVersions.TryGetValue(
                                                 DependencyCatalog.CommandName(u.Kind),
                                                 out var skipped)
                                             || !DependencyUpdateChecker.IsSameVersion(skipped, u.LatestVersion))
                                 .ToList();

            if (pending.Count == 0)
            {
                return;
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow][[!]][/] Güncellenebilir araçlar:");
            foreach (var update in pending)
            {
                AnsiConsole.MarkupLine($"   [white]{Markup.Escape(update.DisplayName)}[/]"
                                       + $" [grey]{Markup.Escape(update.InstalledVersion)} →[/]"
                                       + $" [green]{Markup.Escape(update.LatestVersion)}[/]");
            }

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                    .AddChoices("Şimdi güncelle", "Daha sonra", "Bu sürümü atla"));

            switch (choice)
            {
                case "Şimdi güncelle":
                    IReadOnlyList<DependencyInstallResult> results = [];
                    await AnsiConsole.Status()
                                     .Spinner(Spinner.Known.Dots)
                                     .StartAsync("Araçlar güncelleniyor...",
                                                 async ctx =>
                                                 {
                                                     var progress =
                                                         new Progress<string>(line => ctx.Status(Markup.Escape(line)));
                                                     results = await DependencySetupRunner.InstallAsync(
                                                                   checker.Resolver,
                                                                   pending.Select(u => u.Kind),
                                                                   progress,
                                                                   TuiApplicationCancellation.Token);
                                                 });

                    foreach (var result in results)
                    {
                        var name = DependencyCatalog.DisplayName(result.Kind);
                        AnsiConsole.MarkupLine(result.Success
                                                   ? $"[green]✓[/] {Markup.Escape(name)} güncellendi."
                                                   : $"[red]✗[/] {Markup.Escape(name)}: {Markup.Escape(result.Message)}");
                    }

                    break;
                case "Bu sürümü atla":
                    foreach (var update in pending)
                    {
                        config.SkippedDependencyVersions[DependencyCatalog.CommandName(update.Kind)] =
                            update.LatestVersion;
                    }

                    try { configService.Save(); }
                    catch
                    {
                        // ignored
                    }

                    break;
            }
        }
        catch
        {
            // ignored
        }
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
                    .Title($"[cyan]Yeni sürüm mevcut:[/] v{result.CurrentVersion} → v{result.LatestVersion}"
                           + (result.IsPrerelease ? " [grey](pre-release)[/]" : ""))
                    .HighlightStyle(new Style(Color.Cyan1, decoration: Decoration.Bold))
                    .AddChoices("Evet, güncelle", "Hayır", "Bu sürümü atla"));

            if (choice == "Evet, güncelle")
            {
                var code = await UpdateCommand.RunAsync(["--yes"], services);
                if (code == 0)
                {
                    Environment.Exit(0);
                }

                AnsiConsole.MarkupLine("[grey]Mevcut sürümle devam etmek için bir tuşa basın...[/]");
                if (!Console.IsInputRedirected)
                {
                    Console.ReadKey(true);
                }
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
        var stderrProvider = new StderrLoggerProvider();
        services.AddLogging(builder => builder.AddProvider(stderrProvider));
        services.AddSingleton(stderrProvider);
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddDependencyServices();
        services.AddSingleton<MigurdexDatabase>(sp =>
                                                    new MigurdexDatabase(
                                                        sp.GetRequiredService<IConfigurationService>()
                                                          .ConfigDirectory));
        services.AddTurnstileServices(TurnstilePaths.DataRoot(),
                                      TurnstilePaths.BrowserCacheRoot(),
                                      sp => sp.GetRequiredService<IConfigurationService>()
                                              .Config.EnableIncognitoMode);
        services.AddSingleton<IHistoryService>(sp =>
                                                   new HistoryService(sp.GetRequiredService<IConfigurationService>(),
                                                                      sp.GetRequiredService<MigurdexDatabase>()));
        services.AddDownloadServices(provider =>
        {
            var config     = provider.GetRequiredService<IConfigurationService>().Config;
            var dependency = provider.GetService<DependencyResolver>();
            return new HlsDownloadOptions
            {
                Executable  = dependency?.Resolve(DependencyKind.YtDlp) ?? config.YtDlpPath,
                FfmpegPath  = dependency?.Resolve(DependencyKind.Ffmpeg),
                MaxAttempts = 3,
                RetryDelay  = TimeSpan.FromSeconds(1)
            };
        });
        services.AddSingleton<IDiscordRpcService, DiscordRpcService>();
        services.AddSingleton<IMpvPlayerService, MpvPlayerService>();
        services.AddSingleton<PlaybackOrchestrator>();

        services.AddSingleton<HttpClient>();
        services.AddSingleton<IApiClientService, ApiClientService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        services.AddSingleton<OAuthTokenStore>(sp =>
                                                   new OAuthTokenStore(sp.GetRequiredService<MigurdexDatabase>()));
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
            var db       = sp.GetRequiredService<MigurdexDatabase>();
            var aniOauth = sp.GetRequiredService<AniListOAuthClient>();
            var malOauth = sp.GetRequiredService<MalOAuthClient>();
            var aniList  = new AniListListClient(new CliBridge(), aniOauth, store);
            var mal      = new MalListClient(new CliBridge(), malOauth, store);
            return new WatchSyncService(aniList,
                                        store,
                                        (provider, animeId, season, episode, title, ct) =>
                                            MapEpisodeAsync(api, provider, animeId, season, episode, title, ct),
                                        db,
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
