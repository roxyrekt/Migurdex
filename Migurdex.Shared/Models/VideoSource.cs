using Migurdex.Shared.Enums;

namespace Migurdex.Shared.Models;

public class VideoSource
{
    public string                      Url       { get; set; } = string.Empty;
    public string                      Quality   { get; set; } = string.Empty;
    public VideoType                   Type      { get; set; } = VideoType.Unknown;
    public string?                     Hoster    { get; set; }
    public string?                     Group     { get; set; }
    public string?                     Language  { get; set; }
    public Dictionary<string, string>? Headers   { get; set; }
    public List<Subtitle>?             Subtitles { get; set; }
}
