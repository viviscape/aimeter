using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AIMeter.Core;

public enum Provider { Claude, Codex }

/// <summary>Where an account's credentials come from.</summary>
public enum AccountSource
{
    /// <summary>Signed in through AI Meter; AI Meter owns and refreshes the tokens.</summary>
    OAuth,
    /// <summary>Reads Claude Code's own sign-in (~/.claude/.credentials.json) on every refresh. Never refreshed by AI Meter.</summary>
    ClaudeCli,
    /// <summary>Reads the Codex CLI's own sign-in (~/.codex/auth.json) on every refresh. Never refreshed by AI Meter.</summary>
    CodexCli,
}

public sealed class TokenSet
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>ChatGPT workspace/account id (Codex only).</summary>
    public string? AccountId { get; set; }
}

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Provider Provider { get; set; }
    public AccountSource Source { get; set; }
    public string Label { get; set; } = "";
    public string? Email { get; set; }

    /// <summary>DPAPI-encrypted <see cref="TokenSet"/>; only this form is written to disk.</summary>
    public string? ProtectedTokens { get; set; }

    [JsonIgnore] public TokenSet Tokens { get; set; } = new();
}

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 5;
    public bool AlwaysOnTop { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
}

public sealed class UsageMeter
{
    public required string Name { get; init; }
    /// <summary>Percent of the limit used (0–100, may exceed 100).</summary>
    public double PercentUsed { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    /// <summary>Optional extra detail, e.g. "$12.40 of $50.00".</summary>
    public string? Detail { get; init; }
}

public sealed class UsageSnapshot
{
    public string? Email { get; set; }
    public string? Plan { get; set; }
    public string? Status { get; set; }
    public List<UsageMeter> Meters { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>The stored sign-in is no longer valid and the user has to act.</summary>
public sealed class AuthExpiredException(string message) : Exception(message);

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
