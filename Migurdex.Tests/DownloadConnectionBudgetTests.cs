using Migurdex.Cli.Services.Downloads;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// Toplu indirmede toplam parça bağlantısı bütçesi: 8 dosya × 4 parça = 32 bağlantı sunucuyu
/// boğuyordu; bütçe dosya başına parçayı eşzamanlılığa böler. Bütçe süreç genelinde tek bir
/// durum olduğu için kuyruk testleriyle aynı koleksiyonda, sırayla koşar.
/// </summary>
[Collection("DownloadConnectionBudget")]
public sealed class DownloadConnectionBudgetTests
{
    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(6, 1)]
    [InlineData(8, 1)]
    [InlineData(16, 1)]
    public void Apply_SplitsBudgetAcrossParallelFiles(int parallelFiles, int expectedSegments)
    {
        try
        {
            DownloadConnectionBudget.Apply(parallelFiles);

            Assert.Equal(expectedSegments, DownloadConnectionBudget.SegmentsPerFile);
        }
        finally
        {
            DownloadConnectionBudget.Reset();
        }
    }

    [Fact]
    public void SegmentsPerFile_NeverExceedsSingleFileCeiling()
    {
        try
        {
            foreach (var files in new[] { 1, 2, 4, 8, 32 })
            {
                DownloadConnectionBudget.Apply(files);

                Assert.InRange(DownloadConnectionBudget.SegmentsPerFile,
                               1,
                               DownloadConnectionBudget.DefaultSegmentsPerFile);
            }
        }
        finally
        {
            DownloadConnectionBudget.Reset();
        }
    }

    [Fact]
    public void Reset_RestoresSingleDownloadDefault()
    {
        DownloadConnectionBudget.Apply(8);
        DownloadConnectionBudget.Reset();

        Assert.Equal(DownloadConnectionBudget.DefaultSegmentsPerFile,
                     DownloadConnectionBudget.SegmentsPerFile);
    }

    [Fact]
    public void TotalConnections_StayWithinBudget()
    {
        try
        {
            foreach (var files in new[] { 1, 2, 4, 8 })
            {
                DownloadConnectionBudget.Apply(files);

                Assert.True(files * DownloadConnectionBudget.SegmentsPerFile
                            <= DownloadConnectionBudget.TotalBudget);
            }
        }
        finally
        {
            DownloadConnectionBudget.Reset();
        }
    }
}
