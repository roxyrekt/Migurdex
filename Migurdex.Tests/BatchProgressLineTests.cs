using Migurdex.Cli.Tui;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Toplu indirme ekranındaki satırların tek fiziksel satıra sığması bu işin görünür sözü:
/// sığmazsa terminal kayar ve üstte kalıntı kalır (kullanıcı raporu).
/// </summary>
public sealed class BatchProgressLineTests
{
    private const string LongLabel = "S01E02 - İkinci Ejderha, Kanna! (Burada Tamamen Şımartıyoruz)";

    private const string LongDetail = "Video indiriliyor • %83.2 • 346.76 KiB/s";

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(110)]
    [InlineData(160)]
    public void Fit_KeepsRowWithinGivenWidth(int width)
    {
        var fit  = BatchProgressLine.Fit(LongLabel, LongDetail, 2, width);
        var line = "▶ " + fit.Label + BatchProgressLine.Separator + fit.Detail;

        Assert.True(line.Length <= width, $"satır {line.Length} karakter, sınır {width}: {line}");
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(140)]
    public void FitArrow_KeepsRowWithinGivenWidth(int width)
    {
        var fit  = BatchProgressLine.FitArrow(LongLabel, @"E:\Miss Kobayashi's Dragon Maid\S01E03 - Yeni Bir Hayatın Başlangıcı.mp4", 2, width);
        var line = "✓ " + fit.Label + BatchProgressLine.Arrow + fit.Detail;

        Assert.True(line.Length <= width, $"satır {line.Length} karakter, sınır {width}: {line}");
    }

    [Fact]
    public void Fit_PrefersDetailOverLabel()
    {
        var fit = BatchProgressLine.Fit(LongLabel, LongDetail, 2, 80);

        Assert.Equal(LongDetail, fit.Detail);
        Assert.True(fit.Label.Length < LongLabel.Length);
    }

    [Fact]
    public void Fit_KeepsShortInputsUnchanged()
    {
        var fit = BatchProgressLine.Fit("S01E01", "sırada", 2, 80);

        Assert.Equal("S01E01", fit.Label);
        Assert.Equal("sırada", fit.Detail);
    }

    [Fact]
    public void Fit_WithoutDetail_UsesWholeBudgetForLabel()
    {
        var fit = BatchProgressLine.Fit(LongLabel, string.Empty, 2, 40);

        Assert.Equal(38, fit.Label.Length);
        Assert.Equal(string.Empty, fit.Detail);
    }

    [Fact]
    public void Fit_SeparatorAndArrow_HaveSameVisibleWidth()
    {
        Assert.Equal(BatchProgressLine.Separator.Length, BatchProgressLine.Arrow.Length);
    }
}
