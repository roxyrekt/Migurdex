using Migurdex.Cli.Services;
using Spectre.Console;

namespace Migurdex.Cli.Tui;

public static class FuzzyPrompt
{
    private static string FormatQueryWithCursor(string query, int cursorIdx)
    {
        if (string.IsNullOrEmpty(query))
        {
            return "[black on white] [/]";
        }

        if (cursorIdx >= query.Length)
        {
            return $"{Markup.Escape(query)}[black on white] [/]";
        }

        var left       = query[..cursorIdx];
        var cursorChar = query[cursorIdx];
        var right      = query[(cursorIdx + 1)..];

        return $"{Markup.Escape(left)}[black on white]{Markup.Escape(cursorChar.ToString())}[/]{Markup.Escape(right)}";
    }

    private static int ResolveInitialCursor(List<FuzzyChoice> choicesList, string? initialSelection)
    {
        if (string.IsNullOrEmpty(initialSelection))
        {
            return 0;
        }

        var idx = choicesList.FindIndex(c => c.Searchable.Equals(initialSelection, StringComparison.Ordinal));
        return idx >= 0 ? idx : 0;
    }

    public static FuzzyChoice? Show(
        string                   title,
        IEnumerable<FuzzyChoice> choices,
        int                      pageSize         = 15,
        string?                  initialSelection = null)
    {
        var choicesList     = choices.ToList();
        var query           = string.Empty;
        var cursorIndex     = ResolveInitialCursor(choicesList, initialSelection);
        var textCursorIndex = 0;

        FuzzyChoice? result    = null;
        var          isRunning = true;

        while (isRunning)
        {
            var filtered = FuzzyMatcher.Rank(choicesList, query);

            if (cursorIndex >= filtered.Count)
            {
                cursorIndex = Math.Max(0, filtered.Count - 1);
            }

            AnsiConsole.Clear();

            var grid = new Grid();
            grid.AddColumn();

            grid.AddRow(new Markup($"[grey]~~[/] [bold cyan]{title.TrimEnd(':')}[/] [grey]~~[/]"));
            grid.AddRow(new Text(string.Empty));
            grid.AddRow(new Markup($"[bold cyan]Filtre:[/] {FormatQueryWithCursor(query, textCursorIndex)}"));
            grid.AddRow(new Text(string.Empty));

            var startIdx = Math.Max(0, cursorIndex - (pageSize / 2));
            var endIdx   = Math.Min(filtered.Count, startIdx + pageSize);
            if (endIdx - startIdx < pageSize && startIdx > 0)
            {
                startIdx = Math.Max(0, endIdx - pageSize);
            }

            for (var i = startIdx; i < endIdx; i++)
            {
                var choice = filtered[i];
                if (i == cursorIndex)
                {
                    grid.AddRow(new Markup($"> {choice.DisplayActive}"));
                }
                else
                {
                    grid.AddRow(new Markup($"  {choice.Display}"));
                }
            }

            if (filtered.Count == 0)
            {
                grid.AddRow(new Markup("  [red]Sonuç yok.[/]"));
            }

            grid.AddRow(new Text(string.Empty));
            grid.AddRow(
                new Markup(
                    "[grey]Filtrele: [/] [bold white]Enter[/] [grey]Seç[/]  [bold white]Esc[/] [grey]Geri[/]"));

            AnsiConsole.Write(grid);

            var keyInfo = Console.ReadKey(true);

            switch (keyInfo.Key)
            {
                case ConsoleKey.UpArrow:
                    cursorIndex = filtered.Count > 0
                                      ? (cursorIndex - 1 + filtered.Count) % filtered.Count
                                      : 0;
                    break;
                case ConsoleKey.DownArrow:
                    cursorIndex = filtered.Count > 0
                                      ? (cursorIndex + 1) % filtered.Count
                                      : 0;
                    break;
                case ConsoleKey.LeftArrow:
                    textCursorIndex = Math.Max(0, textCursorIndex - 1);
                    break;
                case ConsoleKey.RightArrow:
                    textCursorIndex = Math.Min(query.Length, textCursorIndex + 1);
                    break;
                case ConsoleKey.Enter:
                    if (filtered.Count > 0)
                    {
                        result    = filtered[cursorIndex];
                        isRunning = false;
                    }

                    break;
                case ConsoleKey.Escape:
                    result    = null;
                    isRunning = false;
                    break;
                case ConsoleKey.Backspace:
                    if (textCursorIndex > 0)
                    {
                        query = query[..(textCursorIndex - 1)] + query[textCursorIndex..];
                        textCursorIndex--;
                        cursorIndex = 0;
                    }

                    break;
                case ConsoleKey.Delete:
                    if (textCursorIndex < query.Length)
                    {
                        query       = query[..textCursorIndex] + query[(textCursorIndex + 1)..];
                        cursorIndex = 0;
                    }

                    break;
                default:
                    if (keyInfo.KeyChar != '\0' && !char.IsControl(keyInfo.KeyChar))
                    {
                        query = query[..textCursorIndex] + keyInfo.KeyChar + query[textCursorIndex..];
                        textCursorIndex++;
                        cursorIndex = 0;
                    }

                    break;
            }
        }

        AnsiConsole.Clear();
        return result;
    }

