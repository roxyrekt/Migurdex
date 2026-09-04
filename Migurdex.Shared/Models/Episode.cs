namespace Migurdex.Shared.Models;

public class Episode
{
    public string Id     { get; set; } = string.Empty;
    public string Title  { get; set; } = string.Empty;
    public double Number { get; set; }
    public int?   Season { get; set; }
}
