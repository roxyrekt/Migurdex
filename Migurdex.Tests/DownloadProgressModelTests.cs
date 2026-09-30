using Migurdex.Cli.Services.Downloads;
using Migurdex.Shared.Enums;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadProgressModelTests
{
    // HLS cok trackli indirmelerde "bu bir ses track'i" ayrimi kullaniliyor.
    // `isAudioTrack` parametresi kurucuya aliniyordu ama `IsAudioTrack` alanina
    // ATANMIYORDU -> alan her zaman false kaliyordu, ayrim sessizce kayboluyordu.
    [Fact]
    public void DownloadProgress_AssignsIsAudioTrack()
    {
        var audio = new DownloadProgress(DownloadStage.Downloading, 10, 100, isAudioTrack: true);
        Assert.True(audio.IsAudioTrack);

        var video = new DownloadProgress(DownloadStage.Downloading, 10, 100, isAudioTrack: false);
        Assert.False(video.IsAudioTrack);
    }

    [Fact]
    public void DownloadProgress_DefaultsToVideoTrack()
    {
        var progress = new DownloadProgress(DownloadStage.Downloading, 1, 2);
        Assert.False(progress.IsAudioTrack);
    }

    [Fact]
    public void DownloadProgress_PreservesTrackNameAlongsideAudioFlag()
    {
        var progress = new DownloadProgress(DownloadStage.Downloading,
                                            10,
                                            100,
                                            track: "audio",
                                            isAudioTrack: true);
        Assert.Equal("audio", progress.Track);
        Assert.True(progress.IsAudioTrack);
    }
}
