using Microsoft.Extensions.DependencyInjection;
using Migurdex.Cli.Services;
using Migurdex.Cli.Tui;
using Migurdex.Cli.Tui.Views;
using Spectre.Console;

namespace Migurdex.Cli;

public static class Program
{
    public static async Task Main(string[] args)
    {
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
        AnsiConsole.MarkupLine("[grey]~~[/] [dim]Migurdex başlatılıyor...[/] [grey]~~[/]");
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

        navigator.Start(mainMenu);

        AnsiConsole.Clear();
        RestoreCursor();
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
}
