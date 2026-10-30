using Migurdex.Cli.Dependencies;
using Spectre.Console;
using System.Text.Json;

namespace Migurdex.Cli.Services;

public static class DepsCommand
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(
        string[]          args,
        IServiceProvider  services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var resolver = services.GetService(typeof(DependencyResolver)) as DependencyResolver;
        if (resolver is null)
        {
            await Console.Error.WriteLineAsync("Hata: bağımlılık çözücü kayıtlı değil.");

            return 1;
        }

        var    showHelp    = false;
        var    json        = false;
        var    install     = false;
        var    force       = false;
        var    requested   = new List<DependencyKind>();
        string? parseError = null;

        for (var i = 0; i < args.Length && parseError is null; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "install":
                    install = true;
                    break;
                case "--force":
                case "-f":
                    force = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help":
                case "-h":
                case "help":
                    showHelp = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        parseError = $"Bilinmeyen bayrak: {arg}";
                    }
                    else if (DependencyCatalog.TryParse(arg, out var kind))
                    {
                        requested.Add(kind);
                    }
                    else
                    {
                        parseError = $"Bilinmeyen araç: {arg} (mpv, yt-dlp, ffmpeg)";
                    }

                    break;
            }
        }

        if (parseError is not null)
        {
            await Console.Error.WriteLineAsync($"Hata: {parseError}");

            return 2;
        }

        if (showHelp)
        {
            PrintHelp();

            return 0;
        }

        return install
                   ? await InstallAsync(resolver, requested, force, json, cancellationToken)
                   : await StatusAsync(resolver, json, cancellationToken);
    }

    private static async Task<int> StatusAsync(
        DependencyResolver resolver,
        bool               json,
        CancellationToken  cancellationToken)
    {
        var statuses = new List<DependencyStatus>();
        foreach (var kind in DependencyCatalog.All)
        {
            statuses.Add(await resolver.CheckAsync(kind,
                                                   includeVersion: true,
                                                   cancellationToken));
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(statuses, _jsonOpts));

            return 0;
        }

        AnsiConsole.MarkupLine($"[cyan]Platform:[/] {Markup.Escape(resolver.PlatformName)}"
                               + $"  [grey](otomatik kurulum: {(resolver.CanAutoInstall ? "var" : "yok")})[/]");
        AnsiConsole.WriteLine();

        var table = new Table().NoBorder().HideHeaders();
        table.AddColumn("Araç", c => c.Width(10));
        table.AddColumn("Durum", c => c.Width(12));
        table.AddColumn("Yol / Sürüm");

        foreach (var status in statuses)
        {
            var state = status.IsInstalled ? "[green]kurulu[/]" : "[red]eksik[/]";
            var detail = status.IsInstalled
                             ? Markup.Escape(status.ResolvedPath!)
                               + (string.IsNullOrWhiteSpace(status.Version)
                                      ? string.Empty
                                      : $" [grey]({Markup.Escape(status.Version)})[/]")
                             : "[grey]—[/]";

            table.AddRow($"[white]{Markup.Escape(status.DisplayName)}[/]", state, detail);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var missing = statuses.Where(s => !s.IsInstalled).Select(s => s.Kind).ToList();
        if (missing.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Tüm araçlar kurulu.[/]");

            return 0;
        }

        if (resolver.CanAutoInstall)
        {
            AnsiConsole.MarkupLine("[yellow]Kurmak için:[/] migurdex deps install");
        }
        else
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(resolver.Installer.GetManualInstructions(missing[0]))}[/]");
        }

        return 0;
    }

    private static async Task<int> InstallAsync(
        DependencyResolver           resolver,
        IReadOnlyList<DependencyKind> requested,
        bool                          force,
        bool                          json,
        CancellationToken             cancellationToken)
    {
        var targets = requested.Count > 0 ? requested.ToList() : DependencyCatalog.All.ToList();

        if (!resolver.CanAutoInstall)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    platform    = resolver.PlatformName,
                    autoInstall = false,
                    message     = "Bu platformda otomatik kurulum yok. Araçları elle kurun."
                }, _jsonOpts));

                return 1;
            }

            AnsiConsole.MarkupLine(
                $"[yellow]Bu platformda ({Markup.Escape(resolver.PlatformName)}) otomatik kurulum yok.[/]");
            foreach (var kind in targets)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(resolver.Installer.GetManualInstructions(kind))}[/]");
            }

            return 1;
        }

        if (!force)
        {
            var alreadyInstalled = targets.Where(kind => resolver.Resolve(kind) is not null).ToList();
            targets = targets.Except(alreadyInstalled).ToList();

            if (targets.Count == 0)
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(Array.Empty<DependencyInstallResult>(), _jsonOpts));
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]İstenen araçların tümü zaten kurulu.[/]");
                }

                return 0;
            }
        }

        var results = new List<DependencyInstallResult>();

        if (json)
        {
            results.AddRange(await DependencySetupRunner.InstallAsync(resolver,
                                                                      targets,
                                                                      progress: null,
                                                                      cancellationToken));
            Console.WriteLine(JsonSerializer.Serialize(results, _jsonOpts));
        }
        else
        {
            await AnsiConsole.Status()
                             .Spinner(Spinner.Known.Dots)
                             .StartAsync("Araçlar kuruluyor...",
                                         async ctx =>
                                         {
                                             var progress =
                                                 new Progress<string>(line => ctx.Status(Markup.Escape(line)));
                                             results.AddRange(await DependencySetupRunner.InstallAsync(resolver,
                                                                                                       targets,
                                                                                                       progress,
                                                                                                       cancellationToken));
                                         });

            AnsiConsole.WriteLine();
            foreach (var result in results)
            {
                var name = DependencyCatalog.DisplayName(result.Kind);
                if (result.Success)
                {
                    AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(name)}: {Markup.Escape(result.Message)}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(name)}: {Markup.Escape(result.Message)}");
                }
            }
        }

        return results.All(r => r.Success) ? 0 : 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Kullanım:");
        Console.WriteLine("  migurdex deps [--json]                 Araç durumunu gösterir.");
        Console.WriteLine("  migurdex deps install [araç...] [--force] [--json]");
        Console.WriteLine("                                         Eksik araçları kurar (Windows).");
        Console.WriteLine();
        Console.WriteLine("Araçlar: mpv, yt-dlp, ffmpeg");
    }
}
