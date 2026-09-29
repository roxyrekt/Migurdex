using Migurdex.Cli.Services.Downloads;
using System.Diagnostics;
using Xunit;

namespace Migurdex.Tests;

public sealed class ExternalProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_LimitsCapturedOutput()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new ExternalProcessRunner().RunAsync(
            ShellCommand("large-output"),
            cts.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput.Length <= ExternalProcessRunner.MaxCapturedCharactersPerStream + 64);
    }

    [Fact]
    public async Task RunAsync_CancellationIsBoundedAndDoesNotLeavePipeWaitIndefinite()
    {
        using var cts = new CancellationTokenSource();
        var run = new ExternalProcessRunner().RunAsync(ShellCommand("infinite-output"), cts.Token);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        cts.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await run.WaitAsync(TimeSpan.FromSeconds(12), TestContext.Current.CancellationToken));
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task RunAsync_ReportsStandardOutputLinesForProgress()
    {
        var seen = new List<string>();
        var result = await new ExternalProcessRunner().RunAsync(
            ShellCommand("progress"),
            line => seen.Add(line),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(seen, line => line.Contains("50%", StringComparison.Ordinal));
    }

    private static ProcessStartInfo ShellCommand(string kind)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow  = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(kind switch
            {
                "large-output" => "[Console]::Out.Write('x' * 1000000)",
                "infinite-output" => "while ($true) { [Console]::Out.Write('x'); Start-Sleep -Milliseconds 10 }",
                _ => "[Console]::Out.WriteLine('[download] 50% of 10MiB')"
            });
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(kind switch
            {
                "large-output" => "head -c 1000000 /dev/zero | tr '\\0' x",
                "infinite-output" => "yes x",
                _ => "printf '%s\\n' '[download] 50% of 10MiB'"
            });
        }

        return startInfo;
    }
}
