using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIMeter.Core;

public abstract class UsageProvider
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public abstract Provider Kind { get; }
    public abstract string DisplayName { get; }

    /// <summary>Opens the browser, waits for the OAuth redirect, and returns a new signed-in account.</summary>
    public abstract Task<Account> SignInAsync(CancellationToken ct);

    /// <summary>Returns an account linked to the provider's CLI sign-in, or throws if the CLI is not signed in.</summary>
    public abstract Account LinkCli();

    public abstract Task<UsageSnapshot> FetchAsync(Account account, CancellationToken ct);

    /// <summary>Exchanges the refresh token for a new access token; returns false if the refresh token is rejected.</summary>
    protected abstract Task<bool> RefreshTokensAsync(Account account, CancellationToken ct);

    /// <summary>Loads the CLI-owned token into <paramref name="account"/> (CLI-linked accounts only).</summary>
    protected abstract void LoadCliTokens(Account account);

    protected abstract string CliExpiredMessage { get; }

    /// <summary>Set when tokens changed and the store should be saved.</summary>
    public bool TokensChanged { get; set; }

    protected async Task<string> GetAccessTokenAsync(Account a, bool forceRefresh, CancellationToken ct)
    {
        if (a.Source != AccountSource.OAuth)
        {
            LoadCliTokens(a);
            if (string.IsNullOrEmpty(a.Tokens.AccessToken) || a.Tokens.ExpiresAt <= DateTimeOffset.UtcNow || forceRefresh)
                throw new AuthExpiredException(CliExpiredMessage);
            return a.Tokens.AccessToken!;
        }

        if (string.IsNullOrEmpty(a.Tokens.RefreshToken) && string.IsNullOrEmpty(a.Tokens.AccessToken))
            throw new AuthExpiredException("Not signed in. Sign in again to resume monitoring.");

        var stale = a.Tokens.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow.AddMinutes(2);
        if (forceRefresh || stale || string.IsNullOrEmpty(a.Tokens.AccessToken))
        {
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrEmpty(a.Tokens.RefreshToken) || !await RefreshTokensAsync(a, ct).ConfigureAwait(false))
                    throw new AuthExpiredException("Sign-in expired. Sign in again to resume monitoring.");
                TokensChanged = true;
            }
            finally { _refreshLock.Release(); }
        }
        return a.Tokens.AccessToken!;
    }

    /// <summary>GETs a JSON endpoint, refreshing the access token once on HTTP 401.</summary>
    protected async Task<JsonElement> GetAuthorizedAsync(Account a, Func<string, HttpRequestMessage> build, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(a, false, ct).ConfigureAwait(false);
        try
        {
            return await Http.SendJsonAsync(build(token), ct).ConfigureAwait(false);
        }
        catch (ApiException e) when (e.Status == 401)
        {
            token = await GetAccessTokenAsync(a, true, ct).ConfigureAwait(false);
            try { return await Http.SendJsonAsync(build(token), ct).ConfigureAwait(false); }
            catch (ApiException e2) when (e2.Status is 401 or 403)
            {
                throw new AuthExpiredException("Sign-in was rejected. Sign in again to resume monitoring.");
            }
        }
    }

    protected static void OpenBrowser(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
