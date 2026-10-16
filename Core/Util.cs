using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIMeter.Core;

internal static class Http
{
    public const string UserAgent = "AIMeter/1.0 (Windows)";

    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<JsonElement> SendJsonAsync(HttpRequestMessage req, CancellationToken ct)
    {
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await Client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new ApiException((int)res.StatusCode, $"HTTP {(int)res.StatusCode} from {req.RequestUri?.Host}: {Trim(body)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static string Trim(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }
}

internal static class Pkce
{
    public static string RandomString(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static (string Verifier, string Challenge) Create()
    {
        var verifier = RandomString(32);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Query(params (string Key, string Value)[] pairs)
        => string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
}

internal static class Jwt
{
    public static JsonElement? Payload(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var p = parts[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(p));
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    public static DateTimeOffset? Expiry(string? jwt)
        => Payload(jwt)?.Num("exp") is double exp ? DateTimeOffset.FromUnixTimeSeconds((long)exp) : null;
}

internal static class Json
{
    public static JsonElement? Get(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static JsonElement? Get(this JsonElement? e, string name) => e?.Get(name);

    public static string? Str(this JsonElement? e, string name) => e?.Get(name) is { } v ? Raw(v) : null;
    public static string? Str(this JsonElement e, string name) => ((JsonElement?)e).Str(name);

    public static double? Num(this JsonElement? e, string name)
    {
        if (e?.Get(name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }
    public static double? Num(this JsonElement e, string name) => ((JsonElement?)e).Num(name);

    public static bool? Bool(this JsonElement? e, string name)
        => e?.Get(name) is { } v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    public static bool? Bool(this JsonElement e, string name) => ((JsonElement?)e).Bool(name);

    public static DateTimeOffset? Date(this JsonElement? e, string name)
    {
        if (e?.Get(name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number) return DateTimeOffset.FromUnixTimeSeconds((long)v.GetDouble());
        return DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }
    public static DateTimeOffset? Date(this JsonElement e, string name) => ((JsonElement?)e).Date(name);

    public static IEnumerable<JsonElement> Items(this JsonElement? e, string name)
        => e?.Get(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Raw(JsonElement v) => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
}

internal static class Text
{
    private static readonly TextInfo En = CultureInfo.GetCultureInfo("en-US").TextInfo;

    /// <summary>"past_due" → "Past due", "weekly_all" → "Weekly all".</summary>
    public static string Humanize(string raw)
    {
        var s = raw.Replace('_', ' ').Replace('-', ' ').Trim();
        return s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
    }

    public static string TitleCase(string raw) => En.ToTitleCase(raw.Replace('_', ' ').Replace('-', ' ').Trim().ToLowerInvariant());
}
