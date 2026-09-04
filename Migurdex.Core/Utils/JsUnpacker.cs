using Migurdex.Core.Interop;
using System.Text.RegularExpressions;

namespace Migurdex.Core.Utils;

public static partial class JsUnpacker
{
    [GeneratedRegex(
        @"eval\s*\(\s*function\s*\(\s*p\s*,\s*a\s*,\s*c\s*,\s*k\s*,\s*e\s*,\s*d\s*\).+?\}\s*\(\s*['""](.*?)['""]\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*['""](.*?)['""]\s*\.split\(['""]\|['""]\)",
        RegexOptions.Singleline)]
    private static partial Regex PackedJsRegex();

    [GeneratedRegex(@"\b[0-9a-zA-Z]+\b")]
    private static partial Regex WordRegex();

    public static string? Unpack(string html)
    {
        if (RustBridge.IsInitialized)
        {
            var rustResult = RustBridge.UnpackJs(html);
            if (!string.IsNullOrEmpty(rustResult))
            {
                return rustResult;
            }
        }

        var match = PackedJsRegex().Match(html);

        if (!match.Success)
        {
            return null;
        }

        var packed = match.Groups[1].Value;
        var radix  = int.Parse(match.Groups[2].Value);
        var words  = match.Groups[4].Value.Split('|');

        return Unpack(packed, radix, words);
    }

    private static string Unpack(string packed, int radix, string[] words)
    {
        var result = WordRegex()
            .Replace(packed,
                     m =>
                     {
                         var value = m.Value;
                         var index = Unbase(value, radix);
                         if (index < words.Length && !string.IsNullOrEmpty(words[index]))
                         {
                             return words[index];
                         }

                         return value;
                     });

        return result;
    }

    private static int Unbase(string value, int radix)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (radix <= 10)
        {
            if (int.TryParse(value, out var result))
            {
                return result;
            }

            return 0;
        }

        var res   = 0;
        var power = 1;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            var digit = alphabet.IndexOf(value[i]);
            if (digit < 0 || digit >= radix)
            {
                return 0;
            }

            res   += digit * power;
            power *= radix;
        }

        return res;
    }
}
