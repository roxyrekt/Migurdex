using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Migurdex.Shared.Models;
using System.Diagnostics;
using Xunit;

namespace Migurdex.Tests;

/// <summary>
/// C6: <c>LooksLikeFfmpegFailure</c> yt-dlp stderr'inde genel <c>"failed"</c>
/// arıyordu. Bu, "ffmpeg hatası" ile "ağ/404/disk dolu" hatasını ayırt
/// edilemez hale getiriyordu: kullanıcıya "HLS için ffmpeg gerekiyor" mesajı
/// gidiyor, gerçek neden gizleniyordu.
///
/// Düzeltme: teşhis satır bazlı, araç adı + ffmpeg'e özgü sinyal aynı satırda
/// eşleşiyor. Genel <c>"failed"</c>" kaldırıldı, ayrıca ffmpeg'in **var olup
/// işini yapamaması** ayrı bir nötr mesajla ayrıldı.
/// </summary>
public sealed class FfmpegDiagnosisTests
{
    [Fact]
    public void LooksLikeFfmpegFailure_DetectsRealMissingBinary()
    {
        var result = new ExternalProcessResult(1, standardError: "ffmpeg: command not found");

        Assert.True(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public void LooksLikeFfmpegFailure_DetectsNotInstalled()
    {
        var result = new ExternalProcessResult(
            1,
            standardError: "You have requested merging of multiple formats but ffmpeg is not installed.");

        Assert.True(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public void LooksLikeFfmpegFailure_DetectsEnoent()
    {
        var result = new ExternalProcessResult(1, standardError: "Error: spawn ffmpeg ENOENT");

        Assert.True(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public void LooksLikeFfmpegFailure_DetectsWindowsNotRecognized()
    {
        var result = new ExternalProcessResult(
            1,
            standardError: "'ffmpeg' is not recognized as an internal or external command");

        Assert.True(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public void LooksLikeFfmpegFailure_FalseForGenericFailedWithFfmpegElsewhereInOutput()
    {
        // BU, düzeltilen asıl hata: iki koşul da tüm çıktı kümesinde aranıyordu.
        // ffmpeg satırı stderr'in bir yerinde, genel "failed" başka yerde.
        var result = new ExternalProcessResult(
            1,
            standardError: "[ffmpeg] Destination: media.mkv\n"
                           + "ERROR: unable to download video data: HTTP Error 403: Forbidden. Requested format is not available. Download failed");

        Assert.False(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public void LooksLikeFfmpegFailure_FalseForNetworkErrorWithoutFfmpeg()
    {
        var result = new ExternalProcessResult(
            1,
            standardError: "ERROR: fragment 12 not found, unable to download. Download failed");

        Assert.False(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public void LooksLikeFfmpegFailure_FalseWhenFfmpegOnlyAppearsInsideAUrl()
    {
        // URL tuzağı: ham URL'de "ffmpeg" geçiyor ve aynı satırda
        // "Not Found" (404) var. Satır bazlı + URL maskeleme bunu kapatır.
        var result = new ExternalProcessResult(
            1,
            standardError: "ERROR: https://cdn.example/ffmpeg-master/frag12.ts: HTTP Error 404: Not Found");

        Assert.False(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public void LooksLikeFfmpegFailure_FalseForDiskFull()
    {
        var result = new ExternalProcessResult(
            1,
            standardError: "[Merger] ffmpeg\nERROR: write failed: No space left on device");

        Assert.False(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public void LooksLikeFfmpegRuntimeFailure_DetectsExitedWithCode()
    {
        var result = new ExternalProcessResult(1, standardError: "ERROR: ffmpeg exited with code 1");

        Assert.True(YtDlpHlsDownloader.LooksLikeFfmpegRuntimeFailure(result));
        Assert.False(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public void LooksLikeFfmpegRuntimeFailure_DetectsInvalidData()
    {
        var result = new ExternalProcessResult(1, standardError: "ffmpeg: Invalid data found when processing input");

        Assert.True(YtDlpHlsDownloader.LooksLikeFfmpegRuntimeFailure(result));
        Assert.False(YtDlpHlsDownloader.LooksLikeFfmpegFailure(result));
    }

    [Fact]
    public async Task Download_NetworkFailureWithFfmpegInOutputDoesNotBlameFfmpeg()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            var runner = new FakeProcessRunner((_, onStandardOutput, _) =>
            {
                onStandardOutput?.Invoke("[download] Destination: /tmp/job/media.mkv");
                onStandardOutput?.Invoke("ERROR: https://cdn.example/frag3.ts: HTTP Error 404: Not Found. Download failed");
                return Task.FromResult(new ExternalProcessResult(1));
            });

            var exception = await Assert.ThrowsAsync<HlsDownloadException>(() =>
                new YtDlpHlsDownloader(runner, maxAttempts: 1, retryDelay: TimeSpan.Zero)
                    .DownloadAsync(
                        new VideoSource
                        {
                            Url = "https://origin.example/master.m3u8",
                            Type = VideoType.M3U8
                        },
                        destination,
                        cancellationToken: TestContext.Current.CancellationToken));

            Assert.DoesNotContain("ffmpeg gerekiyor", exception.Message, StringComparison.OrdinalIgnoreCase);
            // Gerçek neden korunur.
            Assert.Contains("404", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_RealMissingFfmpegStillReportsInstallHint()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            var calls = 0;
            var runner = new FakeProcessRunner((_, _, _) =>
            {
                calls++;
                return Task.FromResult(new ExternalProcessResult(1, standardError: "ffmpeg: command not found"));
            });

            var exception = await Assert.ThrowsAsync<HlsDownloadException>(() =>
                new YtDlpHlsDownloader(runner, maxAttempts: 3, retryDelay: TimeSpan.Zero)
                    .DownloadAsync(
                        new VideoSource
                        {
                            Url = "https://origin.example/master.m3u8",
                            Type = VideoType.M3U8
                        },
                        destination,
                        cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(YtDlpHlsDownloader.FfmpegMissingMessage, exception.Message);
            // ffmpeg yoksa tekrar denemenin anlamı yok: erken çıkılır.
            Assert.Equal(1, calls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Download_FfmpegRuntimeFailureUsesNeutralMessageAndKeepsCause()
    {
        var root = NewTempDir();
        try
        {
            var destination = new DownloadPathBuilder().Build(root, "Anime", "Episode", 1, 1);
            var runner = new FakeProcessRunner((_, onStandardOutput, _) =>
            {
                onStandardOutput?.Invoke("ERROR: ffmpeg exited with code 1");
                return Task.FromResult(new ExternalProcessResult(1));
            });

            var exception = await Assert.ThrowsAsync<HlsDownloadException>(() =>
                new YtDlpHlsDownloader(runner, maxAttempts: 1, retryDelay: TimeSpan.Zero)
                    .DownloadAsync(
                        new VideoSource
                        {
                            Url = "https://origin.example/master.m3u8",
                            Type = VideoType.M3U8
                        },
                        destination,
                        cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("ffmpeg", exception.Message, StringComparison.OrdinalIgnoreCase);
            // "kurun" yönlendirmesi yapılmaz.
            Assert.DoesNotContain("bulunamadı veya çalışmıyor", exception.Message, StringComparison.Ordinal);
            // Gerçek neden korunur.
            Assert.Contains("exited with code 1", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "migurdex-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeProcessRunner : IExternalProcessRunner
    {
        private readonly Func<ProcessStartInfo, Action<string>?, CancellationToken, Task<ExternalProcessResult>> _handler;

        public FakeProcessRunner(
            Func<ProcessStartInfo, Action<string>?, CancellationToken, Task<ExternalProcessResult>> handler)
        {
            _handler = handler;
        }

        public Task<ExternalProcessResult> RunAsync(
            ProcessStartInfo  startInfo,
            CancellationToken cancellationToken = default)
        {
            return _handler(startInfo, null, cancellationToken);
        }

        public Task<ExternalProcessResult> RunAsync(
            ProcessStartInfo  startInfo,
            Action<string>?   onStandardOutput,
            CancellationToken cancellationToken = default)
        {
            return _handler(startInfo, onStandardOutput, cancellationToken);
        }
    }
}

/// <summary>
/// C4: Linux'ta zorla öldürülmüş ana süreç yetim <c>yt-dlp</c>/<c>ffmpeg</c>
/// bırakıyordu. Windows'ta Job Object (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>)
/// bunu çözer; Linux'ta karşılığı yok.
///
/// Seçilen yöntem: <c>fork</c>/<c>exec</c> sarmalayıcısı + <c>prctl</c> DEĞİL,
/// süreç başlatıldıktan sonra PID izleme. Gerekçe: <c>prctl(PR_SET_PDEATHSIG)</c>
/// çocukta <c>fork</c> ile <c>exec</c> arasında çağrılmalıdır ve .NET'in
/// <c>Process.Start</c> yolu araya girebileceğimiz bir nokta bırakmaz.
/// Sarmalayıcıya sokmak ise argüman kaçışı, sinyal semantiği ve süreç ağacı
/// öldürme davranışını bozma riski taşır — yani gerçek indirmeyi bozar.
/// </summary>
public sealed class LinuxOrphanGuardTests
{
    [Fact]
    public void Guard_IsOnlyActiveOnLinux()
    {
        // Windows ve macOS'ta tüm koruma no-op olmalı; Job Object (Windows)
        // zaten doğru davranışı veriyor, macOS'ta PR_SET_PDEATHSIG yok.
        Assert.Equal(OperatingSystem.IsLinux(), LinuxOrphanGuard.IsSupported);
    }

    [Fact]
    public void ShouldGuard_RejectsInvalidPidPairs()
    {
        Assert.False(LinuxOrphanGuard.ShouldGuard(0, 1));
        Assert.False(LinuxOrphanGuard.ShouldGuard(1, 0));
        Assert.False(LinuxOrphanGuard.ShouldGuard(-1, 1));
        Assert.False(LinuxOrphanGuard.ShouldGuard(7, 7));
        Assert.Equal(OperatingSystem.IsLinux(), LinuxOrphanGuard.ShouldGuard(7, 8));
    }

    [Fact]
    public void Attach_IsSafeOnNonLinuxAndOnAlreadyExitedProcesses()
    {
        // Bu iki durumda da istisna atmamalı: koruma bir iyileştirme,
        // indirmenin çalışma önkoşulu değil.
        using var exited = new Process();
        LinuxOrphanGuard.Attach(exited);
        LinuxOrphanGuard.Attach(null!);
    }

    [Fact]
    public async Task RunAsync_StillCompletesNormallyWithTheGuardInstalled()
    {
        // Komut satırı, argüman listesi ve çalışma dizini DEĞİŞMEMELİ; koruma
        // yalnızca süreç yaşamı boyunca PID izler. Burada normal çalışma
        // doğrulanır (beyaz kutu: gerçek komut, gerçek çıktı).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var startInfo = new ProcessStartInfo
        {
            FileName               = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("[Console]::Out.WriteLine('tamam')");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("printf 'tamam\\n'");
        }

        var result = await new ExternalProcessRunner().RunAsync(startInfo, cts.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("tamam", result.StandardOutput, StringComparison.Ordinal);
    }
}
