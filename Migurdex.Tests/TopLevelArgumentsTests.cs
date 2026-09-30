using Migurdex.Cli.Services;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// BULGU 25: TUI'ye düşen bilinmeyen argümanlar. Argümansız `migurdex` ve
/// `migurdex --no-update-check` TUI'yi açmaya devam etmeli; geri kalan her şey
/// alt komutlarla aynı biçimde kullanım hatası (exit 2) olmalı.
/// </summary>
public sealed class TopLevelArgumentsTests
{
    private const char EscapeChar = (char) 27;

    [Fact]
    public void TuiFlags_ContainsOnlyTheUpdateCheckSkip()
    {
        Assert.Equal(["--no-update-check"], TopLevelArguments.TuiFlags);
    }

    [Fact]
    public void IsTuiFlag_IsCaseInsensitive()
    {
        Assert.True(TopLevelArguments.IsTuiFlag("--no-update-check"));
        Assert.True(TopLevelArguments.IsTuiFlag("--NO-UPDATE-CHECK"));
        Assert.False(TopLevelArguments.IsTuiFlag("--no-tui"));
        Assert.False(TopLevelArguments.IsTuiFlag("bogus"));
    }

    [Theory]
    [InlineData()]
    [InlineData("--no-update-check")]
    [InlineData("--NO-UPDATE-CHECK")]
    [InlineData("--no-update-check", "--no-update-check")]
    public void TryValidate_AcceptsArgumentsThatMayOpenTheTui(params string[] args)
    {
        Assert.True(TopLevelArguments.TryValidate(args, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--bilinmeyen")]
    [InlineData("--bilinmeyen-bayrak")]
    [InlineData("bogus")]
    [InlineData("-x")]
    [InlineData("--no-update-check", "--bilinmeyen")]
    [InlineData("migurdex")]
    [InlineData("")]
    public void TryValidate_RejectsAnythingTheTuiDoesNotUnderstand(params string[] args)
    {
        Assert.False(TopLevelArguments.TryValidate(args, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidate_ReportsTheFirstOffendingArgumentOnly()
    {
        Assert.False(TopLevelArguments.TryValidate(["--birinci", "--ikinci"], out var error));
        Assert.Equal("Bilinmeyen bayrak: --birinci", error);
    }

    [Theory]
    [InlineData("--bilinmeyen-bayrak", "Bilinmeyen bayrak: --bilinmeyen-bayrak")]
    [InlineData("bogus", "Bilinmeyen komut: bogus")]
    public void TryValidate_DistinguishesUnknownFlagFromUnknownCommand(string arg, string expected)
    {
        Assert.False(TopLevelArguments.TryValidate([arg], out var error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void PrintUsageError_WritesErrorAndUsageToTheGivenWriterAndReturnsTwo()
    {
        var writer = new StringWriter();

        var exitCode = TopLevelArguments.PrintUsageError(writer, "Bilinmeyen bayrak: --bilinmeyen");

        var output = writer.ToString();
        Assert.Equal(2, exitCode);
        Assert.Contains("Hata: Bilinmeyen bayrak: --bilinmeyen", output, StringComparison.Ordinal);
        Assert.Contains("Kullanım: migurdex --help", output, StringComparison.Ordinal);
        Assert.DoesNotContain(EscapeChar.ToString(), output, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageErrorExitCode_MatchesTheSubCommandContract()
    {
        // Alt komutlar da (download --bilinmeyen vb.) 2 döner; üst düzey ayrışmamalı.
        var writer = new StringWriter();

        Assert.Equal(2, TopLevelArguments.PrintUsageError(writer, "Bilinmeyen komut: bogus"));
    }
}
