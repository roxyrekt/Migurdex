using Migurdex.Shared.Enums;
using System.Runtime.CompilerServices;

namespace Migurdex.Shared.Interfaces;

public interface IProvider
{
    string       Name    { get; }
    string       BaseUrl { get; }
    ProviderType Type    { get; }

    ProviderCapabilities Capabilities => GetAutomaticCapabilities();

    private ProviderCapabilities GetAutomaticCapabilities()
    {
        var caps = ProviderCapabilities.None;
        var type = GetType();

        if (IsOverride(type, "SearchAsync"))
        {
            caps |= ProviderCapabilities.Search;
        }

        if (IsOverride(type, "GetGroupsAsync"))
        {
            caps |= ProviderCapabilities.Fansubs;
        }

        return caps;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsOverride(Type type, string methodName)
    {
        var method = type.GetMethod(methodName);

        return method != null && method.DeclaringType != null && method.DeclaringType != typeof(IAnimeProvider);
    }
}
