using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIMeter.Core;

/// <summary>Persists settings and accounts to %LOCALAPPDATA%\AIMeter\state.json (tokens DPAPI-encrypted).</summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIMeter");

    private static string FilePath => Path.Combine(Folder, "state.json");

    public AppSettings Settings { get; private set; } = new();
    public List<Account> Accounts { get; private set; } = new();

    private sealed class StateFile
    {
        public AppSettings Settings { get; set; } = new();
        public List<Account> Accounts { get; set; } = new();
    }

    public void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(FilePath, Encoding.UTF8), JsonOptions);
            if (state is null) return;
            Settings = state.Settings ?? new AppSettings();
            Accounts = state.Accounts ?? new List<Account>();
            foreach (var a in Accounts)
            {
                try
                {
                    a.Tokens = a.ProtectedTokens is null
                        ? new TokenSet()
                        : JsonSerializer.Deserialize<TokenSet>(Dpapi.Unprotect(a.ProtectedTokens), JsonOptions) ?? new TokenSet();
                }
                catch
                {
                    // Encrypted under another Windows user/machine: the account stays listed and asks to sign in again.
                    a.Tokens = new TokenSet();
                }
            }
        }
        catch
        {
            // Corrupt file: keep a copy for inspection and start fresh rather than refusing to launch.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch { }
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        foreach (var a in Accounts)
            a.ProtectedTokens = Dpapi.Protect(JsonSerializer.Serialize(a.Tokens, JsonOptions));

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new StateFile { Settings = Settings, Accounts = Accounts }, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
