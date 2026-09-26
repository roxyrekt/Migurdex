using Migurdex.Cli.Utils;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Migurdex.Cli.Services.Downloads;

public sealed class ExternalProcessResult
{
    public ExternalProcessResult()
    {
    }

    public ExternalProcessResult(
        int                      exitCode,
        string                   standardOutput = "",
        string                   standardError  = "",
        IReadOnlyList<string>?   outputFiles    = null,
        bool                     outputTruncated = false)
    {
        ExitCode        = exitCode;
        StandardOutput  = standardOutput;
        StandardError   = standardError;
        OutputFiles     = outputFiles ?? [];
        OutputTruncated = outputTruncated;
    }

    public int                ExitCode        { get; init; }
    public string             StandardOutput  { get; init; } = string.Empty;
    public string             StandardError   { get; init; } = string.Empty;
    public IReadOnlyList<string> OutputFiles  { get; init; } = [];
    public string?            OutputPath      { get; init; }
    public bool               OutputTruncated { get; init; }

    public int    ReturnCode => ExitCode;
    public string StdOut    => StandardOutput;
    public string StdErr    => StandardError;
}

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    public const int MaxCapturedCharactersPerStream = 256 * 1024;
    public const int MaxCapturedLineCharacters       = 16 * 1024;
    public const int MaxProgressCallbacks            = 4096;

    private static readonly TimeSpan OutputDrainTimeout   = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessStopTimeout   = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PipeStopTimeout      = TimeSpan.FromSeconds(2);

    public Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo  startInfo,
        CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(startInfo, onStandardOutput: null, cancellationToken);
    }

    public Task<ExternalProcessResult> RunAsync(
        ProcessStartInfo  startInfo,
        Action<string>?   onStandardOutput,
        CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(startInfo, onStandardOutput, cancellationToken);
    }

    private static async Task<ExternalProcessResult> RunCoreAsync(
        ProcessStartInfo  startInfo,
        Action<string>?   onStandardOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();

        startInfo.UseShellExecute        = false;
        startInfo.CreateNoWindow         = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError  = true;

        using var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            if (!process.Start())
            {
                throw new ExternalProcessStartException("Dış araç başlatılamadı.");
            }
        }
        catch (Win32Exception)
        {
            throw new ExternalProcessStartException("Dış araç bulunamadı veya başlatılamadı.");
        }
        catch (FileNotFoundException)
        {
            throw new ExternalProcessStartException("Dış araç bulunamadı veya başlatılamadı.");
        }

        ChildProcessTracker.Track(process);
        using var cancellationRegistration = cancellationToken.Register(() => TryKillProcessTree(process));
        using var pipeCancellation          = new CancellationTokenSource();
        var standardOutputTask = CaptureAsync(process.StandardOutput,
                                              onStandardOutput,
                                              pipeCancellation.Token);
        var standardErrorTask = CaptureAsync(process.StandardError,
                                             onStandardOutput,
                                             pipeCancellation.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(standardOutputTask, standardErrorTask)
                          .WaitAsync(OutputDrainTimeout)
                          .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await CleanupAfterTerminationAsync(process,
                                                   standardOutputTask,
                                                   standardErrorTask,
                                                   pipeCancellation)
                    .ConfigureAwait(false);
                throw new ExternalProcessStartException("Dış aracın çıktı akışları zamanında kapatılmadı.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError  = await standardErrorTask.ConfigureAwait(false);
            return new ExternalProcessResult(process.ExitCode,
                                              standardOutput.Text,
                                              standardError.Text,
                                              outputTruncated: standardOutput.Truncated
                                                                  || standardError.Truncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupAfterTerminationAsync(process,
                                               standardOutputTask,
                                               standardErrorTask,
                                               pipeCancellation)
                .ConfigureAwait(false);
            throw;
        }
        catch (ExternalProcessStartException)
        {
            throw;
        }
        catch (IOException)
        {
            await CleanupAfterTerminationAsync(process,
                                               standardOutputTask,
                                               standardErrorTask,
                                               pipeCancellation)
                .ConfigureAwait(false);
            throw new ExternalProcessStartException("Dış araç çıktısı okunamadı.");
        }
        catch (Exception)
        {
            await CleanupAfterTerminationAsync(process,
                                               standardOutputTask,
                                               standardErrorTask,
                                               pipeCancellation)
                .ConfigureAwait(false);
            throw new ExternalProcessStartException("Dış araç çıktısı okunamadı.");
        }
    }

    private static async Task<CaptureResult> CaptureAsync(
        StreamReader     reader,
        Action<string>?  onLine,
        CancellationToken cancellationToken)
    {
        var text         = new StringBuilder();
        var line         = new StringBuilder();
        var buffer       = new char[4096];
        var callbackCount = 0;
        var lineTruncated = false;
        var truncated     = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                                   .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\r')
                {
                    continue;
                }

                if (character == '\n')
                {
                    EmitLine(line,
                             lineTruncated,
                             text,
                             ref truncated,
                             onLine,
                             ref callbackCount);
                    line.Clear();
                    lineTruncated = false;
                    continue;
                }

                if (line.Length < MaxCapturedLineCharacters)
                {
                    line.Append(character);
                }
                else
                {
                    lineTruncated = true;
                }

                if (text.Length < MaxCapturedCharactersPerStream)
                {
                    text.Append(character);
                }
                else
                {
                    truncated = true;
                }
            }
        }

        if (line.Length > 0 || lineTruncated)
        {
            EmitLine(line,
                     lineTruncated,
                     text,
                     ref truncated,
                     onLine,
                     ref callbackCount);
        }

        if (truncated)
        {
            const string marker = "\n[çıktı sınırına ulaşıldı]";
            if (text.Length > marker.Length)
            {
                text.Length = text.Length - marker.Length;
            }

            text.Append(marker);
        }

        return new CaptureResult(text.ToString(), truncated);
    }

    private static void EmitLine(
        StringBuilder    line,
        bool             lineTruncated,
        StringBuilder    text,
        ref bool         truncated,
        Action<string>?  onLine,
        ref int          callbackCount)
    {
        if (lineTruncated)
        {
            truncated = true;
        }

        if (text.Length < MaxCapturedCharactersPerStream)
        {
            text.Append('\n');
        }
        else
        {
            truncated = true;
        }

        if (onLine is not null && callbackCount < MaxProgressCallbacks)
        {
            var value = line.ToString();
            if (lineTruncated)
            {
                value += "…";
            }

            try
            {
                onLine(value);
            }
            catch
            {
                // İlerleme geri çağrısı süreç akışını bozamalıdır.
            }

            callbackCount++;
        }
    }

    private static async Task CleanupAfterTerminationAsync(
        Process                  process,
        Task<CaptureResult>      standardOutputTask,
        Task<CaptureResult>      standardErrorTask,
        CancellationTokenSource pipeCancellation)
    {
        TryKillProcessTree(process);

        using (var processStop = new CancellationTokenSource(ProcessStopTimeout))
        {
            try
            {
                await process.WaitForExitAsync(processStop.Token).ConfigureAwait(false);
            }
            catch
            {
                // bounded cleanup; süreç sonlanmasa bile sonsuza kadar beklenmez
            }
        }

        pipeCancellation.Cancel();
        using (var pipeStop = new CancellationTokenSource(PipeStopTimeout))
        {
            try
            {
                await Task.WhenAll(standardOutputTask, standardErrorTask)
                          .WaitAsync(pipeStop.Token)
                          .ConfigureAwait(false);
            }
            catch
            {
                // Kalan pipe görevleri gözlemlenir; işlenmemiş exception bırakılmaz.
            }
        }

        ObserveFault(standardOutputTask);
        ObserveFault(standardErrorTask);
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(completed => _ = completed.Exception,
                              CancellationToken.None,
                              TaskContinuationOptions.OnlyOnFaulted
                              | TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private sealed record CaptureResult(string Text, bool Truncated);
}
