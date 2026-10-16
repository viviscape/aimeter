using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIMeter.Core;

/// <summary>
/// Claude subscription usage (Pro / Max / Team / Enterprise) via the same OAuth client and
/// endpoints Claude Code uses for its /usage screen.
/// </summary>
public sealed class ClaudeProvider : UsageProvider
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.com/cai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string Scopes = "user:profile user:inference user:sessions:claude_code user:mcp_servers";
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";
    private const string OAuthBeta = "oauth-2025-04-20";

    private static string CliCredentialsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public override Provider Kind => Provider.Claude;
    public override string DisplayName => "Claude";

    protected override string CliExpiredMessage =>
        "Claude Code's saved sign-in has expired. Run `claude` once to refresh it, or sign in from AI Meter instead.";

    public override async Task<Account> SignInAsync(CancellationToken ct)
    {
        var (verifier, challenge) = Pkce.Create();
        var state = Pkce.RandomString();
        using var server = new LoopbackServer(0);
        var redirect = $"http://localhost:{server.Port}/callback";

        OpenBrowser(AuthorizeUrl + "?" + Pkce.Query(
            ("code", "true"),
            ("client_id", ClientId),
            ("response_type", "code"),
            ("redirect_uri", redirect),
            ("scope", Scopes),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state)));

        var query = await server.WaitForCallbackAsync("/callback", ct).ConfigureAwait(false);
        if (query["error"] is { } err) throw new InvalidOperationException($"Claude sign-in failed: {query["error_description"] ?? err}");
        if (query["state"] != state) throw new InvalidOperationException("Claude sign-in failed: state mismatch.");
        var code = query["code"] ?? throw new InvalidOperationException("Claude sign-in failed: no authorization code returned.");

        var tokens = await PostTokenAsync(new
        {
            grant_type = "authorization_code",
            code,
            redirect_uri = redirect,
            client_id = ClientId,
            code_verifier = verifier,
            state,
        }, ct).ConfigureAwait(false);

        var account = new Account { Provider = Provider.Claude, Source = AccountSource.OAuth, Label = "Claude" };
        ApplyTokenResponse(account, tokens);
        account.Email = tokens.Get("account").Str("email_address");
        if (account.Email is null)
        {
            try { account.Email = (await GetProfileAsync(account, ct).ConfigureAwait(false)).Get("account").Str("email"); }
            catch { /* email is cosmetic */ }
        }
        return account;
    }

    public override Account LinkCli()
    {
        var a = new Account { Provider = Provider.Claude, Source = AccountSource.ClaudeCli, Label = "Claude (Claude Code)" };
        LoadCliTokens(a);
        if (string.IsNullOrEmpty(a.Tokens.AccessToken))
            throw new InvalidOperationException("Claude Code is not signed in on this PC. Run `claude` and log in first.");
        return a;
    }

    protected override void LoadCliTokens(Account a)
    {
        if (!File.Exists(CliCredentialsPath))
            throw new AuthExpiredException("Claude Code's credentials file was not found. Run `claude` and log in, or sign in from AI Meter.");
        using var doc = JsonDocument.Parse(File.ReadAllText(CliCredentialsPath, Encoding.UTF8));
        var o = doc.RootElement.Get("claudeAiOauth");
        a.Tokens = new TokenSet
        {
            AccessToken = o.Str("accessToken"),
            ExpiresAt = o.Num("expiresAt") is double ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null,
        };
    }

    protected override async Task<bool> RefreshTokensAsync(Account a, CancellationToken ct)
    {
        try
        {
            var tokens = await PostTokenAsync(new
            {
                grant_type = "refresh_token",
                refresh_token = a.Tokens.RefreshToken,
                client_id = ClientId,
                scope = Scopes,
            }, ct).ConfigureAwait(false);
            ApplyTokenResponse(a, tokens);
            return true;
        }
        catch (ApiException e) when (e.Status is 400 or 401 or 403)
        {
            return false;
        }
    }

    private static async Task<JsonElement> PostTokenAsync(object body, CancellationToken ct)
        => await Http.SendJsonAsync(new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = JsonContent.Create(body) }, ct)
            .ConfigureAwait(false);

    private static void ApplyTokenResponse(Account a, JsonElement t)
    {
        a.Tokens.AccessToken = t.Str("access_token");
        a.Tokens.RefreshToken = t.Str("refresh_token") ?? a.Tokens.RefreshToken;
        a.Tokens.ExpiresAt = t.Num("expires_in") is double s ? DateTimeOffset.UtcNow.AddSeconds(s) : null;
    }

    private HttpRequestMessage Get(string url, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", token);
        req.Headers.Add("anthropic-beta", OAuthBeta);
        return req;
    }

    private Task<JsonElement> GetProfileAsync(Account a, CancellationToken ct)
        => GetAuthorizedAsync(a, t => Get(ProfileUrl, t), ct);

    public override async Task<UsageSnapshot> FetchAsync(Account a, CancellationToken ct)
    {
        var usage = await GetAuthorizedAsync(a, t => Get(UsageUrl, t), ct).ConfigureAwait(false);
        JsonElement? profile = null;
        try { profile = await GetProfileAsync(a, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { /* plan name is nice-to-have; usage alone is still useful */ }

        var snap = new UsageSnapshot();
        ParseUsage(usage, snap);
        if (profile is { } p)
        {
            var org = p.Get("organization");
            snap.Email = p.Get("account").Str("email");
            snap.Plan = PlanName(org.Str("organization_type"), org.Str("rate_limit_tier"));
            if (org.Str("subscription_status") is { } status) snap.Status = Text.Humanize(status);
        }
        return snap;
    }

    private static void ParseUsage(JsonElement u, UsageSnapshot snap)
    {
        foreach (var l in ((JsonElement?)u).Items("limits"))
        {
            var kind = l.Str("kind") ?? "limit";
            var scope = l.Get("scope");
            var scopeName = scope.Get("model").Str("display_name")
                            ?? (scope.Get("surface") is { ValueKind: JsonValueKind.String } s ? s.GetString() : scope.Get("surface").Str("display_name"));
            snap.Meters.Add(new UsageMeter
            {
                Name = kind switch
                {
                    "session" => "5-hour session",
                    "weekly_all" => "Weekly · all models",
                    "weekly_scoped" => $"Weekly · {scopeName ?? "scoped"}",
                    _ => Text.Humanize(kind) + (scopeName is null ? "" : $" · {scopeName}"),
                },
                PercentUsed = l.Num("percent") ?? 0,
                ResetsAt = l.Date("resets_at"),
            });
        }

        // Older response shape without the "limits" array.
        if (snap.Meters.Count == 0)
        {
            foreach (var (key, name) in new[]
                     {
                         ("five_hour", "5-hour session"), ("seven_day", "Weekly · all models"),
                         ("seven_day_opus", "Weekly · Opus"), ("seven_day_sonnet", "Weekly · Sonnet"),
                     })
            {
                if (u.Get(key) is { } w && w.Num("utilization") is double pct)
                    snap.Meters.Add(new UsageMeter { Name = name, PercentUsed = pct, ResetsAt = w.Date("resets_at") });
            }
        }

        // Pay-as-you-go "extra usage" credits that kick in after plan limits.
        var spend = u.Get("spend");
        if (spend.Bool("enabled") == true)
        {
            var used = Money(spend.Get("used"));
            var limit = Money(spend.Get("limit"));
            snap.Meters.Add(new UsageMeter
            {
                Name = "Extra usage",
                PercentUsed = spend.Num("percent") ?? 0,
                Detail = used is null ? null : limit is null ? $"{used} spent" : $"{used} of {limit}",
            });
        }
        else if (u.Get("extra_usage").Bool("is_enabled") == true)
        {
            snap.Meters.Add(new UsageMeter { Name = "Extra usage", PercentUsed = u.Get("extra_usage").Num("utilization") ?? 0 });
        }
    }

    private static string? Money(JsonElement? m)
    {
        if (m.Num("amount_minor") is not double minor) return null;
        var exp = (int)(m.Num("exponent") ?? 2);
        var value = (decimal)minor / (decimal)Math.Pow(10, exp);
        var currency = m.Str("currency") ?? "USD";
        return currency == "USD"
            ? value.ToString("C2", CultureInfo.GetCultureInfo("en-US"))
            : $"{value.ToString("N2", CultureInfo.GetCultureInfo("en-US"))} {currency}";
    }

    private static string? PlanName(string? orgType, string? tier)
    {
        string? tierName = tier switch
        {
            null => null,
            _ when tier.Contains("max_20x") => "Max 20x",
            _ when tier.Contains("max_5x") => "Max 5x",
            _ when tier.Contains("max") => "Max",
            _ when tier.Contains("pro") => "Pro",
            _ => null,
        };
        return orgType switch
        {
            "claude_max" => tierName ?? "Max",
            "claude_pro" => "Pro",
            "claude_team" => tierName is null or "Pro" ? "Team" : $"Team · {tierName}",
            "claude_enterprise" => tierName is null or "Pro" ? "Enterprise" : $"Enterprise · {tierName}",
            null => tierName,
            _ => tierName ?? Text.TitleCase(orgType.Replace("claude_", "")),
        };
    }
}
