namespace Migurdex.Shared.Models;

public sealed class OperationBlame
{
    public string   Operation   { get; set; } = string.Empty;
    public int      Calls       { get; set; }
    public double   AvgMs       { get; set; }
    public long     MaxMs       { get; set; }
    public int      Errors      { get; set; }
    public int      ClientErrors { get; set; }
    public DateTime LastAt      { get; set; }
}

public sealed class ProviderBlame
{
    public string Operation { get; set; } = string.Empty;
    public string Provider  { get; set; } = string.Empty;
    public int    Calls     { get; set; }
    public double AvgMs     { get; set; }
    public long   MaxMs     { get; set; }
    public int    Timeouts  { get; set; }
    public int    Mismatches { get; set; }
    public int    Matched   { get; set; }
    public int    Empties   { get; set; }
    public int    Errors    { get; set; }
}

public sealed class BlameReport
{
    public DateTime                  Since      { get; set; }
    public List<OperationBlame>      Operations { get; set; } = [];
    public List<ProviderBlame>       Providers  { get; set; } = [];
}
