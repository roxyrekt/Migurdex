using Spectre.Console;

namespace Migurdex.Cli.Tui;

/// <summary>Tuş işlendikten sonra seçim ekranının durumu.</summary>
public enum MultiSelectOutcome
{
    /// <summary>Ekran açık kalmaya devam eder.</summary>
    None,

    /// <summary>Kullanıcı Enter ile onayladı; <see cref="EpisodeMultiSelectState.SelectedIndices"/> geçerlidir.</summary>
    Confirmed,

    /// <summary>Kullanıcı Esc ile vazgeçti.</summary>
    Cancelled
}

/// <summary>
/// Çoklu seçim ekranının durumu (imleç + işaretli satırlar). Çizim yapmaz, konsola dokunmaz;
/// bu yüzden gerçek bir terminale ihtiyaç duymadan test edilebilir.
/// </summary>
public sealed class EpisodeMultiSelectState
{
    private readonly bool[] _selected;

    public EpisodeMultiSelectState(int count, IEnumerable<int>? preselected = null)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Count     = count;
        _selected = new bool[count];

        foreach (var index in preselected ?? [])
        {
            if (index >= 0 && index < count)
            {
                _selected[index] = true;
            }
        }
    }

    public int Count { get; }

    public int Cursor { get; private set; }

    public int SelectedCount => _selected.Count(flag => flag);

    public bool IsSelected(int index)
    {
        return index >= 0 && index < Count && _selected[index];
    }

    public IReadOnlyList<int> SelectedIndices =>
        [.. Enumerable.Range(0, Count).Where(index => _selected[index])];

    public void MoveUp()
    {
        if (Count > 0)
        {
            Cursor = (Cursor - 1 + Count) % Count;
        }
    }

    public void MoveDown()
    {
        if (Count > 0)
        {
            Cursor = (Cursor + 1) % Count;
        }
    }

    public void Toggle()
    {
        if (Count > 0)
        {
            _selected[Cursor] = !_selected[Cursor];
        }
    }

    public void SelectAll()
    {
        Array.Fill(_selected, true);
    }

    public void ClearAll()
    {
        Array.Fill(_selected, false);
    }
}

/// <summary>Tuş → eylem eşlemesi; <see cref="EpisodeMultiSelectPrompt"/> bunu kullanır.</summary>
public static class EpisodeMultiSelectKeys
{
    public static MultiSelectOutcome Apply(EpisodeMultiSelectState state, ConsoleKey key)
    {
        ArgumentNullException.ThrowIfNull(state);

        switch (key)
        {
            case ConsoleKey.UpArrow:
                state.MoveUp();
                return MultiSelectOutcome.None;
            case ConsoleKey.DownArrow:
                state.MoveDown();
                return MultiSelectOutcome.None;
            case ConsoleKey.Spacebar:
                state.Toggle();
                return MultiSelectOutcome.None;
            case ConsoleKey.A:
                state.SelectAll();
                return MultiSelectOutcome.None;
            case ConsoleKey.N:
                state.ClearAll();
                return MultiSelectOutcome.None;
            case ConsoleKey.Enter:
                return state.SelectedCount > 0 ? MultiSelectOutcome.Confirmed : MultiSelectOutcome.None;
            case ConsoleKey.Escape:
                return MultiSelectOutcome.Cancelled;
            default:
                return MultiSelectOutcome.None;
        }
    }
}

/// <summary>
/// Bölüm listesinde çoklu seçim yapan onay kutusu ekranı. Karar mantığı
/// <see cref="EpisodeMultiSelectState"/> + <see cref="EpisodeMultiSelectKeys"/> içindedir; burada
/// yalnızca çizim ve tuş döngüsü vardır.
///
/// Kontroller: ↑/↓ gezin • Space işaretle • A tümü • N temizle • Enter başlat • Esc geri.
/// Dönüş değeri seçilen satırların artan sıradaki indeksleridir; Esc ve uygulama çıkışında null döner.
/// </summary>
public static class EpisodeMultiSelectPrompt
{
    /// <summary>
    /// Liste dışındaki sabit satırlar: başlık, boşluk, sayfa aralığı, boşluk, "Kontroller" ve
    /// ipucu satırı. Başlık satırları (<c>headerLines</c>) buna eklenir.
    /// </summary>
    private const int PromptChromeRows = 6;

