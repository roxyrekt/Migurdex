namespace Migurdex.Shared.Models;

public class Subtitle
{
    public string                      Url      { get; set; } = string.Empty;
    public string                      Language { get; set; } = string.Empty;
    public string?                     Label    { get; set; }
    public string?                     Format   { get; set; }
    public Dictionary<string, string>? Headers  { get; set; }
}
