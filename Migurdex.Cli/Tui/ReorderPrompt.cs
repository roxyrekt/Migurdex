using Spectre.Console;

namespace Migurdex.Cli.Tui;

public class ReorderItem
{
    public string Key         { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}

public static class ReorderPrompt
{
    public static List<ReorderItem>? Show(string title, List<ReorderItem> items)
    {
        var list = items.Select(i => new ReorderItem
                        {
                            Key         = i.Key,
                            DisplayName = i.DisplayName
                        })
                        .ToList();
        var  highlightIdx = 0;
        int? grabbedIdx   = null;
        var  isRunning    = true;

        List<ReorderItem>? result = null;

        Grid BuildGrid()
        {
            var grid = new Grid();
            grid.AddColumn();

            grid.AddRow(new Markup($"[bold cyan]{Markup.Escape(title)}[/]"));
            grid.AddRow(new Text(string.Empty));

            for (var i = 0; i < list.Count; i++)
            {
                var item          = list[i];
                var isHighlighted = i == highlightIdx;
                var isGrabbed     = i == grabbedIdx;

                string prefix;
                string contentStyle;

                if (isHighlighted)
                {
                    if (isGrabbed)
                    {
                        prefix       = "[bold yellow] 🤝 › [/]";
                        contentStyle = "bold yellow reverse";
                    }
                    else
                    {
                        prefix       = "[bold cyan]  ›  [/]";
                        contentStyle = "bold white";
                    }
                }
                else
                {
                    if (isGrabbed)
                    {
                        prefix       = "[bold yellow] 🤝   [/]";
                        contentStyle = "bold yellow";
                    }
                    else
                    {
                        prefix       = "     ";
                        contentStyle = "silver";
                    }
                }

                grid.AddRow(new Markup($"{prefix}[{contentStyle}]{Markup.Escape(item.DisplayName)}[/]"));
            }

            grid.AddRow(new Text(string.Empty));
            grid.AddRow(new Markup("[grey]Kontroller[/]"));
            grid.AddRow(
                new Markup(
                    " [cyan]↑/↓[/] [grey]gezin/taşı •[/] [cyan]Space[/] [grey]tut/bırak •[/] [cyan]Enter[/] [grey]kaydet •[/] [cyan]Esc[/] [grey]iptal[/]"));

            return grid;
        }

        void HandleKey(ConsoleKeyInfo keyInfo)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.UpArrow:
                    if (grabbedIdx.HasValue)
                    {
                        if (highlightIdx > 0)
                        {
                            var targetIdx = highlightIdx - 1;

                            (list[highlightIdx], list[targetIdx]) = (list[targetIdx], list[highlightIdx]);
                            highlightIdx                          = targetIdx;
                            grabbedIdx                            = targetIdx;
                        }
                    }
                    else
                    {
                        highlightIdx = list.Count > 0 ? (highlightIdx - 1 + list.Count) % list.Count : 0;
                    }

                    break;

                case ConsoleKey.DownArrow:
                    if (grabbedIdx.HasValue)
                    {
                        if (highlightIdx < list.Count - 1)
                        {
                            var targetIdx = highlightIdx + 1;

                            (list[highlightIdx], list[targetIdx]) = (list[targetIdx], list[highlightIdx]);
                            highlightIdx                          = targetIdx;
                            grabbedIdx                            = targetIdx;
                        }
                    }
                    else
                    {
                        highlightIdx = list.Count > 0 ? (highlightIdx + 1) % list.Count : 0;
                    }

                    break;

                case ConsoleKey.Spacebar:
                    if (grabbedIdx.HasValue)
                    {
                        if (grabbedIdx == highlightIdx)
                        {
                            grabbedIdx = null;
                        }
                    }
                    else
                    {
                        grabbedIdx = highlightIdx;
                    }

                    break;

                case ConsoleKey.Enter:
                    if (grabbedIdx.HasValue)
                    {
                        grabbedIdx = null;
                    }
                    else
                    {
                        result    = list;
                        isRunning = false;
                    }

                    break;

                case ConsoleKey.Escape:
                    result    = null;
                    isRunning = false;
                    break;
            }
        }

        string Fingerprint() =>
            string.Join("\n", list.Select(i => i.Key)) + "|" + highlightIdx + "|" + (grabbedIdx?.ToString() ?? "-");

        AnsiConsole.Clear();
        AnsiConsole.Live(BuildGrid())
                   .Start(ctx =>
                   {
                       var last = string.Empty;
                       while (isRunning)
                       {
                           var fp = Fingerprint();
                           if (!fp.Equals(last, StringComparison.Ordinal))
                           {
                               ctx.UpdateTarget(BuildGrid());
                               last = fp;
                           }

                           if (Console.KeyAvailable)
                           {
                               HandleKey(Console.ReadKey(true));
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
}
