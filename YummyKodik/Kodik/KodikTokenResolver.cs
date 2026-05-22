using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YummyKodik.Kodik;

public static class KodikTokenResolver
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);

    public const string OnlineModUrl =
        "https://raw.githubusercontent.com/nb557/plugins/refs/heads/main/online_mod.js";

    public const string FallbackScriptUrl =
        "https://kodik-add.com/add-players.min.js?v=2";

    public static async Task<string> ResolveTokenAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        try
        {
            var payload = await GetSecretPayloadAsync(httpClient, cancellationToken).ConfigureAwait(false);
            var token = DecodeSecret(payload.Numbers, payload.DecodeKey);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token.Trim();
            }
        }
        catch
        {
            // Ignore and try fallback source below.
        }

        var scriptResponse = await httpClient.GetAsync(FallbackScriptUrl, cancellationToken).ConfigureAwait(false);
        if (!scriptResponse.IsSuccessStatusCode)
        {
            throw new KodikTokenException($"Failed to download fallback Kodik script. HTTP {(int)scriptResponse.StatusCode}.");
        }

        var scriptBody = await scriptResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(
            scriptBody,
            @"token=(?<t>[^&""'\s]+)",
            RegexOptions.CultureInvariant,
            RegexMatchTimeout);

        if (!match.Success)
        {
            throw new KodikTokenException("Failed to extract fallback Kodik token.");
        }

        var fallbackToken = match.Groups["t"].Value?.Trim();
        if (string.IsNullOrWhiteSpace(fallbackToken))
        {
            throw new KodikTokenException("Failed to extract fallback Kodik token (empty).");
        }

        return fallbackToken;
    }

    private sealed class SecretPayload
    {
        public SecretPayload(int[] numbers, string decodeKey)
        {
            Numbers = numbers;
            DecodeKey = decodeKey;
        }

        public int[] Numbers { get; }
        public string DecodeKey { get; }
    }

    private static async Task<SecretPayload> GetSecretPayloadAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync(OnlineModUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new KodikTokenException($"Failed to download online_mod.js. HTTP {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new KodikTokenException("online_mod.js is empty.");
        }

        var hintIdx = body.LastIndexOf("kodik-api.com/search", StringComparison.Ordinal);
        if (hintIdx >= 0)
        {
            var start = Math.Max(0, hintIdx - 5000);
            var len = Math.Min(body.Length - start, 30000);
            var scope = body.Substring(start, len);

            if (TryExtractSecretPayload(scope, out var payload))
            {
                return payload;
            }
        }

        if (TryExtractSecretPayload(body, out var payloadAll))
        {
            return payloadAll;
        }

        throw new KodikTokenException("Failed to locate Kodik decodeSecret payload in online_mod.js.");
    }

    private static bool TryExtractSecretPayload(string text, out SecretPayload payload)
    {
        payload = null!;

        var regex = new Regex(
            @"(?:(?:var|let|const)\s+token\s*=|token\s*=)\s*(?:Utils\.)?decodeSecret\(\s*\[(?<list>[0-9,\s]+)\]\s*(?:,\s*(?:atob\('(?<b64>[^']+)'\)|""(?<plain1>[^""]+)""|'(?<plain2>[^']+)'))?\s*\)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            RegexMatchTimeout);

        var match = regex.Match(text);
        if (!match.Success)
        {
            regex = new Regex(
                @"(?:Utils\.)?decodeSecret\(\s*\[(?<list>[0-9,\s]+)\]\s*(?:,\s*(?:atob\('(?<b64>[^']+)'\)|""(?<plain1>[^""]+)""|'(?<plain2>[^']+)'))?\s*\)",
                RegexOptions.Singleline | RegexOptions.CultureInvariant,
                RegexMatchTimeout);

            match = regex.Match(text);
            if (!match.Success)
            {
                return false;
            }
        }

        var listStr = match.Groups["list"].Value;
        var numbers = Regex.Matches(listStr, @"\d+", RegexOptions.CultureInvariant, RegexMatchTimeout)
            .Cast<Match>()
            .Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture))
            .ToArray();

        if (numbers.Length == 0)
        {
            return false;
        }

        var decodeKey = "kodik";
        var b64 = match.Groups["b64"]?.Value;
        if (!string.IsNullOrWhiteSpace(b64))
        {
            var decoded = DecodeAtob(b64);
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                decodeKey = decoded;
            }
        }
        else
        {
            var plain1 = match.Groups["plain1"]?.Value;
            var plain2 = match.Groups["plain2"]?.Value;
            var plain = !string.IsNullOrWhiteSpace(plain1) ? plain1 : plain2;
            if (!string.IsNullOrWhiteSpace(plain))
            {
                decodeKey = plain.Trim();
            }
        }

        payload = new SecretPayload(numbers, decodeKey);
        return true;
    }

    private static string DecodeAtob(string b64)
    {
        b64 = (b64 ?? string.Empty).Trim();
        if (b64.Length == 0)
        {
            return string.Empty;
        }

        var pad = (4 - (b64.Length % 4)) % 4;
        if (pad != 0)
        {
            b64 += new string('=', pad);
        }

        try
        {
            var bytes = Convert.FromBase64String(b64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DecodeSecret(int[] numbers, string decodeKey)
    {
        if (numbers == null || numbers.Length == 0)
        {
            return string.Empty;
        }

        decodeKey ??= string.Empty;
        if (decodeKey.Length == 0)
        {
            return string.Empty;
        }

        var hash = Salt("123456789" + decodeKey);
        var hashBuilder = new StringBuilder(hash);
        while (hashBuilder.Length < numbers.Length)
        {
            hashBuilder.Append(hash);
        }

        var expandedHash = hashBuilder.ToString();
        var result = new StringBuilder(numbers.Length);
        for (var i = 0; i < numbers.Length; i++)
        {
            result.Append((char)(numbers[i] ^ expandedHash[i]));
        }

        return result.ToString();
    }

    private static string Salt(string input)
    {
        input ??= string.Empty;

        var hash = 0;
        foreach (var ch in input)
        {
            hash = unchecked((hash << 5) - hash + ch);
        }

        var unsignedHash = unchecked((uint)hash);
        var result = new StringBuilder(10);
        var i = 0;

        for (var j = 29; j >= 0; j -= 3)
        {
            var x = (((unsignedHash >> i) & 7u) << 3) + ((unsignedHash >> j) & 7u);

            int charCode;
            if (x < 26)
            {
                charCode = 97 + (int)x;
            }
            else if (x < 52)
            {
                charCode = 39 + (int)x;
            }
            else
            {
                charCode = (int)x - 4;
            }

            result.Append((char)charCode);
            i += 3;
        }

        return result.ToString();
    }
}