    public static DynamicPromptResult<T> ShowDynamic<T>(
        string                           title,
        IAsyncEnumerable<T>              stream,
        Func<List<T>, List<FuzzyChoice>> formatter,
        FuzzyChoice                      cancelChoice,
        int                              pageSize         = 15,
        StreamScanStats?                 stats            = null,
        string?                          initialSelection = null)
    {
        var rawItems   = new List<T>();
        var isScanning = true;
        var cts        = new CancellationTokenSource();

        var backgroundTask = Task.Run(async () =>
                                      {
                                          try
                                          {
                                              await foreach (var item in stream.WithCancellation(cts.Token))
                                              {
                                                  lock (rawItems)
                                                  {
                                                      rawItems.Add(item);
                                                  }
                                              }
                                          }
                                          catch
                                          {
                                              // ignored
                                          }
                                          finally
                                          {
                                              isScanning = false;
                                          }
                                      },
                                      cts.Token);

        var          query           = string.Empty;
        var          cursorIndex     = 0;
        var          selectionSeeded = false;
        var          textCursorIndex = 0;
        FuzzyChoice? result          = null;
        var          isRunning       = true;
        var          shouldRedraw    = true;

        var spinnerFrames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        var spinnerIdx    = 0;

        var lastCount    = -1;
        var lastScanning = true;
        var lastErrors   = -1;
        var lastReceived = -1;
        var ticks        = 0;

        AnsiConsole.Live(new Text("Yükleniyor..."))
                   .Start(ctx =>
                   {
                       while (isRunning)
                       {
                           List<T> currentRaw;
                           lock (rawItems)
                           {
                               currentRaw = [.. rawItems];
                           }

                           var displayChoices = formatter(currentRaw);
                           if (!isScanning || displayChoices.Count > 0)
                           {
                               displayChoices.Add(cancelChoice);
                           }

                           var filtered = FuzzyMatcher.Rank(displayChoices, query);

                           if (!selectionSeeded && !string.IsNullOrEmpty(initialSelection))
                           {
                               var seedIdx = filtered.FindIndex(c =>
                                                                    c.Searchable.Equals(
                                                                        initialSelection,
                                                                        StringComparison.Ordinal));
                               if (seedIdx >= 0)
                               {
                                   cursorIndex     = seedIdx;
                                   selectionSeeded = true;
                                   shouldRedraw    = true;
                               }
                           }

                           if (cursorIndex >= filtered.Count)
                           {
                               cursorIndex = Math.Max(0, filtered.Count - 1);
                           }

                           var errorCount    = stats?.Errors ?? 0;
                           var receivedCount = stats?.Received ?? 0;
                           var stateChanged = filtered.Count != lastCount
                                              || isScanning != lastScanning
                                              || errorCount != lastErrors
                                              || receivedCount != lastReceived
                                              || shouldRedraw;

                           if (stateChanged)
                           {
                               var grid = new Grid();
                               grid.AddColumn();

                               var detailParts = new List<string>();
                               if (receivedCount > 0)
                               {
                                   detailParts.Add($"[grey]{receivedCount} sonuç[/]");
                               }

                               if (errorCount > 0)
                               {
                                   detailParts.Add($"[red]• {errorCount} hata[/]");
                               }

                               var detailSuffix = detailParts.Count > 0
                                                      ? "  " + string.Join(" ", detailParts)
                                                      : string.Empty;
                               var status =
                                   isScanning
                                       ? $"[yellow]{spinnerFrames[spinnerIdx]} Aranıyor...[/]{detailSuffix}"
                                       : $"[green]OK[/]{detailSuffix}";

                               grid.AddRow(new Markup($"[grey]~~[/] [bold cyan]{title}[/] [grey]~~[/]"));
                               grid.AddRow(new Text(string.Empty));
                               grid.AddRow(new Markup(status));
                               grid.AddRow(new Text(string.Empty));
                               grid.AddRow(
                                   new Markup($"[bold cyan]Arama:[/] {FormatQueryWithCursor(query, textCursorIndex)}"));
                               grid.AddRow(new Text(string.Empty));

                               var startIdx = Math.Max(0, cursorIndex - (pageSize / 2));
                               var endIdx   = Math.Min(filtered.Count, startIdx + pageSize);
                               if (endIdx - startIdx < pageSize && startIdx > 0)
                               {
                                   startIdx = Math.Max(0, endIdx - pageSize);
                               }

                               for (var i = startIdx; i < endIdx; i++)
                               {
                                   var choice = filtered[i];
                                   if (i == cursorIndex)
                                   {
                                       grid.AddRow(new Markup($"> {choice.DisplayActive}"));
                                   }
                                   else
                                   {
                                       grid.AddRow(new Markup($"  {choice.Display}"));
                                   }
                               }

                               if (filtered.Count == 0)
                               {
                                   grid.AddRow(new Markup("  [red]Sonuç yok.[/]"));
                               }

                               if (!isScanning && errorCount > 0)
                               {
                                   grid.AddRow(new Markup($"[red]{errorCount} sağlayıcıda hata oluştu.[/]"));
                               }

                               grid.AddRow(new Text(string.Empty));
                               grid.AddRow(
                                   new Markup(
                                       "[grey]Filtrele: [/] [bold white]Enter[/] [grey]Seç[/]  [bold white]Esc[/] [grey]Geri[/]"));

                               ctx.UpdateTarget(grid);

                               lastCount    = filtered.Count;
                               lastScanning = isScanning;
                               lastErrors   = errorCount;
                               lastReceived = receivedCount;
                               shouldRedraw = false;
                           }

                           if (Console.KeyAvailable)
                           {
                               var keyInfo = Console.ReadKey(true);
                               shouldRedraw = true;

                               switch (keyInfo.Key)
                               {
                                   case ConsoleKey.UpArrow:
                                       cursorIndex = filtered.Count > 0
                                                         ? (cursorIndex - 1 + filtered.Count) % filtered.Count
                                                         : 0;
                                       break;
                                   case ConsoleKey.DownArrow:
                                       cursorIndex = filtered.Count > 0
                                                         ? (cursorIndex + 1) % filtered.Count
                                                         : 0;
                                       break;
                                   case ConsoleKey.LeftArrow:
                                       textCursorIndex = Math.Max(0, textCursorIndex - 1);
                                       break;
                                   case ConsoleKey.RightArrow:
                                       textCursorIndex = Math.Min(query.Length, textCursorIndex + 1);
                                       break;
                                   case ConsoleKey.Enter:
                                       if (filtered.Count > 0)
                                       {
                                           result    = filtered[cursorIndex];
                                           isRunning = false;
                                           cts.Cancel();
                                       }

                                       break;
                                   case ConsoleKey.Escape:
                                       result    = null;
                                       isRunning = false;
                                       cts.Cancel();
                                       break;
                                   case ConsoleKey.Backspace:
                                       if (textCursorIndex > 0)
                                       {
                                           query = query[..(textCursorIndex - 1)] + query[textCursorIndex..];
                                           textCursorIndex--;
                                           cursorIndex = 0;
                                       }

                                       break;
                                   case ConsoleKey.Delete:
                                       if (textCursorIndex < query.Length)
                                       {
                                           query       = query[..textCursorIndex] + query[(textCursorIndex + 1)..];
                                           cursorIndex = 0;
                                       }

                                       break;
                                   default:
                                       if (keyInfo.KeyChar != '\0' && !char.IsControl(keyInfo.KeyChar))
                                       {
                                           query = query[..textCursorIndex]
                                                   + keyInfo.KeyChar
                                                   + query[textCursorIndex..];
                                           textCursorIndex++;
                                           cursorIndex = 0;
                                       }

                                       break;
                               }
                           }
                           else
                           {
                               Thread.Sleep(15);
                               ticks++;
                               if (isScanning && ticks >= 10)
                               {
                                   spinnerIdx   = (spinnerIdx + 1) % spinnerFrames.Length;
                                   shouldRedraw = true;
                                   ticks        = 0;
                               }
                           }
                       }
                   });

        List<T> finalItems;
        lock (rawItems)
        {
            finalItems = [.. rawItems];
        }

        AnsiConsole.Clear();

        return new DynamicPromptResult<T>
        {
            Selection        = result,
            AccumulatedItems = finalItems
        };
    }
}

public class DynamicPromptResult<T>
{
    public FuzzyChoice? Selection        { get; set; }
    public List<T>      AccumulatedItems { get; set; } = [];
}
