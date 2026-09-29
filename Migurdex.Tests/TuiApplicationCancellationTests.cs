using Migurdex.Cli.Tui;
using Xunit;

namespace Migurdex.Tests;

public sealed class TuiApplicationCancellationTests
{
    [Fact]
    public void ModalScope_TracksActivityAndClearsOnDispose()
    {
        TuiApplicationCancellation.Reset();
        try
        {
            Assert.False(TuiApplicationCancellation.IsModalActive);

            using (TuiApplicationCancellation.BeginModal(new CancellationTokenSource()))
            {
                Assert.True(TuiApplicationCancellation.IsModalActive);
            }

            Assert.False(TuiApplicationCancellation.IsModalActive);
        }
        finally
        {
            TuiApplicationCancellation.Reset();
        }
    }

    [Fact]
    public void CancelModal_CancelsOnlyTheRegisteredSource()
    {
        TuiApplicationCancellation.Reset();
        try
        {
            using var modalCts = new CancellationTokenSource();
            using (TuiApplicationCancellation.BeginModal(modalCts))
            {
                Assert.True(TuiApplicationCancellation.CancelModal());
                Assert.True(modalCts.IsCancellationRequested);
                Assert.True(TuiApplicationCancellation.IsModalActive);
                Assert.False(TuiApplicationCancellation.IsCancellationRequested);
            }

            Assert.False(TuiApplicationCancellation.CancelModal());
        }
        finally
        {
            TuiApplicationCancellation.Reset();
        }
    }
}
