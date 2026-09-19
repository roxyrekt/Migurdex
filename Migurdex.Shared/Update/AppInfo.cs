using System.Reflection;

namespace Migurdex.Shared.Update;

public static class AppInfo
{
    public static string GetVersion()
    {
        var asm  = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            return NormalizeTag(info.Split('+')[0]);
        }

        var v = asm.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(v) ? "0.0.0-dev" : NormalizeTag(v);
    }

    public static bool IsDevBuild => GetVersion().StartsWith("0.0.0", StringComparison.Ordinal);

    public static string NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        var t = tag.Trim();
        if (t.StartsWith('v') || t.StartsWith('V'))
        {
            t = t[1..];
        }

        return t.Trim();
    }

    public static int CompareVersions(string? a, string? b)
    {
        var na = NormalizeTag(a);
        var nb = NormalizeTag(b);

        if (na.Equals(nb, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(na))
        {
            return string.IsNullOrEmpty(nb) ? 0 : -1;
        }

        if (string.IsNullOrEmpty(nb))
        {
            return 1;
        }

        var (coreA, preA) = SplitCore(na);
        var (coreB, preB) = SplitCore(nb);

        var coreCmp = CompareCore(coreA, coreB);
        if (coreCmp != 0)
        {
            return coreCmp;
        }

        var hasPreA = !string.IsNullOrEmpty(preA);
        var hasPreB = !string.IsNullOrEmpty(preB);
        if (!hasPreA && !hasPreB)
        {
            return 0;
        }

        if (!hasPreA)
        {
            return 1;
        }

        if (!hasPreB)
        {
            return -1;
        }

        return ComparePreRelease(preA!, preB!);
    }

    private static (string Core, string? Pre) SplitCore(string v)
    {
        var plus = v.IndexOf('+');
        if (plus >= 0)
        {
            v = v[..plus];
        }

        var dash = v.IndexOf('-');
        if (dash < 0)
        {
            return (v, null);
        }

        return (v[..dash], v[(dash + 1)..]);
    }

    private static int CompareCore(string a, string b)
    {
        var pa  = a.Split('.');
        var pb  = b.Split('.');
        var len = Math.Max(pa.Length, pb.Length);

        for (var i = 0; i < len; i++)
        {
            var xa = i < pa.Length && int.TryParse(pa[i], out var va) ? va : 0;
            var xb = i < pb.Length && int.TryParse(pb[i], out var vb) ? vb : 0;
            if (xa != xb)
            {
                return xa.CompareTo(xb);
            }
        }

        return 0;
    }

    private static int ComparePreRelease(string a, string b)
    {
        var pa  = a.Split('.');
        var pb  = b.Split('.');
        var len = Math.Max(pa.Length, pb.Length);

        for (var i = 0; i < len; i++)
        {
            if (i >= pa.Length)
            {
                return -1;
            }

            if (i >= pb.Length)
            {
                return 1;
            }

            var xa   = pa[i];
            var xb   = pb[i];
            var numA = int.TryParse(xa, out var va);
            var numB = int.TryParse(xb, out var vb);

            int cmp;
            if (numA && numB)
            {
                cmp = va.CompareTo(vb);
            }
            else if (numA)
            {
                cmp = -1;
            }
            else if (numB)
            {
                cmp = 1;
            }
            else
            {
                cmp = string.Compare(xa, xb, StringComparison.OrdinalIgnoreCase);
            }

            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }
}
