# AI Meter

A small Windows desktop widget and tray icon that shows how much of your **Claude** and **Codex (ChatGPT)** subscription limits you have left, across as many accounts as you like.

For each account it shows:

- **Plan**, e.g. `Max 5x`, `Team · Max 5x`, `Plus`, `Pro`, `Business`
- **Subscription status**: `Active`, `Past due`, and so on (Claude), or `Limit reached`
- **Each usage window** with a bar, the percent left, the percent used, and when it resets:
  - Claude: 5-hour session, weekly (all models), and weekly per model, plus extra-usage spend if it's turned on
  - Codex: the 5-hour and/or weekly windows, code review limits, and the credit spend limit and balance

Bars turn amber at 70% used and red at 90%. Hovering over the tray icon shows the lowest remaining percentage for each account.

## Installing (for your team)

Give people `artifacts\AIMeter-<version>-x64.msi`. They double-click it and it installs. No admin rights or .NET install needed.

- Installs for the current user only, to `%LOCALAPPDATA%\Programs\AI Meter`. Adds a Start menu shortcut, turns on start-at-sign-in, and opens the widget when setup finishes.
- Each person then signs in to their own accounts under ⚙ → Accounts & settings.
- **"Windows protected your PC" warning:** the MSI isn't code-signed yet, so SmartScreen shows this the first time. Click **More info → Run anyway**. To get rid of the warning, build with a code-signing certificate (see below).
- **Updating:** install a newer MSI over the old one. A running copy is closed and restarted automatically, and accounts and settings are kept.
- **Uninstalling:** Settings → Apps → AI Meter. This leaves `%LOCALAPPDATA%\AIMeter` (saved sign-ins) in place; delete that folder to remove them too.
- **Silent / Intune / scripted installs:** deploy in the *user* context, e.g.

  ```powershell
  msiexec /i AIMeter-1.0.0-x64.msi /qn                 # silent; starts the widget
  msiexec /i AIMeter-1.0.0-x64.msi /qn LAUNCHAPP=0 AUTOSTART=0
  msiexec /x AIMeter-1.0.0-x64.msi /qn                 # silent uninstall
  ```

  `AUTOSTART` sets start-at-sign-in (default on). On upgrades, each person's own choice from the settings window is kept.

## Building the installer

```powershell
.\build\build-installer.ps1                       # version from AIMeter.csproj
.\build\build-installer.ps1 -Version 1.1.0        # bump for every release, or upgrades won't happen
.\build\build-installer.ps1 -Version 1.1.0 -CertThumbprint <sha1>   # code-sign exe + MSI
```

This publishes a self-contained single-file `AIMeter.exe`, then builds the MSI with WiX v5. WiX comes in as a NuGet SDK, so you only need the .NET SDK. Output goes to `artifacts\`. The installer source is `installer\Package.wxs`, and `build\make-icon.ps1` regenerates `assets\AIMeter.ico`.

## Run from source

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\AIMeter.exe
```

Or build a single exe (it needs the .NET 8 Desktop Runtime installed):

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
.\publish\AIMeter.exe
```

Only one copy runs at a time. Launching it again just brings the widget back.

## Adding accounts

Open **⚙ → Accounts & settings**. There are two ways to add an account:

| Option | What happens | Good for |
|---|---|---|
| **Sign in to Claude…** / **Sign in to ChatGPT (Codex)…** | Your browser opens the real Anthropic/OpenAI sign-in. AI Meter gets its own sign-in, which it keeps fresh by itself. | Watching any account, including ones you don't use the CLI with. Add as many as you like. |
| **Use Claude Code's sign-in** / **Use Codex CLI's sign-in** | Reads the CLI's saved sign-in (`~/.claude/.credentials.json`, `~/.codex/auth.json`) on every refresh. AI Meter never refreshes or changes those files. | Zero setup on a machine where you already use the CLI. |

Tips:
- To add a *second* account for the same service, use a private browser window, or sign out in the browser first. Otherwise the browser just signs you in as the account you're already using.
- Claude Code's own sign-in only lasts about 8 hours unless Claude Code renews it. If you don't use Claude Code every day, a CLI-linked Claude account will show "expired" until you run `claude` again. Signing in through AI Meter avoids this.
- Codex sign-in always uses `http://localhost:1455`, the only callback OpenAI allows for this client. Close any `codex login` that's still running before you start.

## Widget controls

- Drag the header to move the widget. It remembers where you put it.
- ⟳ refreshes now, ⚙ opens accounts & settings, — hides the widget. Click the tray icon to bring it back.
- Right-click the widget or the tray icon for more options: always on top, refresh, exit.
- Settings: how often to refresh (1 minute to 1 hour, default 5 minutes), keep on top, and start with Windows (a per-user `HKCU\…\Run` entry, so no admin rights are needed).

## Where data lives

- `%LOCALAPPDATA%\AIMeter\state.json` holds settings and accounts. Tokens are encrypted with Windows DPAPI, so only your Windows user on this PC can read them.
- `%LOCALAPPDATA%\AIMeter\error.log` records unexpected errors.
- AI Meter only connects to Anthropic (`api.anthropic.com`, `platform.claude.com`, `claude.com`) and OpenAI (`auth.openai.com`, `chatgpt.com`).

## How it works / caveats

Neither company publishes a usage API for subscription plans. AI Meter calls the same endpoints the official tools use for their own usage screens:

- Claude: `GET api.anthropic.com/api/oauth/usage` and `/api/oauth/profile`, using Claude Code's OAuth client. This is the data behind Claude Code's `/usage`.
- Codex: `GET chatgpt.com/backend-api/wham/usage`, using the Codex CLI's OAuth client. This is the data behind Codex's `/status`.

Because these endpoints are undocumented, they can change without notice. The parsers don't fail on unexpected fields: new limit types appear under their raw names, and anything they can't read is skipped.

## Layout

```
Core/   provider clients (ClaudeProvider, CodexProvider), OAuth loopback + PKCE, DPAPI store
UI/     WPF widget (MainWindow), accounts/settings window, view models, tray icon art
App.*   app lifetime, tray icon, refresh timer, single-instance handling
```
