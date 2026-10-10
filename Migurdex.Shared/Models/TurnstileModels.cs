namespace Migurdex.Shared.Models;

public enum TurnstileSolverSource
{
    Cache,
    UserBrowser,
    DownloadedBrowser,
    Manual
}

public enum BrowserKind
{
    Chromium,
    Firefox,
    Unknown
}

public sealed class BrowserInfo
{
    public string      Name           { get; set; } = string.Empty;
    public string      ExecutablePath { get; set; } = string.Empty;
    public BrowserKind Kind           { get; set; } = BrowserKind.Unknown;
}

public sealed class TurnstileSolveResult
{
    public bool                  Success              { get; set; }
    public string                Host                 { get; set; } = string.Empty;
    public string?               Clearance            { get; set; }
    public DateTime?             ExpiresAtUtc         { get; set; }
    public TurnstileSolverSource Source               { get; set; }
    public string?               BrowserName          { get; set; }
    public string?               Error                { get; set; }
    public bool                  ManualActionRequired { get; set; }
    public bool                  NoChallenge          { get; set; }
}
