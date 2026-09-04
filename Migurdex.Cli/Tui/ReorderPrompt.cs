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

        while (isRunning)
        {
            AnsiConsole.Clear();

            var grid = new Grid();
            grid.AddColumn();

            grid.AddRow(new Markup($"[grey]~~[/] [bold yellow]{title}[/] [grey]~~[/]"));
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
                        prefix       = "[bold gold1] 🤝 > [/]";
                        contentStyle = "bold gold1 reverse";
                    }
                    else
                    {
                        prefix       = "[bold pink1]  >  [/]";
                        contentStyle = "bold white";
                    }
                }
                else
                {
                    if (isGrabbed)
                    {
                        prefix       = "[bold gold1] 🤝   [/]";
                        contentStyle = "bold gold1";
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
            grid.AddRow(new Markup("[grey]~~ Kontroller ~~[/]"));
            grid.AddRow(
                new Markup(
                    " [bold pink1]↑/↓[/] [grey]Gezin / Taşı ·[/] [bold gold1]Space[/] [grey]Elemanı Tut / Bırak ·[/] [bold green]Enter[/] [grey]Kaydet ·[/] [bold red]Esc[/] [grey]İptal[/]"));

            AnsiConsole.Write(grid);

            var keyInfo = Console.ReadKey(true);

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
                        return list;
                    }

                    break;

                case ConsoleKey.Escape:
                    return null;
            }
        }

        return null;
    }
}
