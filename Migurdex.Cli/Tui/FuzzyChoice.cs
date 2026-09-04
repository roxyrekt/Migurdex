namespace Migurdex.Cli.Tui;

public class FuzzyChoice
{
    public string  Display         { get; set; } = string.Empty;
    public string  DisplayActive   { get; set; } = string.Empty;
    public string  Searchable      { get; set; } = string.Empty;
    public object? AssociatedValue { get; set; }
}
