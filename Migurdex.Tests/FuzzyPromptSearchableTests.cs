using Migurdex.Cli.Tui;
using Spectre.Console;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
///     FuzzyPrompt.Show'un `searchable: false` modu (tek seçenekli indirme sonuç ekranı):
///     `Ara:` filtre satırı ve altındaki boş satır üretilmez, sorgu değiştirilemez;
///     gezinme (↑↓) ve seçim (Enter/Esc) davranışı aynen korunur.
/// </summary>
public sealed class FuzzyPromptSearchableTests
{
    [Fact]
    public void BuildGrid_SearchableFalse_OmitsSearchRow_AndKeepsResultScreenBasics()
    {
        var grid = FuzzyPrompt.BuildGrid("İndirme tamamlandı",
                                         ["[green]Video indirildi.[/]",
                                          "[grey]Video:[/] [white]C:\\Anime\\S01E05 - Bölüm.mp4[/]"],
                                         [Theme.BackChoice()],
                                         query:           string.Empty,
                                         cursorIndex:     0,
                                         textCursorIndex: 0,
                                         pageSize:        15,
                                         footerHelp:      "Enter devam • Esc geri",
                                         searchable:      false);

        var output = Render(grid);

        Assert.DoesNotContain("Ara:", output);
        Assert.Contains("İndirme tamamlandı", output);
        Assert.Contains("1 öğe", output);
        Assert.Contains("Geri", output);
        Assert.Contains("Enter devam • Esc geri", output);
    }

    [Fact]
    public void BuildGrid_SearchableTrue_ShowsSearchRow()
    {
        var grid = FuzzyPrompt.BuildGrid("İndirme tamamlandı",
                                         null,
                                         [Theme.BackChoice()],
                                         string.Empty,
                                         0,
                                         0,
                                         15,
                                         null,
                                         searchable: true);

        Assert.Contains("Ara:", Render(grid));
    }

    [Theory]
    [InlineData('a', ConsoleKey.A, false)]
    [InlineData('A', ConsoleKey.A, false)]
    [InlineData('ğ', ConsoleKey.NoName, false)]
    [InlineData('5', ConsoleKey.D5, false)]
    [InlineData(' ', ConsoleKey.Spacebar, false)]
    [InlineData('\b', ConsoleKey.Backspace, false)]
    [InlineData('\0', ConsoleKey.Delete, false)]
    [InlineData('\0', ConsoleKey.LeftArrow, false)]
    [InlineData('\0', ConsoleKey.RightArrow, false)]
    [InlineData('\u007f', ConsoleKey.Backspace, true)]
    [InlineData('\u0017', ConsoleKey.W, true)]
    public void HandleKey_SearchableFalse_QueryEditingKeys_DoNotChangeQuery(char keyChar, ConsoleKey key, bool control)
    {
        var query           = string.Empty;
        var cursorIndex     = 0;
        var textCursorIndex = 0;
        FuzzyChoice? result    = null;
        var          isRunning = true;
        var          filtered  = new List<FuzzyChoice> { Theme.BackChoice() };

        FuzzyPrompt.HandleKey(new ConsoleKeyInfo(keyChar, key, shift: false, alt: false, control),
                              filtered,
                              ref query,
                              ref cursorIndex,
                              ref textCursorIndex,
                              searchable: false,
                              ref result,
                              ref isRunning);

        Assert.Equal(string.Empty, query);
        Assert.Equal(0, textCursorIndex);
        Assert.Equal(0, cursorIndex);
        Assert.Null(result);
        Assert.True(isRunning);
    }

    [Fact]
    public void HandleKey_SearchableFalse_Enter_SelectsCurrentChoice()
    {
        var query           = string.Empty;
        var cursorIndex     = 0;
        var textCursorIndex = 0;
        FuzzyChoice? result    = null;
        var          isRunning = true;
        var          back      = Theme.BackChoice();

        FuzzyPrompt.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
                              [back],
                              ref query,
                              ref cursorIndex,
                              ref textCursorIndex,
                              searchable: false,
                              ref result,
                              ref isRunning);

