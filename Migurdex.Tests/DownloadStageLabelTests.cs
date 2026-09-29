using Migurdex.Cli.Services.Downloads;
using Xunit;

namespace Migurdex.Tests;

public sealed class DownloadStageLabelTests
{
    [Theory]
    [InlineData("master.faud2-English.mp4", false, "Ses indiriliyor")]
    [InlineData("audio.m4a", false, "Ses indiriliyor")]
    [InlineData("track_Audio.mp3", false, "Ses indiriliyor")]
    [InlineData("master.f5471.mp4", false, "Video indiriliyor")]
    [InlineData("media.mkv", false, "Video indiriliyor")]
    [InlineData(null, false, "Video indiriliyor")]
    [InlineData("master.f302.mp4", true, "Ses indiriliyor")]
    [InlineData("master.f302.mp4", false, "Video indiriliyor")]
    public void Downloading_LabelsAudioAndVideoTracks(string? track, bool isAudioTrack, string expected)
    {
        Assert.Equal(expected, DownloadStageLabel.For(DownloadStage.Downloading, track, isAudioTrack));
    }

    [Fact]
    public void OtherStages_IgnoreTrack()
    {
        Assert.Equal("Altyazı indiriliyor",
                     DownloadStageLabel.For(DownloadStage.Subtitle, "master.faud2-English.mp4"));
        Assert.Equal("Bağlanıyor",
                     DownloadStageLabel.For(DownloadStage.Requesting, null));
    }
}
