# Tide

A lightweight, modern network monitor for Windows. See which apps are using your connection right now, how much they've used over time, and cut any of them off from the internet in one click.

![Tide icon](src/Tide/Assets/tide.ico)

## Features

- **Live per-app usage** – download and upload speed for every app, updated every second, with a two-minute live graph for all traffic or any single app (click a row).
- **History** – hourly, daily, weekly and monthly totals per app, with a stacked chart and a ranked app list. Step back through previous days, weeks and months.
- **One-click blocking** – the Block button adds inbound and outbound Windows Firewall rules for that program. Rules are grouped under **"Tide"** in Windows Firewall, so they keep working even when Tide is closed. Unblock from the Live page or the Blocked apps page.
- **Alerts** – a notification when an app goes online for the first time, or when an app moves more than a set amount of data in a minute (default 250 MB). Block straight from the alert list.
- **Themes** – Tide, Dark, Dusk and Light, switchable instantly.
- **Built-in updater** – checks GitHub Releases at startup and twice a day, verifies the download's SHA-256, swaps the .exe in place and restarts. Nothing installs without you clicking **Update**.
- **Tray app** – closing the window keeps Tide monitoring in the tray; the tray tooltip shows current speeds.
- **Start with Windows** – uses a Task Scheduler task with highest privileges, so there's no UAC prompt at every sign-in.
- **Light on resources** – no background services, no drivers, no third-party packages. Graphs are drawn directly and history is a few KB per day.

## Download

Grab the latest release from **[Releases](https://github.com/larosa023/Tide/releases/latest)**:

| File | Size | Needs |
| --- | --- | --- |
| `Tide.exe` | ~75 MB | Nothing – runs on any 64-bit Windows 10/11 |
| `Tide-small.exe` | ~0.5 MB | The [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |

There's no installer: put the .exe wherever you like (for example `C:\Program Files\Tide\`) and run it. Tide asks for administrator rights because reading per-app network activity and writing firewall rules both require them.

> Windows SmartScreen may warn about an unrecognised app the first time, because the .exe isn't code-signed. Choose **More info → Run anyway**.

## How it works

| Part | How |
| --- | --- |
| Per-app traffic | A real-time ETW session on the `Microsoft-Windows-Kernel-Network` provider (the same source Resource Monitor uses). Every TCP/UDP send and receive is attributed to its process, then grouped by executable path. |
| Blocking | Windows Firewall rules via the `HNetCfg.FwPolicy2` COM API – one inbound and one outbound block rule per program. |
| History | One JSON file per day in `%LocalAppData%\Tide\History`, with hourly buckets per app. Kept for 365 days by default. |
| Updates | `GET api.github.com/repos/larosa023/Tide/releases/latest`, compare versions, download the matching asset, check `*.sha256`, rename the running exe to `.old`, move the new one in, restart. |

A few things worth knowing:

- Apps that share one program (for example Windows services running inside `svchost.exe`) appear as one entry, and blocking that entry blocks all of them. Tide warns you before blocking anything under `C:\Windows`.
- Blocking stops new connections immediately; a connection an app already had open can continue until it closes.
- Local (localhost) traffic is ignored by default; turn that off in Settings if you want to see it.
- All data stays on your PC. The only network request Tide makes is the GitHub update check, which you can switch off.

## Building

Requires the .NET 10 SDK on Windows.

```powershell
dotnet run --project src/Tide          # debug run (from an elevated terminal)

dotnet publish src/Tide -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o out
```

## Releasing

Bump `<Version>` in `src/Tide/Tide.csproj`, commit, then tag:

```powershell
git tag v1.0.1
git push origin v1.0.1
```

The **Build** workflow publishes both executables plus their `.sha256` files to a GitHub release. Running copies of Tide pick it up on their next update check.

## License

[MIT](LICENSE)
