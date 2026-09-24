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

    private static bool IsWordDeleteKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            if (keyInfo.Key is ConsoleKey.Backspace or ConsoleKey.W)
            {
                return true;
            }

            if (keyInfo.KeyChar is (char) 8 or (char) 23 or (char) 127)
            {
                return true;
            }
        }

        if (keyInfo.KeyChar is (char) 23)
        {
            return true;
        }

        return false;
    }

    private static void DeleteWordBeforeCursor(ref string query, ref int textCursorIndex)
    {
        if (textCursorIndex <= 0)
        {
            return;
        }

        var target = textCursorIndex;

        while (target > 0 && char.IsWhiteSpace(query[target - 1]))
        {
            target--;
        }

        while (target > 0 && !char.IsWhiteSpace(query[target - 1]))
        {
            target--;
        }

        query           = query[..target] + query[textCursorIndex..];
        textCursorIndex = target;
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

    private static string SelectedRowMarkup(FuzzyChoice choice, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return choice.DisplayActive;
        }

        return Theme.HighlightDisplay(choice.DisplayActive, query);
    }

    private static string UnselectedRowMarkup(FuzzyChoice choice, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return choice.Display;
        }

        return Theme.HighlightDisplay(choice.Display, query);
    }

    private static string FooterMarkup(int cursorIndex, int count, string? customHelp = null)
    {
        var pos = count > 1 ? $" • {cursorIndex + 1}/{count}" : "";
        return $"[grey]{customHelp ?? "↑↓ gez • Enter seç • Esc geri • yazarak filtrele"}{pos}[/]";
    }

    public static FuzzyChoice? Show(
        string                      title,
        IEnumerable<FuzzyChoice>    choices,
        int                         pageSize          = 15,
        string?                     initialSelection  = null,
        IEnumerable<string>?        headerLines       = null,
        Func<string, FuzzyChoice?>? pinnedRowProvider = null,
        string?                     footerHelp        = null)
    {
        var choicesList     = choices.ToList();
        var headersList     = headerLines?.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        var query           = string.Empty;
        var cursorIndex     = ResolveInitialCursor(choicesList, initialSelection);
        var textCursorIndex = 0;

        FuzzyChoice? result    = null;
        var          isRunning = true;

        Grid BuildGrid(List<FuzzyChoice> filtered)
        {
            var grid = new Grid();
            grid.AddColumn();

            var countSuffix = filtered.Count > 0
                                  ? string.IsNullOrWhiteSpace(query)
                                        ? $"  [grey]{filtered.Count} öğe[/]"
                                        : $"  [grey]{filtered.Count} sonuç[/]"
                                  : string.Empty;
            grid.AddRow(new Markup($"[bold cyan]{Markup.Escape(title.TrimEnd(':'))}[/]{countSuffix}"));
            if (headersList != null)
            {
                foreach (var header in headersList)
                {
                    grid.AddRow(new Markup(header));
                }

                grid.AddRow(new Text(string.Empty));
            }

            grid.AddRow(new Markup($"[grey]Ara:[/] {FormatQueryWithCursor(query, textCursorIndex)}"));
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
                    grid.AddRow(new Markup($"[bold cyan]›[/] {SelectedRowMarkup(choice, query)}"));
                }
                else
                {
                    grid.AddRow(new Markup($"  {UnselectedRowMarkup(choice, query)}"));
                }
            }

            if (filtered.Count == 0)
            {
                grid.AddRow(new Markup("  [grey]Sonuç yok.[/]"));
            }

            grid.AddRow(new Text(string.Empty));
            grid.AddRow(new Markup(FooterMarkup(cursorIndex, filtered.Count, footerHelp)));

            return grid;
        }

        void HandleKey(ConsoleKeyInfo keyInfo, List<FuzzyChoice> filtered)
        {
            if (IsWordDeleteKey(keyInfo))
            {
                DeleteWordBeforeCursor(ref query, ref textCursorIndex);
                cursorIndex = 0;
            }
            else
            {
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
        }

        AnsiConsole.Clear();
        AnsiConsole.Live(BuildGrid(FuzzyMatcher.Rank(choicesList, query)))
                   .Start(ctx =>
                   {
                       var lastQuery      = "\0";
                       var lastCursor     = -1;
                       var lastTextCursor = -1;

                       while (isRunning)
                       {
                           var filtered = FuzzyMatcher.Rank(choicesList, query);
                           if (!string.IsNullOrWhiteSpace(query)
                               && pinnedRowProvider?.Invoke(query.Trim()) is { } pinned)
                           {
                               filtered.Insert(0, pinned);
                           }

                           if (cursorIndex >= filtered.Count)
                           {
                               cursorIndex = Math.Max(0, filtered.Count - 1);
                           }

                           if (!query.Equals(lastQuery, StringComparison.Ordinal)
                               || cursorIndex != lastCursor
                               || textCursorIndex != lastTextCursor)
                           {
                               ctx.UpdateTarget(BuildGrid(filtered));
                               lastQuery      = query;
                               lastCursor     = cursorIndex;
                               lastTextCursor = textCursorIndex;
                           }

                           if (Console.KeyAvailable)
                           {
                               HandleKey(Console.ReadKey(true), filtered);
                           }
                           else
                           {
                               Thread.Sleep(15);
                           }
                       }
                   });

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
        string?                          initialSelection = null,
        IEnumerable<string>?             headerLines      = null)
    {
        var rawItems    = new List<T>();
        var headersList = headerLines?.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        var isScanning  = true;
        var cts         = new CancellationTokenSource();

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

        AnsiConsole.Clear();
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
                                       ? $"[yellow]{spinnerFrames[spinnerIdx]} Aranıyor...[/]"
                                       : "[green]Bitti[/]";

                               grid.AddRow(new Markup($"[bold cyan]{Markup.Escape(title)}[/]{detailSuffix}"));
                               if (headersList != null)
                               {
                                   foreach (var header in headersList)
                                   {
                                       grid.AddRow(new Markup(header));
                                   }
                               }

                               grid.AddRow(new Text(string.Empty));
                               grid.AddRow(new Markup(status));
                               grid.AddRow(new Text(string.Empty));
                               grid.AddRow(
                                   new Markup($"[grey]Ara:[/] {FormatQueryWithCursor(query, textCursorIndex)}"));
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
                                       grid.AddRow(new Markup($"[bold cyan]›[/] {SelectedRowMarkup(choice, query)}"));
                                   }
                                   else
                                   {
                                       grid.AddRow(new Markup($"  {UnselectedRowMarkup(choice, query)}"));
                                   }
                               }

                               if (filtered.Count == 0)
                               {
                                   grid.AddRow(new Markup("  [grey]Sonuç yok.[/]"));
                               }

                               if (!isScanning && errorCount > 0)
                               {
                                   grid.AddRow(new Markup($"[red]{errorCount} sağlayıcıda hata oluştu.[/]"));
                               }

                               grid.AddRow(new Text(string.Empty));
                               grid.AddRow(
                                   new Markup(
                                       "[grey]↑↓ gez • Enter seç • Esc geri • yazarak filtrele[/]"));

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

                               if (IsWordDeleteKey(keyInfo))
                               {
                                   DeleteWordBeforeCursor(ref query, ref textCursorIndex);
                                   cursorIndex = 0;
                               }
                               else
                               {
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
