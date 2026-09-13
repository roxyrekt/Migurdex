namespace Migurdex.Core.Services;

public static class MalAppCredentials
{
    public const string ClientId = "8792ad0d9cc7c61bdfd2096bf0af9acf";

    public const int    LoopbackPort        = 46421;
    public const string CallbackPath        = "/callback";
    public const string LoopbackRedirectUri = "http://127.0.0.1:46421/callback";

    public static bool IsConfigured => ClientId.Length > 0;
}
