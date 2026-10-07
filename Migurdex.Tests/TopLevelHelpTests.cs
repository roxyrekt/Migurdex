using Migurdex.Cli.Services;
using Xunit;

namespace Migurdex.Tests;

public sealed class TopLevelHelpTests
{
    private const char EscapeChar = (char) 27;

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    [InlineData("--HELP")]
    [InlineData("Help")]
    public void IsTopLevelRequest_RecognizesEveryHelpAlias(string helpToken)
    {
        Assert.True(HelpCommand.IsHelpToken(helpToken));
        Assert.True(HelpCommand.IsTopLevelRequest([helpToken]));
    }

    [Theory]
    [InlineData("download", "naruto", "--help")]
    [InlineData("search", "--help")]
    [InlineData("play", "-h")]
    [InlineData("continue", "--help")]
    [InlineData("update", "--help")]
    [InlineData("auth", "--help")]
    [InlineData("version", "--help")]
    public void IsTopLevelRequest_LeavesSubCommandHelpToSubCommand(params string[] args)
    {
        Assert.False(HelpCommand.IsTopLevelRequest(args));
    }

    [Theory]
    [InlineData()]
    [InlineData("--no-update-check")]
    [InlineData("download", "naruto", "-e", "1")]
    public void IsTopLevelRequest_IsFalseWithoutAHelpRequest(params string[] args)
    {
        Assert.False(HelpCommand.IsTopLevelRequest(args));
    }

    [Fact]
    public void PrintHelp_CoversTuiSubCommandsVersionAndHelp()
    {
        var writer = new StringWriter();
        HelpCommand.PrintHelp(writer);
        var help = writer.ToString();

        Assert.Contains("migurdex ", help, StringComparison.Ordinal); // TUI satırı
        Assert.Contains("TUI", help, StringComparison.Ordinal);
        Assert.Contains("migurdex search <sorgu>", help, StringComparison.Ordinal);
        Assert.Contains("migurdex play <sorgu>", help, StringComparison.Ordinal);
        Assert.Contains("migurdex continue", help, StringComparison.Ordinal);
        Assert.Contains("migurdex download <sorgu>", help, StringComparison.Ordinal);
        Assert.Contains("migurdex blame", help, StringComparison.Ordinal);
        Assert.Contains("migurdex --version", help, StringComparison.Ordinal);
        Assert.Contains("migurdex --help", help, StringComparison.Ordinal);
        Assert.DoesNotContain(EscapeChar.ToString(), help, StringComparison.Ordinal); // yönlendirilmiş çıktıda ANSI kaçmaz
    }

    [Fact]
    public void PrintHelp_ReusesNonInteractiveCommandLines()
    {
        var topLevel = new StringWriter();
        HelpCommand.PrintHelp(topLevel);

        var subCommand = new StringWriter();
        NonInteractiveCommand.PrintHelp(subCommand);

        foreach (var line in subCommand.ToString()
                                  .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                                  .Where(line => line.StartsWith("  migurdex", StringComparison.Ordinal)))
        {
            Assert.Contains(line, topLevel.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Run_WritesHelpToTheGivenWriterAndSucceeds()
    {
        var writer = new StringWriter();

        var exitCode = HelpCommand.Run(writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("migurdex --help", writer.ToString(), StringComparison.Ordinal);
    }
}
