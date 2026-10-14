using Migurdex.Cli.Tui;
using Migurdex.Cli.Tui.Views;
using Xunit;

namespace Migurdex.Tests;

public sealed class ListWindowTests
{
    [Theory]
    [InlineData(40, 33)]
    [InlineData(30, 23)]
    [InlineData(6, 1)]
    [InlineData(0, 1)]
    public void Budget_ReservesSettingsChromeAndOverflowSlack(int windowHeight, int expected)
    {
        Assert.Equal(expected, ListWindow.Budget(windowHeight, SettingsView.ChromeRows, int.MaxValue));
    }

    [Fact]
    public void SettingsScreen_ContentStaysInsideShortTerminal()
    {
        for (var height = SettingsView.ChromeRows + 3; height <= 80; height++)
        {
            var budget = ListWindow.Budget(height, SettingsView.ChromeRows, int.MaxValue);
            Assert.True(budget + SettingsView.ChromeRows <= height - 2,
                        $"height {height}: {budget} list rows plus chrome {SettingsView.ChromeRows} overflow");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(23)]
    [InlineData(31)]
    [InlineData(100)]
    public void Compute_AlwaysKeepsCursorInsideWindow(int budget)
    {
        const int count = 31;
        for (var cursor = 0; cursor < count; cursor++)
        {
            var (start, end) = ListWindow.Compute(count, cursor, budget);

            Assert.InRange(start, 0, count - 1);
            Assert.InRange(end, 1, count);
            Assert.True(cursor >= start && cursor < end,
                        $"cursor {cursor} is outside [{start}, {end})");
            Assert.True(end - start <= Math.Min(budget, count),
                        $"window {end - start} exceeds budget {budget}");
        }
    }

    [Fact]
    public void Compute_PullsSectionHeadingIntoWindow()
    {
        static bool IsSection(int index) => index % 10 == 0;

        var (start, end) = ListWindow.Compute(30, 11, 1, IsSection);

        Assert.Equal(10, start);
        Assert.True(11 >= start && 11 < end, $"cursor 11 is outside [{start}, {end})");
    }

    [Fact]
    public void Compute_TopAndBottomCursorsShowListEdges()
    {
        const int count = 31;

        var top = ListWindow.Compute(count, 0, 12);
        Assert.Equal(0, top.Start);

        var bottom = ListWindow.Compute(count, count - 1, 12);
        Assert.Equal(count, bottom.End);
        Assert.Equal(count - 12, bottom.Start);
    }

    [Fact]
    public void Compute_EmptyListReturnsEmptyWindow()
    {
        Assert.Equal((0, 0), ListWindow.Compute(0, 0, 12));
    }
}
