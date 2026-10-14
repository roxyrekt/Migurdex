using Migurdex.Cli.Tui;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Toplu indirme ekranındaki çoklu seçim akışının karar kısmı: imleç, işaretleme ve tuş eşlemesi.
/// (Çizim ve canlı döngü gerçek terminal ister; burada kullanıcının gördüğü davranış kuralları test edilir.)
/// </summary>
public sealed class EpisodeMultiSelectStateTests
{
    [Fact]
    public void State_StartsWithNothingSelectedAndCursorAtTheTop()
    {
        var state = new EpisodeMultiSelectState(3);

        Assert.Equal(3, state.Count);
        Assert.Equal(0, state.Cursor);
        Assert.Equal(0, state.SelectedCount);
        Assert.False(state.IsSelected(0));
        Assert.Empty(state.SelectedIndices);
    }

    [Fact]
    public void State_PreselectionIsAppliedAndOutOfRangeIndicesAreIgnored()
    {
        var state = new EpisodeMultiSelectState(3, [2, 0, 0, 99, -1]);

        Assert.Equal(2, state.SelectedCount);
        Assert.Equal(new[] { 0, 2 }, state.SelectedIndices);
    }

    [Fact]
    public void State_CursorWrapsAroundTheList()
    {
        var state = new EpisodeMultiSelectState(3);

        state.MoveUp();
        Assert.Equal(2, state.Cursor);
        state.MoveDown();
        Assert.Equal(0, state.Cursor);
    }

    [Fact]
    public void State_EmptyListIsSafe()
    {
        var state = new EpisodeMultiSelectState(0);

        state.MoveDown();
        state.MoveUp();
        state.Toggle();

        Assert.Equal(0, state.Cursor);
        Assert.Equal(0, state.SelectedCount);
        Assert.Equal(MultiSelectOutcome.None, EpisodeMultiSelectKeys.Apply(state, ConsoleKey.Enter));
    }

    [Fact]
    public void State_NegativeCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EpisodeMultiSelectState(-1));
    }

    [Fact]
    public void Keys_ToggleOnlyTheRowUnderTheCursor()
    {
        var state = new EpisodeMultiSelectState(3);

        EpisodeMultiSelectKeys.Apply(state, ConsoleKey.DownArrow);
        Assert.Equal(MultiSelectOutcome.None, EpisodeMultiSelectKeys.Apply(state, ConsoleKey.Spacebar));

        Assert.Equal(new[] { 1 }, state.SelectedIndices);

        EpisodeMultiSelectKeys.Apply(state, ConsoleKey.Spacebar);
        Assert.Empty(state.SelectedIndices);
    }

    [Fact]
    public void Keys_AAndNClearTheWholeSelection()
    {
        var state = new EpisodeMultiSelectState(4);

        Assert.Equal(MultiSelectOutcome.None, EpisodeMultiSelectKeys.Apply(state, ConsoleKey.A));
        Assert.Equal(4, state.SelectedCount);

        Assert.Equal(MultiSelectOutcome.None, EpisodeMultiSelectKeys.Apply(state, ConsoleKey.N));
        Assert.Equal(0, state.SelectedCount);
    }

    [Fact]
    public void Keys_EnterConfirmsOnlyWhenSomethingIsSelected()
    {
        var state = new EpisodeMultiSelectState(2);

        Assert.Equal(MultiSelectOutcome.None, EpisodeMultiSelectKeys.Apply(state, ConsoleKey.Enter));

        EpisodeMultiSelectKeys.Apply(state, ConsoleKey.Spacebar);
        EpisodeMultiSelectKeys.Apply(state, ConsoleKey.DownArrow);
        EpisodeMultiSelectKeys.Apply(state, ConsoleKey.Spacebar);

        Assert.Equal(MultiSelectOutcome.Confirmed, EpisodeMultiSelectKeys.Apply(state, ConsoleKey.Enter));
        Assert.Equal(new[] { 0, 1 }, state.SelectedIndices);
    }

    [Fact]
    public void Keys_EscapeCancelsAndIgnoresEverythingElse()
    {
        var state = new EpisodeMultiSelectState(3);

        Assert.Equal(MultiSelectOutcome.None, EpisodeMultiSelectKeys.Apply(state, ConsoleKey.X));
        Assert.Equal(MultiSelectOutcome.None, EpisodeMultiSelectKeys.Apply(state, ConsoleKey.Q));
        Assert.Equal(MultiSelectOutcome.Cancelled, EpisodeMultiSelectKeys.Apply(state, ConsoleKey.Escape));
    }

    [Fact]
    public void Keys_ArrowNavigationWrapsAndNeverLeavesTheList()
    {
        var state = new EpisodeMultiSelectState(3);

        for (var step = 0; step < 5; step++)
        {
            EpisodeMultiSelectKeys.Apply(state, ConsoleKey.DownArrow);
            Assert.InRange(state.Cursor, 0, state.Count - 1);
        }

        // 0 -> 1 -> 2 -> 0 -> 1 -> 2
        Assert.Equal(2, state.Cursor);
        Assert.Equal(0, state.SelectedCount);
    }
}