        Assert.Same(back, result);
        Assert.False(isRunning);
        Assert.Equal(string.Empty, query);
    }

    [Fact]
    public void HandleKey_SearchableFalse_Escape_Cancels()
    {
        var query           = string.Empty;
        var cursorIndex     = 0;
        var textCursorIndex = 0;
        FuzzyChoice? result    = null;
        var          isRunning = true;

        FuzzyPrompt.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false),
                              [Theme.BackChoice()],
                              ref query,
                              ref cursorIndex,
                              ref textCursorIndex,
                              searchable: false,
                              ref result,
                              ref isRunning);

        Assert.Null(result);
        Assert.False(isRunning);
        Assert.Equal(string.Empty, query);
    }

    [Fact]
    public void HandleKey_SearchableFalse_Arrows_MoveCursor()
    {
        var query           = string.Empty;
        var cursorIndex     = 0;
        var textCursorIndex = 0;
        FuzzyChoice? result    = null;
        var          isRunning = true;
        var          filtered  = new List<FuzzyChoice>
                                 {
                                     Theme.MenuItem("İndir"),
                                     Theme.MenuItem("Oynat"),
                                     Theme.BackChoice()
                                 };

        void Press(ConsoleKey key)
        {
            FuzzyPrompt.HandleKey(new ConsoleKeyInfo('\0', key, false, false, false),
                                  filtered,
                                  ref query,
                                  ref cursorIndex,
                                  ref textCursorIndex,
                                  searchable: false,
                                  ref result,
                                  ref isRunning);
        }

        Press(ConsoleKey.DownArrow);
        Assert.Equal(1, cursorIndex);
        Press(ConsoleKey.DownArrow);
        Assert.Equal(2, cursorIndex);
        Press(ConsoleKey.UpArrow);
        Assert.Equal(1, cursorIndex);
        Press(ConsoleKey.UpArrow);
        Assert.Equal(0, cursorIndex);
        Press(ConsoleKey.UpArrow);
        Assert.Equal(2, cursorIndex);

        Assert.True(isRunning);
        Assert.Null(result);
        Assert.Equal(string.Empty, query);
    }

    [Fact]
    public void HandleKey_SearchableTrue_Characters_AppendToQuery()
    {
        var query           = string.Empty;
        var cursorIndex     = 0;
        var textCursorIndex = 0;
        FuzzyChoice? result    = null;
        var          isRunning = true;
        var          filtered  = new List<FuzzyChoice> { Theme.BackChoice() };

        void Press(char keyChar, ConsoleKey key)
        {
            FuzzyPrompt.HandleKey(new ConsoleKeyInfo(keyChar, key, false, false, false),
                                  filtered,
                                  ref query,
                                  ref cursorIndex,
                                  ref textCursorIndex,
                                  searchable: true,
                                  ref result,
                                  ref isRunning);
        }

        Press('a', ConsoleKey.A);
        Assert.Equal("a", query);
        Press('b', ConsoleKey.B);
        Assert.Equal("ab", query);
        Assert.Equal(2, textCursorIndex);
        Assert.True(isRunning);
    }

    [Fact]
    public void HandleKey_SearchableTrue_Backspace_RemovesFromQuery()
    {
        var query           = "ab";
        var cursorIndex     = 0;
        var textCursorIndex = 2;
        FuzzyChoice? result    = null;
        var          isRunning = true;
        var          filtered  = new List<FuzzyChoice> { Theme.BackChoice() };

        FuzzyPrompt.HandleKey(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
                              filtered,
                              ref query,
                              ref cursorIndex,
                              ref textCursorIndex,
                              searchable: true,
                              ref result,
                              ref isRunning);

        Assert.Equal("a", query);
        Assert.Equal(1, textCursorIndex);
        Assert.True(isRunning);
    }

    private static string Render(Grid grid)
    {
        var writer  = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
                                         {
                                             Ansi = AnsiSupport.No,
                                             Out  = new AnsiConsoleOutput(writer)
                                         });
        console.Profile.Width = 80;
        console.Write(grid);

        return writer.ToString();
    }
}