    public static List<int>? Show(
        string                    title,
        IReadOnlyList<string>     labels,
        IEnumerable<string>?      headerLines = null,
        IReadOnlyList<int>?       preselected = null,
        int                       pageSize    = 15)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count == 0)
        {
            return [];
        }

        var headers = headerLines?.Where(line => !string.IsNullOrWhiteSpace(line)).ToList() ?? [];

        // Kısa terminalde sayfa taşarsa terminal kayar ve ekran kalıntısı kalır; sayfa boyu
        // pencereye sığdırılır (uzun terminalde istenen sayfa boyu korunur).
        pageSize = ListWindow.Budget(TuiConsole.WindowHeight,
                                     PromptChromeRows + headers.Count,
                                     pageSize);

        var state   = new EpisodeMultiSelectState(labels.Count, preselected);
        var running = true;
        List<int>? result = null;

        AnsiConsole.Clear();
        AnsiConsole.Live(BuildGrid(title, headers, labels, state, pageSize))
                   .Start(ctx =>
                   {
                       var last = string.Empty;
                       while (running)
                       {
                           if (TuiConsole.IsAppExitRequested)
                           {
                               result  = null;
                               running = false;
                               break;
                           }

                           var fingerprint = BuildFingerprint(state);
                           if (!fingerprint.Equals(last, StringComparison.Ordinal))
                           {
                               ctx.UpdateTarget(BuildGrid(title, headers, labels, state, pageSize));
                               last = fingerprint;
                           }

                           if (Console.KeyAvailable)
                           {
                               var outcome = EpisodeMultiSelectKeys.Apply(state, Console.ReadKey(true).Key);
                               if (outcome == MultiSelectOutcome.Confirmed)
                               {
                                   result  = [.. state.SelectedIndices];
                                   running = false;
                               }
                               else if (outcome == MultiSelectOutcome.Cancelled)
                               {
                                   result  = null;
                                   running = false;
                               }
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

    private static string BuildFingerprint(EpisodeMultiSelectState state)
    {
        var bits = new char[state.Count];
        for (var index = 0; index < state.Count; index++)
        {
            bits[index] = state.IsSelected(index) ? '1' : '0';
        }

        return new string(bits) + "|" + state.Cursor;
    }

    private static Grid BuildGrid(
        string                    title,
        IReadOnlyList<string>     headers,
        IReadOnlyList<string>     labels,
        EpisodeMultiSelectState   state,
        int                       pageSize)
    {
        var grid = new Grid();
        grid.AddColumn();

        grid.AddRow(
            new Markup($"[bold cyan]{Markup.Escape(title)}[/]  [grey]{state.SelectedCount} / {state.Count} seçili[/]"));
        foreach (var header in headers)
        {
            grid.AddRow(new Markup(header));
        }

        grid.AddRow(new Text(string.Empty));

        var startIndex = Math.Max(0, state.Cursor - (pageSize / 2));
        var endIndex   = Math.Min(state.Count, startIndex + pageSize);
        if (endIndex - startIndex < pageSize && startIndex > 0)
        {
            startIndex = Math.Max(0, endIndex - pageSize);
        }

        for (var index = startIndex; index < endIndex; index++)
        {
            var box   = state.IsSelected(index) ? "[green][[x]][/]" : "[grey][[ ]][/]";
            var label = Markup.Escape(labels[index]);
            grid.AddRow(index == state.Cursor
                            ? new Markup($"[bold cyan]›[/] {box} [bold white]{label}[/]")
                            : new Markup($"  {box} [silver]{label}[/]"));
        }

        if (state.Count > endIndex || startIndex > 0)
        {
            grid.AddRow(new Markup($"[grey]{startIndex + 1}-{endIndex} / {state.Count}[/]"));
        }

        grid.AddRow(new Text(string.Empty));
        grid.AddRow(new Markup("[grey]Kontroller[/]"));
        grid.AddRow(
            new Markup(
                " [cyan]↑/↓[/] [grey]gezin •[/] [cyan]Space[/] [grey]işaretle •[/] [cyan]A[/] [grey]tümü •[/] [cyan]N[/] [grey]temizle •[/] [cyan]Enter[/] [grey]başlat •[/] [cyan]Esc[/] [grey]geri[/]"));

        return grid;
    }
}
