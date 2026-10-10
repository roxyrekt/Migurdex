using Migurdex.Cli.Services;
using Xunit;

namespace Migurdex.Tests;

public sealed class SolveCommandTests
{
    [Fact]
    public void TryParse_AcceptsUrlAndFlags()
    {
        Assert.True(SolveCommand.TryParse(["https://ornek.com/bolum/1", "--yes", "--timeout", "30"],
                                          out var opts,
                                          out var error));
        Assert.Null(error);
        Assert.Equal("https://ornek.com/bolum/1", opts.PageUrl);
        Assert.True(opts.Yes);
        Assert.Equal(30, opts.TimeoutSecs!.Value);
    }

    [Fact]
    public void TryParse_AcceptsManualMode()
    {
        Assert.True(SolveCommand.TryParse(["ornek.com", "--manual", "abc123"], out var opts, out _));
        Assert.Equal("abc123", opts.Manual);
    }

    [Fact]
    public void TryParse_AcceptsVerbose()
    {
        Assert.True(SolveCommand.TryParse(["ornek.com", "--verbose"], out var opts, out _));
        Assert.True(opts.Verbose);
    }

    [Fact]
    public void TryParse_AcceptsVisible()
    {
        Assert.True(SolveCommand.TryParse(["ornek.com", "--visible"], out var opts, out _));
        Assert.True(opts.Visible);
    }

    [Theory]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "301")]
    [InlineData("--timeout", "abc")]
    [InlineData("--timeout", "NaN")]
    [InlineData("--bilinmeyen")]
    public void TryParse_RejectsBadFlags(params string[] args)
    {
        Assert.False(SolveCommand.TryParse(args, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void IsCommand_RecognizesSolve()
    {
        Assert.True(NonInteractiveCommand.IsCommand("solve"));
        Assert.True(NonInteractiveCommand.IsCommand("SOLVE"));
        Assert.False(HelpCommand.IsTopLevelRequest(["solve", "--help"]));
    }
}
