using System;
using System.Linq;

namespace YummyKodik.Util;

public static class StringTokenParser
{
    private static readonly char[] DefaultSeparators = { '|', ',', ';' };

    public static string[] ParseTokens(string? input)
    {
        var s = (input ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return Array.Empty<string>();
        }

        return s.Split(DefaultSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
    }
}
