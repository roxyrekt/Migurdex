using Spectre.Console;

namespace Migurdex.Cli.Tui;

public static class Theme
{
    public const string Primary    = "cyan";
    public const string Text       = "silver";
    public const string TextStrong = "white";
    public const string Muted      = "grey";
    public const string Success    = "green";
    public const string Warning    = "yellow";
    public const string Danger     = "red";

    public const int MaxTitleLength = 48;
    public const int MaxLineWidth   = 100;

    public static string Header(string title, string? subtitle = null)
    {
        var safe = Markup.Escape(title.Trim());
        return string.IsNullOrWhiteSpace(subtitle)
                   ? $"[bold {Primary}]{safe}[/]"
                   : $"[bold {Primary}]{safe}[/] [grey]• {Markup.Escape(subtitle.Trim())}[/]";
    }

    public static void WriteHeader(string title, string? subtitle = null)
    {
        AnsiConsole.MarkupLine(Header(title, subtitle));
        AnsiConsole.WriteLine();
    }

    public static string FooterHelp(string help = "↑↓ gez • Enter seç • Esc geri")
    {
        return $"[grey]{Markup.Escape(help)}[/]";
    }

    public static string Item(string text)
    {
        return $"[{Text}]{Markup.Escape(text)}[/]";
    }

    public static string ItemMarkup(string markup)
    {
        return $"[{Text}]{markup}[/]";
    }

    public static string ItemActive(string text)
    {
        return $"[bold {TextStrong} on grey23] {Markup.Escape(text)} [/]";
    }

    public static string ItemActiveMarkup(string markup)
    {
        return $"[bold {TextStrong} on grey23] {markup} [/]";
    }

    public static FuzzyChoice MenuItem(string searchable, string? display = null)
    {
        var label = display ?? searchable;
        return new FuzzyChoice
        {
            Display       = Item(label),
            DisplayActive = ItemActive(label),
            Searchable    = searchable
        };
    }

    public static FuzzyChoice MenuItemMarkup(string searchable, string displayMarkup, string activeMarkup)
    {
        return new FuzzyChoice
        {
            Display       = ItemMarkup(displayMarkup),
            DisplayActive = ItemActiveMarkup(activeMarkup),
            Searchable    = searchable
        };
    }

    public static FuzzyChoice BackChoice(string label = "Geri")
    {
        return new FuzzyChoice
        {
            Display       = $"[{Danger}]{Markup.Escape(label)}[/]",
            DisplayActive = $"[bold {TextStrong} on darkred] {Markup.Escape(label)} [/]",
            Searchable    = label,
            IsAction      = true
        };
    }

    public static FuzzyChoice ActionChoice(string label, string color = Warning)
    {
        return new FuzzyChoice
        {
            Display       = $"[{color}]{Markup.Escape(label)}[/]",
            DisplayActive = $"[bold {TextStrong} on grey23] {Markup.Escape(label)} [/]",
            Searchable    = label,
            IsAction      = true
        };
    }

    public static FuzzyChoice PlayChoice(string label)
    {
        return new FuzzyChoice
        {
            Display       = $"[bold {Success}]▶ {Markup.Escape(label)}[/]",
            DisplayActive = $"[bold {TextStrong} on darkgreen] ▶ {Markup.Escape(label)} [/]",
            Searchable    = label,
            IsAction      = true
        };
    }

    public static FuzzyChoice AsAction(FuzzyChoice choice)
    {
        choice.IsAction = true;
        return choice;
    }

    public static string Badge(string text, string color = Primary)
    {
        return $"[{color}][[{Markup.Escape(text)}]][/]";
    }

    public static string SuccessText(string text) => $"[{Success}]{Markup.Escape(text)}[/]";
    public static string DangerText(string  text) => $"[{Danger}]{Markup.Escape(text)}[/]";
    public static string MutedText(string   text) => $"[{Muted}]{Markup.Escape(text)}[/]";

    public static T Ask<T>(string label, T defaultValue)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<T>($"[cyan]{Markup.Escape(label)}[/]")
                .PromptStyle(Primary)
                .AllowEmpty()
                .DefaultValue(defaultValue));
    }

    public static bool Confirm(string markup, bool defaultValue = true)
    {
        return AnsiConsole.Prompt(
            new ConfirmationPrompt(markup)
            {
                DefaultValue = defaultValue
            });
    }

    public static string HighlightDisplay(string markup, string query)
    {
        var tokens = query.Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return markup;
        }

        var sb      = new System.Text.StringBuilder(markup.Length + 32);
        var textBuf = new System.Text.StringBuilder();

        var i = 0;
        while (i < markup.Length)
        {
            var c = markup[i];
            if (c == '[' && i + 1 < markup.Length && markup[i + 1] == '[')
            {
                textBuf.Append('[');
                i += 2;
            }
            else if (c == ']' && i + 1 < markup.Length && markup[i + 1] == ']')
            {
                textBuf.Append(']');
                i += 2;
            }
            else if (c == '[')
            {
                FlushText();
                var end = markup.IndexOf(']', i + 1);
                if (end < 0)
                {
                    textBuf.Append(markup[i..]);
                    break;
                }

                sb.Append(markup[i..(end + 1)]);
                i = end + 1;
            }
            else
            {
                textBuf.Append(c);
                i++;
            }
        }

        FlushText();
        return sb.ToString();

        void FlushText()
        {
            if (textBuf.Length == 0)
            {
                return;
            }

            sb.Append(HighlightTextRun(textBuf.ToString(), tokens));
            textBuf.Clear();
        }
    }

    private static string HighlightTextRun(string text, string[] tokens)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var token in tokens)
        {
            var from = 0;
            while (from < text.Length)
            {
                var idx = text.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    break;
                }

                ranges.Add((idx, idx + token.Length));
                from = idx + 1;
            }
        }

        if (ranges.Count == 0)
        {
            return Markup.Escape(text);
        }

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
            }
            else
            {
                merged.Add(r);
            }
        }

        var sb  = new System.Text.StringBuilder(text.Length + 32);
        var pos = 0;
        foreach (var (start, end) in merged)
        {
            sb.Append(Markup.Escape(text[pos..start]));
            sb.Append($"[bold {Primary}]{Markup.Escape(text[start..end])}[/]");
            pos = end;
        }

        sb.Append(Markup.Escape(text[pos..]));
        return sb.ToString();
    }

    public static string Ellipsize(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        if (maxLength <= 3)
        {
            return text[..maxLength];
        }

        return text[..(maxLength - 3)] + "...";
    }

    public static List<string> WrapText(string text, int width = 92, int maxLines = 3)
    {
        var clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (clean.Contains("  "))
        {
            clean = clean.Replace("  ", " ");
        }

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var i     = 0;

        while (i < words.Length && lines.Count < maxLines)
        {
            var sb       = new System.Text.StringBuilder();
            var lastLine = lines.Count == maxLines - 1;

            while (i < words.Length)
            {
                var w    = words[i];
                var need = sb.Length == 0 ? w.Length : sb.Length + 1 + w.Length;
                if (need > width)
                {
                    break;
                }

                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(w);
                i++;
            }

            if (sb.Length == 0 && i < words.Length)
            {
                sb.Append(Ellipsize(words[i], width));
                i++;
            }

            var line = sb.ToString();
            if (lastLine && i < words.Length)
            {
                line = Ellipsize(line, width);
            }

            lines.Add(line);
        }

        return lines;
    }

    public static string SelectionMarker => "›";
}
