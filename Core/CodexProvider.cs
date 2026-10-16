using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIMeter.Core;

/// <summary>
/// Codex usage for ChatGPT plans (Plus / Pro / Business / Enterprise) via the same OAuth client and
/// endpoint the Codex CLI uses for its /status rate-limit display.
/// </summary>
public sealed class CodexProvider : UsageProvider
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string Issuer = "https://auth.openai.com";
    private const string Scopes = "openid profile email offline_access";
    // The Codex OAuth client only accepts this exact redirect, so the port is fixed.
    private const int CallbackPort = 1455;
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private static string CliAuthPath => Path.Combine(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        "auth.json");

    public override Provider Kind => Provider.Codex;
    public override string DisplayName => "Codex";

    protected override string CliExpiredMessage =>
        "The Codex CLI's saved sign-in has expired. Run `codex` once to refresh it, or sign in from AI Meter instead.";

    public override async Task<Account> SignInAsync(CancellationToken ct)
    {
        var (verifier, challenge) = Pkce.Create();
        var state = Pkce.RandomString();
        LoopbackServer server;
        try { server = new LoopbackServer(CallbackPort); }
        catch (SocketException)
        {
            throw new InvalidOperationException(
                $"Port {CallbackPort} is in use (probably a running `codex login`). Close it and try again.");
        }

        using (server)
        {
            var redirect = $"http://localhost:{CallbackPort}/auth/callback";
            OpenBrowser($"{Issuer}/oauth/authorize?" + Pkce.Query(
                ("response_type", "code"),
                ("client_id", ClientId),
                ("redirect_uri", redirect),
                ("scope", Scopes),
                ("code_challenge", challenge),
                ("code_challenge_method", "S256"),
                ("id_token_add_organizations", "true"),
                ("codex_cli_simplified_flow", "true"),
                ("state", state),
                ("originator", "codex_cli_rs")));

            var query = await server.WaitForCallbackAsync("/auth/callback", ct).ConfigureAwait(false);
            if (query["error"] is { } err) throw new InvalidOperationException($"ChatGPT sign-in failed: {query["error_description"] ?? err}");
            if (query["state"] != state) throw new InvalidOperationException("ChatGPT sign-in failed: state mismatch.");
            var code = query["code"] ?? throw new InvalidOperationException("ChatGPT sign-in failed: no authorization code returned.");

            var tokens = await Http.SendJsonAsync(new HttpRequestMessage(HttpMethod.Post, $"{Issuer}/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirect,
                    ["client_id"] = ClientId,
                    ["code_verifier"] = verifier,
                }),
            }, ct).ConfigureAwait(false);

            var account = new Account { Provider = Provider.Codex, Source = AccountSource.OAuth, Label = "Codex" };
            ApplyTokenResponse(account, tokens);
            return account;
        }
    }

    public override Account LinkCli()
    {
        var a = new Account { Provider = Provider.Codex, Source = AccountSource.CodexCli, Label = "Codex (Codex CLI)" };
        LoadCliTokens(a);
        if (string.IsNullOrEmpty(a.Tokens.AccessToken))
            throw new InvalidOperationException("The Codex CLI is not signed in with ChatGPT on this PC. Run `codex login` first.");
        return a;
    }

    protected override void LoadCliTokens(Account a)
    {
        if (!File.Exists(CliAuthPath))
            throw new AuthExpiredException("The Codex CLI's auth.json was not found. Run `codex login`, or sign in from AI Meter.");
        using var doc = JsonDocument.Parse(File.ReadAllText(CliAuthPath, Encoding.UTF8));
        var t = doc.RootElement.Get("tokens");
        var access = t.Str("access_token");
        a.Tokens = new TokenSet
        {
            AccessToken = access,
            AccountId = t.Str("account_id"),
            ExpiresAt = Jwt.Expiry(access),
        };
        a.Email ??= Jwt.Payload(t.Str("id_token")).Str("email");
    }

    protected override async Task<bool> RefreshTokensAsync(Account a, CancellationToken ct)
    {
        try
        {
            var tokens = await Http.SendJsonAsync(new HttpRequestMessage(HttpMethod.Post, $"{Issuer}/oauth/token")
            {
                Content = JsonContent.Create(new
                {
                    client_id = ClientId,
                    grant_type = "refresh_token",
                    refresh_token = a.Tokens.RefreshToken,
                    scope = "openid profile email",
                }),
            }, ct).ConfigureAwait(false);
            ApplyTokenResponse(a, tokens);
            return true;
        }
        catch (ApiException e) when (e.Status is 400 or 401 or 403)
        {
            return false;
        }
    }

    private static void ApplyTokenResponse(Account a, JsonElement t)
    {
        a.Tokens.AccessToken = t.Str("access_token") ?? a.Tokens.AccessToken;
        a.Tokens.RefreshToken = t.Str("refresh_token") ?? a.Tokens.RefreshToken;
        a.Tokens.ExpiresAt = Jwt.Expiry(a.Tokens.AccessToken)
                             ?? (t.Num("expires_in") is double s ? DateTimeOffset.UtcNow.AddSeconds(s) : null);

        var claims = Jwt.Payload(t.Str("id_token"));
        if (claims is not null)
        {
            a.Email = claims.Str("email") ?? a.Email;
            a.Tokens.AccountId = claims.Get("https://api.openai.com/auth").Str("chatgpt_account_id") ?? a.Tokens.AccountId;
        }
    }

    public override async Task<UsageSnapshot> FetchAsync(Account a, CancellationToken ct)
    {
        var u = await GetAuthorizedAsync(a, token =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new("Bearer", token);
            if (a.Tokens.AccountId is { } id) req.Headers.Add("ChatGPT-Account-Id", id);
            return req;
        }, ct).ConfigureAwait(false);

        var snap = new UsageSnapshot
        {
            Email = u.Str("email"),
            Plan = u.Str("plan_type") is { } plan ? PlanName(plan) : null,
        };

        AddWindows(snap, u.Get("rate_limit"), null);
        AddWindows(snap, u.Get("code_review_rate_limit"), "Code review");

        var extra = u.Get("additional_rate_limits");
        if (extra is { ValueKind: JsonValueKind.Array } arr)
        {
            foreach (var item in arr.EnumerateArray())
                AddWindows(snap, item.Get("rate_limit") ?? item, item.Str("limit_name") ?? item.Str("metered_feature") ?? "Additional");
        }
        else if (extra is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var prop in obj.EnumerateObject())
                AddWindows(snap, prop.Value, Text.Humanize(prop.Name));
        }

        var spend = u.Get("spend_control").Get("individual_limit");
        if (spend is not null && spend.Num("limit") is double cap)
        {
            snap.Meters.Add(new UsageMeter
            {
                Name = "Credit spend limit",
                PercentUsed = spend.Num("used_percent") ?? 0,
                ResetsAt = spend.Date("reset_at"),
                Detail = $"{spend.Num("used") ?? 0:0.#} of {cap:0.#} credits",
            });
        }

        var credits = u.Get("credits");
        if (credits.Bool("unlimited") == true) snap.Notes.Add("Unlimited credits");
        else if (credits.Bool("has_credits") == true && credits.Str("balance") is { } bal) snap.Notes.Add($"Credit balance: {bal}");

        if (u.Get("rate_limit").Bool("limit_reached") == true || u.Str("rate_limit_reached_type") is not null)
            snap.Status = "Limit reached";
        else if (u.Get("spend_control").Bool("reached") == true)
            snap.Notes.Add("Credit spend limit reached — usage beyond plan limits is paused");

        return snap;
    }

    private static void AddWindows(UsageSnapshot snap, JsonElement? rl, string? prefix)
    {
        if (rl is null) return;
        foreach (var key in new[] { "primary_window", "secondary_window" })
        {
            var w = rl.Get(key);
            if (w is null) continue;
            var name = WindowName(w.Num("limit_window_seconds"));
            var reset = w.Date("reset_at")
                        ?? (w.Num("reset_after_seconds") is double after ? DateTimeOffset.UtcNow.AddSeconds(after) : null);
            snap.Meters.Add(new UsageMeter
            {
                Name = prefix is null ? name : $"{prefix} · {name}",
                PercentUsed = w.Num("used_percent") ?? 0,
                ResetsAt = reset,
            });
        }
    }

    private static string WindowName(double? seconds) => seconds switch
    {
        null => "Usage",
        >= 6 * 86400 and <= 8 * 86400 => "Weekly",
        >= 86400 => $"{Math.Round(seconds.Value / 86400)}-day",
        _ => $"{Math.Round(seconds.Value / 3600)}-hour",
    };

    private static string PlanName(string raw) => raw switch
    {
        "free" => "Free",
        "plus" => "Plus",
        "pro" => "Pro",
        "team" => "Team",
        "business" => "Business",
        "enterprise" => "Enterprise",
        "edu" => "Edu",
        _ => Text.TitleCase(raw.Replace("self_serve_", "")),
    };
}
