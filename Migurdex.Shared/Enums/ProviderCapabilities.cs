namespace Migurdex.Shared.Enums;

[Flags]
public enum ProviderCapabilities
{
    None    = 0,
    Search  = 1 << 0,
    Fansubs = 1 << 1
}
