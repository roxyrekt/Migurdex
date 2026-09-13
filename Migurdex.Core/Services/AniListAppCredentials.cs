namespace Migurdex.Core.Services;

public static class AniListAppCredentials
{
    public const string ClientId     = "51041";
    public const string ClientSecret = "asdPpJqLnHCNEBVFDSbfGrIrm6PQPMN8fmOcrbzx";

    public static bool IsConfigured => ClientId.Length > 0 && ClientSecret.Length > 0;
}
